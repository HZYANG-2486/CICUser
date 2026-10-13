using System;
using System.IO;

namespace CICUser;

/// <summary>
/// 插件日志。带大小轮转，避免长期运行写满磁盘；
/// 同时提供节流能力，防止高频事件刷屏。
/// </summary>
public static class Logger
{
    private static readonly string LogPath;
    private static readonly object SyncRoot = new();

    /// <summary>单个日志文件的大小上限，超过后自动轮转。</summary>
    private const long MaxLogSizeBytes = 1024 * 1024;

    /// <summary>保留的历史日志份数。</summary>
    private const int MaxLogFiles = 3;

    static Logger()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pluginDir = Path.Combine(appData, "ClassIsland", "Plugins", "CICUser");
        try
        {
            Directory.CreateDirectory(pluginDir);
        }
        catch
        {
            // 目录创建失败时退回到临时目录，保证日志功能不中断
            pluginDir = Path.GetTempPath();
        }
        LogPath = Path.Combine(pluginDir, "plugin.log");
    }

    /// <summary>当前日志文件路径。</summary>
    public static string CurrentLogPath => LogPath;

    /// <summary>
    /// 是否对日志中的敏感字段脱敏。由插件在加载配置后设置。
    /// 默认开启，避免把设备ID 与服务器地址明文写入可分享的日志文件。
    /// </summary>
    public static bool MaskSensitive { get; set; } = true;

    /// <summary>
    /// 需要从日志中抹除的字面量，通常取自当前配置的服务器主机名。
    /// 由插件在加载配置后设置。
    /// </summary>
    /// <remarks>
    /// 正则只能「猜」地址的形态，而 .NET 网络异常的原文形如
    /// <c>No such host is known. (my-server.local:443)</c> 或
    /// <c>actively refused it. (192.168.1.10:8080)</c>——主机名裸在括号里，
    /// 不带 scheme，任何基于形态的规则都可能漏。
    /// 这里改用「已知答案反查」：从配置里取出主机名，直接做字面量替换，
    /// 确定性覆盖，且不会误伤版本号之类的数字串。
    /// </remarks>
    public static string[] SensitiveLiterals { get; set; } = Array.Empty<string>();

    public static void Info(string message) => WriteLog("INFO", Mask(message));

    public static void Warning(string message) => WriteLog("WARN", Mask(message));

    public static void Error(string message) => WriteLog("ERROR", Mask(message));

    public static void Error(string message, Exception ex)
        => WriteLog("ERROR", Mask($"{message}\nException: {ex}"));

    public static void Debug(string message) => WriteLog("DEBUG", Mask(message));

    /// <summary>
    /// 带节流的日志：同一标识在间隔内只记录一次，
    /// 用于配合高频事件（如主计时器逐帧触发）避免刷屏。
    /// </summary>
    /// <param name="throttleKey">节流标识，相同标识共享间隔。</param>
    /// <param name="interval">最小记录间隔。</param>
    /// <param name="message">日志内容。</param>
    /// <returns>本次是否实际写入。</returns>
    public static bool Throttled(string throttleKey, TimeSpan interval, string message)
    {
        if (!ThrottleState.ShouldWrite(throttleKey, interval))
        {
            return false;
        }

        WriteLog("DEBUG", Mask(message));
        return true;
    }

    /// <summary>
    /// 对日志文本做敏感信息脱敏。
    /// </summary>
    /// <remarks>
    /// 处理三类内容：
    /// <list type="bullet">
    /// <item>设备ID：保留首尾各若干字符，中间以 * 代替，既便于比对又不可还原</item>
    /// <item>服务器地址：保留 scheme 与端口，隐藏主机名与路径细节</item>
    /// <item>裸地址：不带 scheme 的内网 IP / 内网域名，常见于网络异常原文</item>
    /// </list>
    /// 关闭脱敏时原样返回，便于本机深度排查。
    /// </remarks>
    public static string Mask(string message)
    {
        if (!MaskSensitive || string.IsNullOrEmpty(message))
        {
            return message;
        }

        // 顺序有讲究：
        // ① 先处理带 scheme 的 URL，它信息最完整、脱敏效果最好；
        // ② 再处理裸内网地址，覆盖异常原文里的 host:port；
        // ③ 最后用配置字面量兜底，处理前两步都没认出来的形态；
        // ④ 设备ID 放最后，避免被前面的替换打乱边界。
        var result = MaskServerUrls(message);
        result = MaskBareEndpoints(result);
        result = MaskLiterals(result);
        result = MaskDeviceIds(result);
        return result;
    }

    /// <summary>
    /// 从服务器地址解析出需要在日志中抹除的字面量。
    /// </summary>
    /// <remarks>
    /// 同时登记 <c>host</c> 与 <c>host:port</c> 两种形态：网络异常原文里
    /// 两者都可能出现，先替换长串可避免短串把长串切碎后留下端口残片。
    /// </remarks>
    /// <param name="serverUrl">当前配置的服务器地址，可为空。</param>
    /// <returns>按长度降序排列的字面量数组。</returns>
    public static string[] BuildSensitiveLiterals(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || !Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return Array.Empty<string>();
        }

        var host = uri.Host;
        var literals = new System.Collections.Generic.List<string> { host };

        if (uri.Port > 0)
        {
            literals.Add($"{host}:{uri.Port}");
        }

        literals.Sort((a, b) => b.Length.CompareTo(a.Length));
        return literals.ToArray();
    }

    /// <summary>隐藏 URL 中的主机名与凭据部分。</summary>
    private static string MaskServerUrls(string text)
        => UrlRegex.Replace(text, match =>
        {
            var scheme = match.Groups["scheme"].Value;
            var host = match.Groups["host"].Value;
            return $"{scheme}://{MaskMiddle(host, 2, 3)}";
        });

    /// <summary>
    /// 隐藏不带 scheme 的内网地址（含端口）。
    /// </summary>
    /// <remarks>
    /// <see cref="UrlRegex"/> 要求 <c>https?://</c> 开头，而 .NET 的网络异常
    /// 往往只把 <c>host:port</c> 放进括号里，于是整段原样落进日志。
    /// 这里只圈定<b>内网段</b>与<b>内网域名后缀</b>：
    /// 版本号（1.0.0.1）之类的数字串永远不会落在 10./172.16-31./192.168. 段，
    /// 因此可以放心匹配而不产生误报。
    /// </remarks>
    private static string MaskBareEndpoints(string text)
        => BareEndpointRegex.Replace(text, "***");

    /// <summary>
    /// 用配置中的已知字面量做精准替换，作为形态匹配的兜底。
    /// </summary>
    private static string MaskLiterals(string text)
    {
        var literals = SensitiveLiterals;
        if (literals == null || literals.Length == 0)
        {
            return text;
        }

        var result = text;
        foreach (var literal in literals)
        {
            // 过短的串（如单字符主机名）替换起来只会破坏日志可读性
            if (string.IsNullOrWhiteSpace(literal) || literal.Length < 4)
            {
                continue;
            }

            result = result.Replace(literal, "***", StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    /// <summary>
    /// 隐藏设备ID。
    /// 设备ID 可能是纯十六进制哈希，也可能是「班级-设备名-哈希」组合形式。
    /// 这里对形如 <c>ABC-123-DEADBEEF</c> 或 16 位十六进制串做处理。
    /// </summary>
    private static string MaskDeviceIds(string text)
    {
        var result = CompositeIdRegex.Replace(text, match =>
        {
            var value = match.Value;
            return value.Length <= 8 ? value : MaskMiddle(value, 3, 3);
        });

        return HexIdRegex.Replace(result, match => MaskMiddle(match.Value, 4, 4));
    }

    /// <summary>保留首尾若干字符，中间以固定长度的 * 代替。</summary>
    private static string MaskMiddle(string value, int head, int tail)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        // 过短的值整体打码，避免脱敏后反而暴露长度特征
        if (value.Length <= head + tail)
        {
            return new string('*', value.Length);
        }

        return $"{value[..head]}***{value[^tail..]}";
    }

    /// <summary>匹配 http(s)://host[:port] 形式，主机名单独捕获。</summary>
    private static readonly System.Text.RegularExpressions.Regex UrlRegex = new(
        @"(?<scheme>https?)://(?<host>[^\s/""']+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>匹配「字母数字-字母数字-字母数字」形式的组合设备ID。</summary>
    private static readonly System.Text.RegularExpressions.Regex CompositeIdRegex = new(
        @"\b[A-Za-z0-9_]{2,20}(?:-[A-Za-z0-9_]{1,20}){1,3}\b",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>匹配 16 位十六进制串（设备ID 的哈希部分）。</summary>
    private static readonly System.Text.RegularExpressions.Regex HexIdRegex = new(
        @"\b[0-9A-Fa-f]{16}\b",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 匹配不带 scheme 的内网 IPv4（含端口）与内网域名（含端口）。
    /// </summary>
    /// <remarks>
    /// <para>覆盖的私网段：10/8、172.16/12、192.168/16、127/8、169.254/16。</para>
    /// <para>覆盖的域名后缀：.local / .lan / .internal / .corp / .home，
    /// 这些是校园与企业内网最常用的私有后缀，公网 DNS 不解析。</para>
    /// <para>前后用断言排除相邻的字母数字与点号，避免把 <c>v1.0.0.1</c> 或
    /// 更长串里的片段截出来。</para>
    /// </remarks>
    private static readonly System.Text.RegularExpressions.Regex BareEndpointRegex = new(
        @"(?<![0-9A-Za-z.])(?:" +
        @"(?:10|127)\.\d{1,3}\.\d{1,3}\.\d{1,3}" +
        @"|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3}" +
        @"|192\.168\.\d{1,3}\.\d{1,3}" +
        @"|169\.254\.\d{1,3}\.\d{1,3}" +
        @"|[0-9A-Za-z](?:[0-9A-Za-z-]*[0-9A-Za-z])?\.(?:local|lan|internal|corp|home)" +
        @")(?::\d{2,5})?(?![0-9A-Za-z])",
        System.Text.RegularExpressions.RegexOptions.Compiled
        | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static void WriteLog(string level, string message)
    {
        try
        {
            lock (SyncRoot)
            {
                RotateIfNeeded();
                using var writer = new StreamWriter(LogPath, append: true);
                writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}");
            }
        }
        catch
        {
            // 日志失败不应影响插件主流程
        }
    }

    /// <summary>
    /// 日志超过大小上限时轮转：plugin.log → plugin.1.log → plugin.2.log，
    /// 超出保留份数的旧文件被删除。
    /// </summary>
    private static void RotateIfNeeded()
    {
        var info = new FileInfo(LogPath);
        if (!info.Exists || info.Length < MaxLogSizeBytes)
        {
            return;
        }

        try
        {
            var oldest = $"{LogPath}.{MaxLogFiles}";
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (var i = MaxLogFiles - 1; i >= 1; i--)
            {
                var source = $"{LogPath}.{i}";
                var target = $"{LogPath}.{i + 1}";
                if (File.Exists(source))
                {
                    File.Move(source, target, overwrite: true);
                }
            }

            File.Move(LogPath, $"{LogPath}.1", overwrite: true);
        }
        catch
        {
            // 轮转失败时继续追加写，避免丢失日志
        }
    }
}

/// <summary>
/// 节流状态记录。独立成类以便与 Logger 解耦。
/// </summary>
internal static class ThrottleState
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> LastWrite = new();

    /// <summary>
    /// 判断给定标识是否已超过节流间隔，是则记录当前时间并返回 true。
    /// </summary>
    public static bool ShouldWrite(string key, TimeSpan interval)
    {
        var now = DateTime.UtcNow;
        var last = LastWrite.GetOrAdd(key, _ => DateTime.MinValue);
        if (now - last < interval)
        {
            return false;
        }

        LastWrite[key] = now;
        return true;
    }
}
