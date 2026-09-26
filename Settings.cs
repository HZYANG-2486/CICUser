using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CICUser;

/// <summary>
/// 插件持久化配置。包含当前生效的连接参数、报文模板，
/// 以及可供一键切换的多套配置与多套模板。
/// </summary>
public class PluginSettings
{
    // ── 频率下限（防止误设为极小值导致请求风暴）──
    /// <summary>状态报文最小间隔（秒）。</summary>
    public const int MinStatusIntervalSeconds = 5;

    /// <summary>课表报文最小间隔（秒）。</summary>
    public const int MinScheduleIntervalSeconds = 60;

    /// <summary>心跳最小间隔（秒）。</summary>
    public const int MinHeartbeatIntervalSeconds = 10;

    // ── 连接参数 ──
    /// <summary>服务器根地址，例如 http://192.168.1.10:8080。</summary>
    public string ServerUrl { get; set; } = "";

    /// <summary>
    /// 设备唯一标识。默认由 <see cref="DeviceIdGenerator"/> 自动生成并固化，
    /// 手动修改会导致服务端将其识别为新设备，因此界面上默认只读。
    /// </summary>
    public string DeviceId { get; set; } = "";

    /// <summary>设备ID 的生成方式，用于界面回显与重新生成。</summary>
    public string DeviceIdSource { get; set; } = nameof(CICUser.DeviceIdSource.MachineId);

    /// <summary>设备ID 是否被用户手动修改过，界面据此显示警示。</summary>
    public bool DeviceIdManuallyEdited { get; set; }

    /// <summary>设备名称，例如「701班电子白板」。</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>班级名称，例如「高一(1)班」。</summary>
    public string ClassName { get; set; } = "";

    // ── 授权状态 ──
    /// <summary>授权状态：pending / authorized / rejected。</summary>
    public string AuthStatus { get; set; } = "pending";

    /// <summary>授权状态附带的消息。</summary>
    public string AuthMessage { get; set; } = "";

    // ── 发送策略（三通道各自独立）──
    /// <summary>是否启用自动发送（总开关，关闭后三通道定时发送全部停止）。</summary>
    public bool AutoSendEnabled { get; set; } = true;

    /// <summary>是否启用状态报文定时上报。</summary>
    public bool StatusSendEnabled { get; set; } = true;

    /// <summary>状态上报的定时间隔（秒），最小 5 秒。</summary>
    public int StatusSendIntervalSeconds { get; set; } = 30;

    /// <summary>是否启用课表报文定时上报。</summary>
    public bool ScheduleSendEnabled { get; set; } = true;

    /// <summary>课表定时上报间隔（秒），最小 60 秒。课表变更时另有即时通道。</summary>
    public int ScheduleSendIntervalSeconds { get; set; } = 1800;

    /// <summary>是否启用心跳保活报文。</summary>
    public bool HeartbeatEnabled { get; set; } = true;

    /// <summary>心跳间隔（秒），最小 10 秒。</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 60;

    /// <summary>心跳接口路径。</summary>
    public string HeartbeatEndpoint { get; set; } = "/class/api/heartbeat";

    /// <summary>课表发生变更时是否立即上报。</summary>
    public bool RealtimeScheduleSync { get; set; } = true;

    /// <summary>课表内容无变化时是否跳过上报。</summary>
    public bool SkipUnchangedSchedule { get; set; } = true;

    // ── 授权（可选，不强制）──
    /// <summary>
    /// 是否启用授权检查。默认关闭，以便在未实现授权机制的平台上直接上报。
    /// 开启后，只有授权通过才会发送数据。
    /// </summary>
    public bool AuthCheckEnabled { get; set; } = false;

    /// <summary>授权申请接口路径。</summary>
    public string AuthRequestEndpoint { get; set; } = "/class/api/auth/request";

    /// <summary>授权查询接口路径。</summary>
    public string AuthQueryEndpoint { get; set; } = "/class/api/auth/query";

    /// <summary>授权请求体中表示设备ID 的字段名。</summary>
    public string AuthDeviceIdField { get; set; } = "DeviceId";

    /// <summary>授权请求体中表示设备名称的字段名。</summary>
    public string AuthDeviceNameField { get; set; } = "DeviceName";

    /// <summary>授权请求体中表示班级名称的字段名。</summary>
    public string AuthClassNameField { get; set; } = "ClassName";

    // ── 课表发送格式 ──
    /// <summary>
    /// 课表内容的序列化格式预设：
    /// ByIndex（按节次字典）/ Array（数组）/ BySubject（按科目合并）/ Flat（扁平列表）。
    /// </summary>
    public string ScheduleFormat { get; set; } = nameof(CICUser.ScheduleFormat.ByIndex);

    /// <summary>课表内容为 ByIndex / Array 时，每一项使用的键名顺序或字段名集合。</summary>
    public List<MessageField> ScheduleItemFields { get; set; } = MessageField.CreateScheduleItemDefaults();

    /// <summary>课表格式为 ByIndex 时，节次的键名前缀。</summary>
    public string ScheduleIndexKeyPrefix { get; set; } = "";

    // ── 稳定性与安全性 ──
    /// <summary>单次 HTTP 请求超时（秒）。</summary>
    public int RequestTimeoutSeconds { get; set; } = 15;

    /// <summary>请求失败后的最大重试次数（不含首次）。</summary>
    public int MaxRetryCount { get; set; } = 2;

    /// <summary>是否在日志中对设备ID 与服务器地址脱敏。</summary>
    public bool MaskSensitiveInLog { get; set; } = true;

    /// <summary>允许使用明文 HTTP（未加密）服务器地址。</summary>
    public bool AllowInsecureHttp { get; set; } = true;

    // ── 报文模板 ──
    /// <summary>状态报文模板。</summary>
    public MessageTemplate StatusTemplate { get; set; } = MessageTemplate.CreateDefault();

    /// <summary>课表报文模板。</summary>
    public MessageTemplate ScheduleTemplate { get; set; } = MessageTemplate.CreateDefault();

    /// <summary>保存的多套状态报文模板。</summary>
    public List<MessageTemplate> StatusTemplates { get; set; } = new();

    /// <summary>保存的多套课表报文模板。</summary>
    public List<MessageTemplate> ScheduleTemplates { get; set; } = new();

    // ── 多套配置 ──
    /// <summary>本地保存的多套配置，用于多教室批量部署时切换。</summary>
    public List<ConfigProfile> SavedProfiles { get; set; } = new();

    /// <summary>当前生效配置的名称，用于界面回显。</summary>
    public string CurrentProfileName { get; set; } = "默认配置";

    /// <summary>课表上报版本号，每次内容变化后自增，供服务端判断新旧。</summary>
    public int ScheduleVersion { get; set; } = 0;

    /// <summary>上一次成功上报的课表内容哈希，用于变更去重。</summary>
    public string LastScheduleHash { get; set; } = "";

    [JsonIgnore]
    private static string SettingsDirectory
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(appData, "ClassIsland", "Plugins", "CICUser");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>配置文件完整路径。</summary>
    [JsonIgnore]
    public static string SettingsFilePath => Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>
    /// 规范化服务器根地址：去空白与不可见字符、去掉结尾的多余斜杠。
    /// </summary>
    /// <remarks>
    /// 地址可能带着从网页或聊天工具粘来的零宽字符，肉眼无法察觉却会让
    /// <c>Uri</c> 解析出 <c>http://host\u200B:8080</c> 这样的主机名而连接失败。
    /// 结尾斜杠会被 <c>GetBaseUrl</c> 再削一次，这里先去干净更直观。
    /// </remarks>
    internal static string NormalizeServerUrl(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        // 去掉不可见字符、控制字符与全部空白：
        // 从网页或聊天工具粘来的地址常夹带换行与零宽字符。
        var trimmed = TextSanitizer.CleanStrict(value, alsoRemoveWhitespace: true);

        return trimmed.TrimEnd('/');
    }

    /// <summary>
    /// 规范化接口路径：去空白与不可见字符、确保以 / 开头。
    /// </summary>
    /// <remarks>
    /// 这里同时要防住两类「看起来正常、实际会改道」的输入：
    /// <list type="bullet">
    /// <item>零宽字符（U+200B 等）会逃过 <see cref="string.IsNullOrWhiteSpace"/>，
    /// 使路径带着肉眼不可见的字符发出，服务端返回 404。</item>
    /// <item>整段绝对地址（<c>http://其它主机/xx</c>）。端点本应是相对路径，
    /// 若放行绝对地址，拼接后的请求会绕开对服务器地址的明文 HTTP 守卫，
    /// 把上报内容发往另一个主机。</item>
    /// </list>
    /// 同时剥离查询串与片段：它们由模板与代码自行附加，写在路径里只会造成重复。
    /// </remarks>
    internal static string NormalizeEndpoint(string? value, string fallback)
    {
        if (string.IsNullOrEmpty(value))
        {
            return fallback;
        }

        var trimmed = TextSanitizer.Clean(value);
        if (trimmed.Length == 0)
        {
            return fallback;
        }

        // 绝对地址一律拒绝：端点只能是相对路径
        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            return fallback;
        }

        // 查询串与片段不属于路径本身
        var cut = trimmed.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0)
        {
            trimmed = trimmed[..cut];
        }

        // 控制字符与内部空白会污染 HTTP 请求行，直接去掉
        trimmed = TextSanitizer.CleanStrict(trimmed, alsoRemoveWhitespace: true);

        if (trimmed.Length == 0)
        {
            return fallback;
        }

        trimmed = trimmed.StartsWith('/') ? trimmed : "/" + trimmed;

        // 折叠重复斜杠，"//api" 在某些服务端会被当作跨域路径而拒绝
        while (trimmed.Contains("//", StringComparison.Ordinal))
        {
            trimmed = trimmed.Replace("//", "/", StringComparison.Ordinal);
        }

        return trimmed.Length > 256 ? fallback : trimmed;
    }

    /// <summary>
    /// 规范化报文字段名：去空白与不可见字符，非法字符替换为下划线。
    /// </summary>
    /// <remarks>
    /// JSON 的键名不允许叠加控制字符与零宽字符。若放行 <c>"Next\u200BSubject"</c>
    /// 这类键，序列化结果在肉眼下与 <c>"NextSubject"</c> 无异，
    /// 服务端却取不到值，表现为「字段缺失」。
    /// </remarks>
    internal static string NormalizeFieldName(string? value, string fallback)
    {
        if (string.IsNullOrEmpty(value))
        {
            return fallback;
        }

        var trimmed = TextSanitizer.Clean(value);
        if (trimmed.Length == 0)
        {
            return fallback;
        }

        var chars = trimmed
            .Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_')
            .ToArray();
        var result = new string(chars);

        // 全是非法字符时替换结果会退化成下划线串，此时回落更清晰
        return result.Length == 0 || result.All(c => c == '_') ? fallback : result;
    }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 从磁盘加载配置。文件缺失或损坏时回退到默认配置，并补齐新增字段。
    /// </summary>
    public static PluginSettings Load()
    {
        PluginSettings settings;
        try
        {
            var path = SettingsFilePath;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                settings = JsonSerializer.Deserialize<PluginSettings>(json, ReadOptions) ?? new PluginSettings();
            }
            else
            {
                settings = new PluginSettings();
            }
        }
        catch
        {
            settings = new PluginSettings();
        }

        settings.Normalize();
        return settings;
    }

    /// <summary>
    /// 补齐可能缺失的集合与模板，兼容旧版配置文件。
    /// </summary>
    public void Normalize()
    {
        StatusTemplate ??= MessageTemplate.CreateDefault();
        ScheduleTemplate ??= MessageTemplate.CreateDefault();
        StatusTemplates ??= new List<MessageTemplate>();
        ScheduleTemplates ??= new List<MessageTemplate>();
        SavedProfiles ??= new List<ConfigProfile>();

        if (StatusTemplate.StatusFields == null || StatusTemplate.StatusFields.Count == 0)
        {
            // 只重建缺失的一半：原实现整体替换会让用户已配好的课表字段一并丢失
            StatusTemplate.StatusFields = MessageTemplate.CreateDefaultStatusFields();
        }

        if (ScheduleTemplate.ScheduleFields == null || ScheduleTemplate.ScheduleFields.Count == 0)
        {
            ScheduleTemplate.ScheduleFields = MessageTemplate.CreateDefaultScheduleFields();
        }

        // 清理列表中的空项与无名项，避免界面上出现空白模板
        StatusTemplates.RemoveAll(x => x == null || string.IsNullOrWhiteSpace(x.Name));
        ScheduleTemplates.RemoveAll(x => x == null || string.IsNullOrWhiteSpace(x.Name));
        SavedProfiles.RemoveAll(x => x == null || string.IsNullOrWhiteSpace(x.Name));

        // 列表中的每套模板都要有可用字段，否则界面编辑时会显示为空
        foreach (var t in StatusTemplates.Concat(ScheduleTemplates))
        {
            t.StatusFields ??= new List<MessageField>();
            t.ScheduleFields ??= new List<MessageField>();
        }

        NormalizeActiveTemplateName();

        // ── 频率范围钳制（下限保护，避免误设导致请求风暴）──
        if (StatusSendIntervalSeconds < MinStatusIntervalSeconds)
        {
            StatusSendIntervalSeconds = 30;
        }

        if (ScheduleSendIntervalSeconds < MinScheduleIntervalSeconds)
        {
            ScheduleSendIntervalSeconds = 1800;
        }

        if (HeartbeatIntervalSeconds < MinHeartbeatIntervalSeconds)
        {
            HeartbeatIntervalSeconds = 60;
        }

        // ── 路径字段兜底 ──
        HeartbeatEndpoint = NormalizeEndpoint(HeartbeatEndpoint, "/class/api/heartbeat");
        AuthRequestEndpoint = NormalizeEndpoint(AuthRequestEndpoint, "/class/api/auth/request");
        AuthQueryEndpoint = NormalizeEndpoint(AuthQueryEndpoint, "/class/api/auth/query");

        AuthDeviceIdField = NormalizeFieldName(AuthDeviceIdField, "DeviceId");
        AuthDeviceNameField = NormalizeFieldName(AuthDeviceNameField, "DeviceName");
        AuthClassNameField = NormalizeFieldName(AuthClassNameField, "ClassName");

        // 服务器根地址：去空白与不可见字符、去掉结尾斜杠。
        // 地址自带路径（如 http://host:8080/base）是允许的，拼接时只保留其前缀。
        ServerUrl = NormalizeServerUrl(ServerUrl);

        // 模板内的路径与字段名同样需要规范化，否则用户改坏一处就能让整条通道失效
        foreach (var t in new[] { StatusTemplate, ScheduleTemplate }
                     .Concat(StatusTemplates)
                     .Concat(ScheduleTemplates))
        {
            if (t == null)
            {
                continue;
            }

            t.StatusEndpoint = NormalizeEndpoint(t.StatusEndpoint, "/class/api/submit");
            t.ScheduleEndpoint = NormalizeEndpoint(t.ScheduleEndpoint, "/class/api/schedule/update");
            t.ScheduleDataKey = NormalizeFieldName(t.ScheduleDataKey, "Schedule");

            // 字段名是 JSON 的键，必须与值区分对待：键不许含空白与不可见字符
            foreach (var f in t.StatusFields.Concat(t.ScheduleFields))
            {
                if (f == null || string.IsNullOrWhiteSpace(f.Key))
                {
                    continue;
                }

                var key = TextSanitizer.Clean(f.Key);
                if (key.Length == 0)
                {
                    f.Key = "_unknown";
                    continue;
                }

                if (key.Length > 64)
                {
                    key = key[..64];
                }

                f.Key = key;
            }
        }

        // ── 课表格式 ──
        if (!Enum.TryParse<ScheduleFormat>(ScheduleFormat, out _))
        {
            ScheduleFormat = nameof(CICUser.ScheduleFormat.ByIndex);
        }

        ScheduleItemFields ??= MessageField.CreateScheduleItemDefaults();
        ScheduleItemFields.RemoveAll(x => x == null || string.IsNullOrWhiteSpace(x.Key));
        if (ScheduleItemFields.Count == 0)
        {
            ScheduleItemFields = MessageField.CreateScheduleItemDefaults();
        }

        ScheduleIndexKeyPrefix ??= "";

        // ── 安全项范围 ──
        if (RequestTimeoutSeconds is < 3 or > 120)
        {
            RequestTimeoutSeconds = 15;
        }

        if (MaxRetryCount is < 0 or > 5)
        {
            MaxRetryCount = 2;
        }

        if (string.IsNullOrWhiteSpace(CurrentProfileName))
        {
            CurrentProfileName = "默认配置";
        }

        // 生成方式字段缺失或非法时回落到默认方式
        if (!Enum.TryParse<DeviceIdSource>(DeviceIdSource, out _))
        {
            DeviceIdSource = nameof(CICUser.DeviceIdSource.MachineId);
        }
    }

    /// <summary>
    /// 确保设备ID 已生成。仅在为空时生成，已有值一律保留，
    /// 避免升级后覆盖老用户手工填写的标识导致服务端把设备当作新设备。
    /// </summary>
    /// <returns>本次是否执行了生成。</returns>
    public bool EnsureDeviceId()
    {
        if (!string.IsNullOrWhiteSpace(DeviceId))
        {
            return false;
        }

        var source = Enum.TryParse<DeviceIdSource>(DeviceIdSource, out var parsed)
            ? parsed
            : CICUser.DeviceIdSource.MachineId;

        // 「班级+设备名派生」在班级或设备名为空时无法产生可读前缀，
        // 此时回退到机器码哈希，保证结果始终有效。
        if (source == CICUser.DeviceIdSource.ClassNameDerived &&
            string.IsNullOrWhiteSpace(ClassName) && string.IsNullOrWhiteSpace(DeviceName))
        {
            source = CICUser.DeviceIdSource.MachineId;
        }

        DeviceId = DeviceIdGenerator.Generate(source, ClassName, DeviceName);
        DeviceIdSource = source.ToString();
        DeviceIdManuallyEdited = false;
        return true;
    }

    /// <summary>
    /// 修正活动模板，使其与模板列表保持一致。
    /// 当活动模板已不在列表中时，回退到列表首项或内置默认模板，
    /// 避免界面继续引用一个已不存在的模板（下拉框残留幽灵项）。
    /// </summary>
    /// <remarks>
    /// 状态与课表是<b>两套独立</b>的字段集合：前者描述「当前在上什么课」，
    /// 后者描述「今天有哪些课」。因此回退时不能用同一份内容填充两者，
    /// 否则课表报文会带上状态字段、丢失课表字段。
    /// </remarks>
    private void NormalizeActiveTemplateName()
    {
        var activeName = StatusTemplate?.Name;

        // 活动模板本身合法且已在列表中，无需修正
        if (!string.IsNullOrWhiteSpace(activeName) &&
            StatusTemplates.Any(x => x.Name == activeName))
        {
            return;
        }

        // 列表中还有其它模板时，切换到首项。
        // 该模板自带两套字段，直接整体克隆即可，不能只取其中一套。
        var fallback = StatusTemplates.FirstOrDefault();
        if (fallback != null)
        {
            StatusTemplate = fallback.Clone();
            ScheduleTemplate = fallback.Clone();
            return;
        }

        // 列表为空：活动模板若可用则保留，否则退回内置默认。
        // 这里分别修复两套字段，缺哪套补哪套，已有的那套原样保留。
        StatusTemplate ??= MessageTemplate.CreateDefault();
        if (StatusTemplate.StatusFields.Count == 0)
        {
            StatusTemplate.StatusFields = MessageTemplate.CreateDefaultStatusFields();
        }

        ScheduleTemplate ??= MessageTemplate.CreateDefault();
        if (ScheduleTemplate.ScheduleFields.Count == 0)
        {
            ScheduleTemplate.ScheduleFields = MessageTemplate.CreateDefaultScheduleFields();
        }
    }

    /// <summary>
    /// 保存配置到磁盘。写入失败时记录日志但不抛出，避免影响主流程。
    /// </summary>
    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, WriteOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch (Exception ex)
        {
            Logger.Error("保存配置失败", ex);
        }
    }

    // ── 配置导入导出 ──

    /// <summary>
    /// 导出当前配置到指定文件，供批量部署时复制到其它教室设备。
    /// </summary>
    public static bool ExportTo(string filePath, PluginSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, WriteOptions);
            File.WriteAllText(filePath, json);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"导出配置到 {filePath} 失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 从文件导入配置。导入后自动补齐缺失字段。
    /// </summary>
    public static PluginSettings? ImportFrom(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                Logger.Warning($"导入配置失败：文件不存在 {filePath}");
                return null;
            }

            var json = File.ReadAllText(filePath);
            var settings = JsonSerializer.Deserialize<PluginSettings>(json, ReadOptions);
            settings?.Normalize();
            return settings;
        }
        catch (Exception ex)
        {
            Logger.Error($"从 {filePath} 导入配置失败", ex);
            return null;
        }
    }

    /// <summary>
    /// 把当前连接参数、上报策略与报文模板保存为一套命名配置。同名时覆盖。
    /// </summary>
    public void SaveAsProfile(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"配置{DateTime.Now:MMddHHmm}";
        }

        var profile = new ConfigProfile
        {
            Name = name,
            ServerUrl = ServerUrl,
            DeviceId = DeviceId,
            DeviceIdSource = DeviceIdSource,
            DeviceName = DeviceName,
            ClassName = ClassName,
            AutoSendEnabled = AutoSendEnabled,
            StatusSendEnabled = StatusSendEnabled,
            StatusSendIntervalSeconds = StatusSendIntervalSeconds,
            ScheduleSendEnabled = ScheduleSendEnabled,
            ScheduleSendIntervalSeconds = ScheduleSendIntervalSeconds,
            HeartbeatEnabled = HeartbeatEnabled,
            HeartbeatIntervalSeconds = HeartbeatIntervalSeconds,
            HeartbeatEndpoint = HeartbeatEndpoint,
            RealtimeScheduleSync = RealtimeScheduleSync,
            SkipUnchangedSchedule = SkipUnchangedSchedule,
            AuthCheckEnabled = AuthCheckEnabled,
            AuthRequestEndpoint = AuthRequestEndpoint,
            AuthQueryEndpoint = AuthQueryEndpoint,
            ScheduleFormat = ScheduleFormat,
            ScheduleItemFields = ScheduleItemFields?.Select(x => x.Clone()).ToList()
                ?? MessageField.CreateScheduleItemDefaults(),
            ScheduleIndexKeyPrefix = ScheduleIndexKeyPrefix,
            StatusTemplate = StatusTemplate?.Clone(),
            ScheduleTemplate = ScheduleTemplate?.Clone()
        };

        var existing = SavedProfiles.FirstOrDefault(x => x.Name == name);
        if (existing != null)
        {
            SavedProfiles.Remove(existing);
        }

        SavedProfiles.Add(profile);
        CurrentProfileName = name;
    }

    /// <summary>
    /// 切换到指定的命名配置，返回是否切换成功。
    /// 除连接参数外，同时恢复上报策略与报文模板，
    /// 保证切换后界面上的所有开关与文案都与保存时一致。
    /// </summary>
    public bool ApplyProfile(string name)
    {
        var profile = SavedProfiles.FirstOrDefault(x => x.Name == name);
        if (profile == null)
        {
            return false;
        }

        ServerUrl = profile.ServerUrl;
        DeviceId = profile.DeviceId;
        DeviceIdSource = string.IsNullOrWhiteSpace(profile.DeviceIdSource)
            ? nameof(CICUser.DeviceIdSource.MachineId)
            : profile.DeviceIdSource;
        DeviceName = profile.DeviceName;
        ClassName = profile.ClassName;
        AutoSendEnabled = profile.AutoSendEnabled;
        StatusSendEnabled = profile.StatusSendEnabled;
        StatusSendIntervalSeconds = profile.StatusSendIntervalSeconds;
        ScheduleSendEnabled = profile.ScheduleSendEnabled;
        ScheduleSendIntervalSeconds = profile.ScheduleSendIntervalSeconds;
        HeartbeatEnabled = profile.HeartbeatEnabled;
        HeartbeatIntervalSeconds = profile.HeartbeatIntervalSeconds;
        HeartbeatEndpoint = string.IsNullOrWhiteSpace(profile.HeartbeatEndpoint)
            ? "/class/api/heartbeat" : profile.HeartbeatEndpoint;
        RealtimeScheduleSync = profile.RealtimeScheduleSync;
        SkipUnchangedSchedule = profile.SkipUnchangedSchedule;
        AuthCheckEnabled = profile.AuthCheckEnabled;
        AuthRequestEndpoint = string.IsNullOrWhiteSpace(profile.AuthRequestEndpoint)
            ? "/class/api/auth/request" : profile.AuthRequestEndpoint;
        AuthQueryEndpoint = string.IsNullOrWhiteSpace(profile.AuthQueryEndpoint)
            ? "/class/api/auth/query" : profile.AuthQueryEndpoint;
        ScheduleFormat = string.IsNullOrWhiteSpace(profile.ScheduleFormat)
            ? nameof(CICUser.ScheduleFormat.ByIndex) : profile.ScheduleFormat;
        ScheduleItemFields = profile.ScheduleItemFields?.Select(x => x.Clone()).ToList()
            ?? MessageField.CreateScheduleItemDefaults();
        ScheduleIndexKeyPrefix = profile.ScheduleIndexKeyPrefix ?? "";

        // 模板随配置一同切换，仅在保存时确实带有模板的情况下覆盖
        if (profile.StatusTemplate != null)
        {
            StatusTemplate = profile.StatusTemplate.Clone();
        }

        if (profile.ScheduleTemplate != null)
        {
            ScheduleTemplate = profile.ScheduleTemplate.Clone();
        }

        CurrentProfileName = profile.Name;

        // 服务器或设备变化后原授权不再适用
        AuthStatus = "pending";
        AuthMessage = "";
        return true;
    }

    /// <summary>
    /// 删除指定的命名配置。
    /// </summary>
    public bool RemoveProfile(string name)
    {
        var profile = SavedProfiles.FirstOrDefault(x => x.Name == name);
        if (profile == null)
        {
            return false;
        }

        SavedProfiles.Remove(profile);
        if (CurrentProfileName == name)
        {
            CurrentProfileName = SavedProfiles.FirstOrDefault()?.Name ?? "默认配置";
        }
        return true;
    }

    /// <summary>
    /// 生成返回给界面的全局配置键值对，供报文模板引用。
    /// </summary>
    public Dictionary<string, string> GetGlobalValues() => new()
    {
        ["DeviceId"] = DeviceId,
        ["DeviceName"] = DeviceName,
        ["ClassName"] = ClassName,
        ["ServerUrl"] = ServerUrl
    };
}
