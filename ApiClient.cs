using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CICUser;

/// <summary>
/// 与自建服务端通信的客户端。报文内容由 <see cref="MessageTemplate"/> 驱动，
/// 支持字段级自定义，不再写死字段名。
/// </summary>
public class ApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly PluginSettings _settings;
    private readonly bool _ownsClient;

    /// <summary>请求超时的兜底值（秒）。实际值取自配置。</summary>
    private const int DefaultTimeoutSeconds = 15;

    /// <summary>重试的基础退避时长（毫秒）。每次重试翻倍。</summary>
    private const int BaseRetryDelayMs = 400;

    /// <summary>单次请求的超时时间上限，防止配置异常导致长时间挂起。</summary>
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(120);

    public ApiClient(HttpClient httpClient, PluginSettings settings, bool ownsClient = false)
    {
        _httpClient = httpClient;
        _settings = settings;
        _ownsClient = ownsClient;

        // 超时以配置为准，并夹在合理区间内
        var seconds = settings.RequestTimeoutSeconds is >= 3 and <= 120
            ? settings.RequestTimeoutSeconds
            : DefaultTimeoutSeconds;
        var timeout = TimeSpan.FromSeconds(seconds);

        if (_httpClient.Timeout == Timeout.InfiniteTimeSpan || _httpClient.Timeout > MaxTimeout)
        {
            _httpClient.Timeout = timeout;
        }
    }

    private string GetBaseUrl() => (_settings.ServerUrl ?? "").TrimEnd('/');

    /// <summary>
    /// 校验连接参数，返回错误描述；返回 null 表示通过。
    /// </summary>
    private string? ValidateConnection()
    {
        if (string.IsNullOrWhiteSpace(_settings.ServerUrl))
        {
            return "服务器地址为空";
        }

        if (string.IsNullOrWhiteSpace(_settings.DeviceId))
        {
            return "设备ID为空";
        }

        // 开了签名却没有密钥属于配置错误：继续发出去也只会被服务端拒绝，
        // 不如在本地就报清楚，省去一轮无谓的网络往返。
        if (_settings.SignatureEnabled && string.IsNullOrWhiteSpace(_settings.DeviceSecret))
        {
            return "已启用请求签名，但共享密钥为空";
        }

        return null;
    }

    /// <summary>
    /// 申请设备授权。接口路径与报文字段名均可在高级设置中自定义。
    /// </summary>
    public async Task<ApiResponse> RequestAuthAsync(string requestReason = "同步课程表和课堂状态",
        CancellationToken cancellationToken = default)
    {
        var error = ValidateConnection();
        if (error != null)
        {
            return ApiResponse.Fail($"缺少必要参数：{error}");
        }

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [_settings.AuthDeviceIdField] = _settings.DeviceId,
            [_settings.AuthDeviceNameField] = _settings.DeviceName,
            [_settings.AuthClassNameField] = _settings.ClassName,
            ["RequestReason"] = requestReason
        };

        return await PostJsonAsync(_settings.AuthRequestEndpoint, payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 查询授权结果。接口路径可在高级设置中自定义。
    /// </summary>
    public async Task<ApiResponse> QueryAuthAsync(CancellationToken cancellationToken = default)
    {
        var error = ValidateConnection();
        if (error != null)
        {
            return ApiResponse.Fail($"缺少必要参数：{error}");
        }

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [_settings.AuthDeviceIdField] = _settings.DeviceId
        };

        return await PostJsonAsync(_settings.AuthQueryEndpoint, payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 上报当日课表。报文按课表模板构建，课表内容按用户选定的格式预设
    /// 转换后注入到模板指定的键名中。
    /// </summary>
    public async Task<ApiResponse> UpdateScheduleAsync(
        IReadOnlyDictionary<string, string[]> schedule,
        IReadOnlyDictionary<string, object?> runtimeValues,
        CancellationToken cancellationToken = default)
    {
        var error = ValidateConnection();
        if (error != null)
        {
            return ApiResponse.Fail($"缺少必要参数：{error}");
        }

        var template = _settings.ScheduleTemplate;
        var globalValues = _settings.GetGlobalValues();
        var issues = new List<string>();

        // 内部中间表示 → 用户选定的格式预设。
        // 把全局配置一并传进去，课表项字段才能引用设备ID、班级名称等。
        // 格式以活动模板的指定为准，模板没指定时才用全局设置。
        var format = _settings.ResolveScheduleFormat();
        var data = ScheduleFormatter.Convert(
            schedule, format, _settings.ScheduleItemFields,
            _settings.ScheduleIndexKeyPrefix, globalValues);

        var json = MessageBuilder.Build(
            template.ScheduleFields,
            runtimeValues,
            globalValues,
            KeywordScope.Schedule,
            template.ScheduleDataKey,
            data,
            issues);

        LogBuildIssues("课表", issues);
        return await PostRawAsync(template.ScheduleEndpoint, json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 上报课堂状态。报文按状态模板构建。
    /// </summary>
    public async Task<ApiResponse> SubmitStatusAsync(
        IReadOnlyDictionary<string, object?> runtimeValues,
        CancellationToken cancellationToken = default)
    {
        var error = ValidateConnection();
        if (error != null)
        {
            return ApiResponse.Fail($"缺少必要参数：{error}");
        }

        var template = _settings.StatusTemplate;
        var issues = new List<string>();
        var json = MessageBuilder.Build(
            template.StatusFields,
            runtimeValues,
            _settings.GetGlobalValues(),
            KeywordScope.Status,
            issues: issues);

        LogBuildIssues("状态", issues);

        return await PostRawAsync(template.StatusEndpoint, json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把构建报文时收集到的问题写进日志。
    /// </summary>
    /// <remarks>
    /// 这些问题不影响报文结构完整性（取值失败会回退为原文本），
    /// 但通常意味着模板配错了，写进日志便于用户按图索骥地修正。
    /// 正常情况下列表为空，不产生任何日志噪音。
    /// </remarks>
    /// <param name="scene">场景名，如「状态」「课表」。</param>
    /// <param name="issues">构建期收集的问题。</param>
    private static void LogBuildIssues(string scene, List<string> issues)
    {
        if (issues == null || issues.Count == 0)
        {
            return;
        }

        Logger.Warning($"{scene}报文构建提示：{string.Join("；", issues)}");
    }

    /// <summary>
    /// 向服务端注册本设备并提交新密钥（TLS 之上的一次性密钥分发）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是<b>唯一一次</b>密钥经过网络的调用，因此要求 https ——
    /// 整条链路的防护上限就等于这一步的传输层是否可信。
    /// 注册成功后密钥只留在本机，之后所有上报改用签名头，密钥再不出网。
    /// </para>
    /// <para>
    /// 请求体里的 Secret 同时充当<b>幂等令牌</b>：网络超时导致的重试
    /// 会带着同一个 Secret 再来一次，服务端据此返回成功而非「已注册」错误，
    /// 设备不会把自己锁死。
    /// </para>
    /// <para>
    /// 本方法刻意<b>不</b>走 <see cref="ValidateConnection"/>：注册时本就还没有密钥，
    /// 沿用那套校验会被「已启用签名但密钥为空」直接挡下。
    /// </para>
    /// </remarks>
    /// <param name="secret">本机新生成的密钥。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>服务端响应。</returns>
    public async Task<ApiResponse> EnrollAsync(
        string secret, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.ServerUrl))
        {
            return ApiResponse.Fail("服务器地址为空");
        }

        if (string.IsNullOrWhiteSpace(_settings.DeviceId))
        {
            return ApiResponse.Fail("设备ID为空");
        }

        if (string.IsNullOrWhiteSpace(secret))
        {
            return ApiResponse.Fail("待注册的密钥为空");
        }

        var path = string.IsNullOrWhiteSpace(_settings.EnrollEndpoint)
            ? "/class/api/enroll"
            : _settings.EnrollEndpoint;

        // 注册是唯一一次把密钥交给服务端的调用，因此必须走 https——
        // 明文通道下这一步等于把密钥直接摊在网上，之后所有签名都形同虚设。
        // 注意这条限制<b>只针对注册</b>：普通上报允许明文，
        // 那是刻意保留的「手输密钥 + 服务端未上 TLS」兼容档位。
        var enrollUrl = path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? path
            : GetBaseUrl() + path;

        if (!enrollUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return ApiResponse.Fail(
                "自动注册必须使用 https:// 地址：注册会把密钥提交给服务端，明文传输将使其失去防护意义。");
        }

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["DeviceId"] = _settings.DeviceId,
            ["DeviceName"] = _settings.DeviceName,
            ["ClassName"] = _settings.ClassName,
            ["Secret"] = secret,
            ["ClientVersion"] = typeof(ApiClient).Assembly.GetName().Version?.ToString() ?? "",
            ["Scheme"] = RequestSigner.SchemeVersion
        };

        return await PostJsonAsync(path, payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ApiResponse> PostJsonAsync(string path, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        return await PostRawAsync(path, json, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiResponse> PostRawAsync(string path, string json, CancellationToken cancellationToken = default)
    {
        // 明文 HTTP 防护：未开启「允许明文 HTTP」时拒绝发送，
        // 避免用户误把敏感的上报内容发往未加密的地址。
        // 校验对象是拼好的完整地址，端点里塞绝对地址也躲不过。
        var insecureError = CheckInsecureHttpFor(path);
        if (insecureError != null)
        {
            return ApiResponse.Fail(insecureError);
        }

        var maxRetry = _settings.MaxRetryCount is >= 0 and <= 5 ? _settings.MaxRetryCount : 2;
        var timeoutSeconds = TimeoutSeconds;

        ApiResponse last = ApiResponse.Fail("请求未执行");

        // 首次 + 最多 maxRetry 次重试
        for (var attempt = 0; attempt <= maxRetry; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ApiResponse.Fail("请求已取消");
            }

            // 重试前按指数退避等待，避免在服务端故障时形成请求风暴
            if (attempt > 0)
            {
                var delay = BaseRetryDelayMs * (1 << (attempt - 1));
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return ApiResponse.Fail("请求已取消");
                }
            }

            var (response, retryable) = await TrySendOnceAsync(path, json, timeoutSeconds, cancellationToken)
                .ConfigureAwait(false);

            last = response;

            if (response.IsSuccess || !retryable)
            {
                return response;
            }

            if (attempt < maxRetry)
            {
                Logger.Throttled($"retry-{path}", TimeSpan.FromSeconds(30),
                    $"请求 {path} 失败（{response.Message}），将在退避后重试（第 {attempt + 1}/{maxRetry} 次）");
            }
        }

        return last;
    }

    /// <summary>配置中的超时秒数，夹在 3–120 的合理区间。</summary>
    private int TimeoutSeconds
        => _settings.RequestTimeoutSeconds is >= 3 and <= 120
            ? _settings.RequestTimeoutSeconds
            : DefaultTimeoutSeconds;

    /// <summary>
    /// 明文 HTTP 检查。返回 null 表示通过，否则返回错误描述。
    /// </summary>
    /// <remarks>
    /// 这里检查的是<b>最终拼出的完整地址</b>，而不仅是服务器地址栏。
    /// 端点字段虽已禁止绝对地址，但配置可能来自旧版本或被直接改写过，
    /// 因此以真实请求地址为准才可靠。
    /// </remarks>
    private string? CheckInsecureHttp()
        => CheckInsecureHttpFor("");

    /// <summary>
    /// 针对将要请求的完整地址做明文物检查。
    /// </summary>
    /// <param name="path">端点相对路径，可为空（只校验服务器地址）。</param>
    private string? CheckInsecureHttpFor(string path)
    {
        var baseUrl = GetBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            // 地址为空由 ValidateConnection 报错，这里不重复拦截
            return null;
        }

        var full = string.IsNullOrEmpty(path) ? baseUrl : baseUrl + path;

        if (_settings.AllowInsecureHttp)
        {
            return null;
        }

        if (full.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return "服务器地址为明文 HTTP，而「允许明文 HTTP」已关闭。请改用 https:// 或开启该选项。";
        }

        // 既非 http 也非 https 的方案（file://、ftp://、自定义协议）不走本插件的网络通道
        if (!full.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return $"不支持的服务器协议：{full}。请使用 https:// 开头的地址。";
        }

        return null;
    }

    /// <summary>
    /// 单次发送尝试。
    /// </summary>
    /// <returns>响应，以及该失败是否值得重试。</returns>
    private async Task<(ApiResponse Response, bool Retryable)> TrySendOnceAsync(
        string path, string json, int timeoutSeconds, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var url = $"{GetBaseUrl()}{path}";

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = content
            };
            ApplySignature(request, json);

            using var response = await _httpClient.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            var result = ParseResponse(responseJson);

            // HTTP 层错误在响应体不可用时回填状态码，便于排查
            if ((int)response.StatusCode >= 400 && result.Code == 0)
            {
                result.Code = (int)response.StatusCode;
                if (string.IsNullOrWhiteSpace(result.Message))
                {
                    result.Message = $"服务端返回 HTTP {(int)response.StatusCode}";
                }
            }

            // 5xx 视为服务端临时故障，值得重试；4xx 属于请求本身的问题，重试无意义
            var retryable = (int)response.StatusCode >= 500;
            return (result, retryable);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 超时属于网络抖动，值得重试
            return (ApiResponse.Fail($"请求超时（{timeoutSeconds} 秒）"), true);
        }
        catch (OperationCanceledException)
        {
            return (ApiResponse.Fail("请求已取消"), false);
        }
        catch (HttpRequestException ex)
        {
            // 连接失败 / DNS 失败属于临时问题，值得重试。
            // 注意 ex.Message 里通常裸着 host:port，落日志前由 Logger.Mask 负责抹除。
            return (ApiResponse.Fail($"网络请求失败：{ex.Message}"), true);
        }
        catch (Exception ex)
        {
            return (ApiResponse.Fail($"请求失败：{ex.Message}"), false);
        }
    }

    /// <summary>
    /// 按配置给请求加上签名头（方案 A）。
    /// </summary>
    /// <remarks>
    /// 未启用签名时不做任何事，报文与旧版完全一致，
    /// 以便插件先行升级、服务端随后再补校验。
    /// 签名覆盖请求体原文，因此必须在 <paramref name="json"/> 定稿之后调用。
    /// </remarks>
    /// <param name="request">将要发送的请求。</param>
    /// <param name="json">请求体原文，与正文内容必须逐字节一致。</param>
    private void ApplySignature(HttpRequestMessage request, string json)
    {
        if (!_settings.SignatureEnabled)
        {
            return;
        }

        var secret = (_settings.DeviceSecret ?? "").Trim();
        if (secret.Length == 0)
        {
            return;
        }

        var signed = RequestSigner.Sign(secret, _settings.DeviceId, json);

        request.Headers.TryAddWithoutValidation(
            RequestSigner.HeaderVersion, RequestSigner.SchemeVersion);
        request.Headers.TryAddWithoutValidation(
            RequestSigner.HeaderDevice, _settings.DeviceId);
        request.Headers.TryAddWithoutValidation(
            RequestSigner.HeaderTimestamp, signed.Timestamp);
        request.Headers.TryAddWithoutValidation(
            RequestSigner.HeaderNonce, signed.Nonce);
        request.Headers.TryAddWithoutValidation(
            RequestSigner.HeaderSignature, signed.Signature);
    }

    private static ApiResponse ParseResponse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ApiResponse.Fail("服务端返回了空响应");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return ApiResponse.Fail("响应格式不是 JSON 对象");
            }

            var response = new ApiResponse();

            if (TryGetPropertyIgnoreCase(root, "code", out var codeProp))
            {
                response.Code = codeProp.ValueKind switch
                {
                    JsonValueKind.Number => codeProp.TryGetInt32(out var c) ? c : 0,
                    JsonValueKind.String => int.TryParse(codeProp.GetString(), out var sc) ? sc : 0,
                    _ => 0
                };
            }

            if (TryGetPropertyIgnoreCase(root, "msg", out var msgProp) && msgProp.ValueKind == JsonValueKind.String)
            {
                response.Message = msgProp.GetString() ?? "";
            }
            else if (TryGetPropertyIgnoreCase(root, "message", out var messageProp) &&
                     messageProp.ValueKind == JsonValueKind.String)
            {
                response.Message = messageProp.GetString() ?? "";
            }

            if (TryGetPropertyIgnoreCase(root, "status", out var statusProp) &&
                statusProp.ValueKind == JsonValueKind.String)
            {
                response.Status = statusProp.GetString() ?? "";
            }

            if (TryGetPropertyIgnoreCase(root, "auth_required", out var authProp) &&
                (authProp.ValueKind == JsonValueKind.True || authProp.ValueKind == JsonValueKind.False))
            {
                response.AuthRequired = authProp.GetBoolean();
            }

            return response;
        }
        catch (JsonException)
        {
            return ApiResponse.Fail("响应解析失败：返回内容不是合法 JSON");
        }
        catch (Exception ex)
        {
            return ApiResponse.Fail($"响应解析失败：{ex.Message}");
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}

/// <summary>
/// 服务端响应。
/// </summary>
public class ApiResponse
{
    /// <summary>业务状态码，0 表示成功；负数为插件内部错误。</summary>
    public int Code { get; set; }

    /// <summary>服务端返回的消息。</summary>
    public string Message { get; set; } = "";

    /// <summary>授权状态。</summary>
    public string Status { get; set; } = "";

    /// <summary>是否要求重新授权。</summary>
    public bool AuthRequired { get; set; }

    /// <summary>是否为成功响应。</summary>
    public bool IsSuccess => Code == 0;

    public static ApiResponse Fail(string message) => new() { Code = -1, Message = message };
}

/// <summary>
/// 一节课程的时段与科目信息。
/// </summary>
public class ScheduleItem
{
    /// <summary>科目名称。</summary>
    public string Subject { get; set; } = "";

    /// <summary>开始时间（HH:mm:ss）。</summary>
    public string StartTime { get; set; } = "";

    /// <summary>结束时间（HH:mm:ss）。</summary>
    public string EndTime { get; set; } = "";

    /// <summary>是否为临时调整的课程。</summary>
    public bool IsChanged { get; set; }
}
