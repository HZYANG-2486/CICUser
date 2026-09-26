using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using CICUser.Services;

namespace CICUser.Views.SettingsPages;

/// <summary>
/// 「关于」独立设置页。集中展示插件身份与版本、用途简介、
/// 当前生效的接口端点，以及本机上的配置与日志位置，
/// 便于使用者了解插件用途、也便于排查问题时把环境信息提供给维护人员。
/// </summary>
[SettingsPageInfo(Plugin.AboutSettingsPageId, "关于", "\ue946", "\ue946")]
public partial class CICUserAboutPage : SettingsPageBase
{
    /// <summary>插件名称，界面与文案统一使用该常量。</summary>
    public const string DisplayName = Plugin.DisplayName;

    private PluginSettings _settings = new();

    public CICUserAboutPage()
    {
        InitializeComponent();
    }

    // ── 生命周期 ──

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _settings = PluginSettings.Load();

        LoadIdentity();
        LoadChannels();
        LoadPaths();
        LoadEndpoints();
        LoadSecurityState();
    }

    // ── 身份与版本 ──

    private void LoadIdentity()
    {
        AppNameText.Text = DisplayName;
        VersionBadgeText.Text = $"v{ReadPluginVersion()}";

        PluginVersionText.Text = ReadPluginVersion();
        PluginIdText.Text = ReadManifestValue("id", Plugin.PluginIdFallback);
        ApiVersionText.Text = ReadManifestValue("apiVersion", "—");
        TargetFrameworkText.Text = ReadTargetFramework();
        BuildTimeText.Text = ReadBuildTime();
        HostVersionText.Text = ReadHostVersion();
    }

    /// <summary>读取插件自身的程序集版本。</summary>
    private static string ReadPluginVersion()
    {
        try
        {
            var asm = typeof(CICUserAboutPage).Assembly;
            var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                // 去掉可能的 +commit 后缀，只保留语义化版本
                var plus = informational.IndexOf('+');
                return plus > 0 ? informational[..plus] : informational;
            }

            return asm.GetName().Version?.ToString() ?? "未知";
        }
        catch
        {
            return "未知";
        }
    }

    /// <summary>从随包分发的 manifest.yml 中读取指定字段。</summary>
    /// <remarks>
    /// 优先读取插件目录下的清单文件；读取失败时回落为默认值，
    /// 不因为读不到清单就整页空掉。
    /// </remarks>
    private static string ReadManifestValue(string key, string fallback)
    {
        try
        {
            var path = Path.Combine(SettingsDirectoryForManifest(), "manifest.yml");
            if (!File.Exists(path))
            {
                return fallback;
            }

            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                if (!line[..colon].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = line[(colon + 1)..].Trim().Trim('"', '\'');
                return string.IsNullOrWhiteSpace(value) ? fallback : value;
            }
        }
        catch
        {
            // 清单不可读时回落
        }

        return fallback;
    }

    /// <summary>插件程序集所在目录，用于定位 manifest.yml。</summary>
    private static string SettingsDirectoryForManifest()
    {
        try
        {
            var location = typeof(CICUserAboutPage).Assembly.Location;
            if (!string.IsNullOrEmpty(location))
            {
                return Path.GetDirectoryName(location) ?? "";
            }
        }
        catch
        {
            // 忽略
        }

        return AppContext.BaseDirectory;
    }

    /// <summary>读取编译时的目标框架标识。</summary>
    private static string ReadTargetFramework()
    {
        try
        {
            var asm = typeof(CICUserAboutPage).Assembly;
            var attr = asm.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>();
            return string.IsNullOrWhiteSpace(attr?.FrameworkName) ? "—" : attr!.FrameworkName!;
        }
        catch
        {
            return "—";
        }
    }

    /// <summary>读取插件 DLL 的构建时间。</summary>
    private static string ReadBuildTime()
    {
        try
        {
            var location = typeof(CICUserAboutPage).Assembly.Location;
            if (string.IsNullOrEmpty(location) || !File.Exists(location))
            {
                return "—";
            }

            return File.GetLastWriteTime(location).ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch
        {
            return "—";
        }
    }

    /// <summary>读取宿主 ClassIsland 的版本号。</summary>
    private static string ReadHostVersion()
    {
        try
        {
            // 宿主版本可从 ClassIsland.Core 程序集得知
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = asm.GetName().Name;
                if (name is "ClassIsland" or "ClassIsland.Core")
                {
                    var v = asm.GetName().Version;
                    if (v != null)
                    {
                        return v.ToString();
                    }
                }
            }
        }
        catch
        {
            // 忽略
        }

        return "—";
    }

    // ── 三通道速览 ──

    private void LoadChannels()
    {
        SetChannel(AboutStatusChannelText, AboutStatusChannelHint,
            _settings.AutoSendEnabled && _settings.StatusSendEnabled,
            _settings.StatusSendIntervalSeconds);

        SetChannel(AboutScheduleChannelText, AboutScheduleChannelHint,
            _settings.AutoSendEnabled && _settings.ScheduleSendEnabled,
            _settings.ScheduleSendIntervalSeconds);

        SetChannel(AboutHeartbeatChannelText, AboutHeartbeatChannelHint,
            _settings.AutoSendEnabled && _settings.HeartbeatEnabled,
            _settings.HeartbeatIntervalSeconds);
    }

    private static void SetChannel(TextBlock state, TextBlock hint, bool enabled, int seconds)
    {
        state.Text = enabled ? "已启用" : "已关闭";
        state.Foreground = new SolidColorBrush(Color.Parse(enabled ? "#1F9D55" : "#9A9A9A"));
        hint.Text = enabled ? FormatInterval(seconds) : "未参与上报";
    }

    /// <summary>把秒数格式化为更易读的间隔描述。</summary>
    internal static string FormatInterval(int seconds)
    {
        if (seconds < 60)
        {
            return $"每 {seconds} 秒";
        }

        if (seconds % 3600 == 0)
        {
            return $"每 {seconds / 3600} 小时";
        }

        if (seconds % 60 == 0)
        {
            return $"每 {seconds / 60} 分钟";
        }

        return $"每 {seconds} 秒";
    }

    // ── 路径 ──

    private void LoadPaths()
    {
        ConfigPathText.Text = PluginSettings.SettingsFilePath;
        LogPathText.Text = Logger.CurrentLogPath;
        DataDirPathText.Text = SafeDirectoryName(PluginSettings.SettingsFilePath);
    }

    private static string SafeDirectoryName(string filePath)
    {
        try
        {
            return Path.GetDirectoryName(filePath) ?? "—";
        }
        catch
        {
            return "—";
        }
    }

    // ── 接口端点 ──

    private void LoadEndpoints()
    {
        EndpointStatusText.Text = _settings.StatusTemplate.StatusEndpoint;
        EndpointScheduleText.Text = _settings.ScheduleTemplate.ScheduleEndpoint;
        EndpointHeartbeatText.Text = _settings.HeartbeatEndpoint;
        EndpointAuthRequestText.Text = _settings.AuthRequestEndpoint;
        EndpointAuthQueryText.Text = _settings.AuthQueryEndpoint;

        AuthModeHintText.Text = _settings.AuthCheckEnabled
            ? "授权检查当前为开启状态，未授权时插件不会上报。"
            : "授权检查当前为关闭状态，插件将直接开始上报（兼容未部署授权机制的平台）。";
    }

    // ── 安全状态 ──

    private void LoadSecurityState()
    {
        var mask = _settings.MaskSensitiveInLog ? "已开启" : "已关闭";
        var plain = _settings.AllowInsecureHttp ? "允许（仅建议内网使用）" : "已禁止";
        MaskStateHintText.Text =
            $"日志脱敏：{mask}。明文 HTTP：{plain}。请求超时 {_settings.RequestTimeoutSeconds} 秒，" +
            $"失败重试 {_settings.MaxRetryCount} 次。";
    }

    // ── 交互 ──

    private void OpenLogButton_Click(object? sender, RoutedEventArgs e)
        => SettingsDisplayHelper.OpenPath(Logger.CurrentLogPath, "日志文件尚未生成。", ShowMessage);

    private void OpenConfigFolderButton_Click(object? sender, RoutedEventArgs e)
        => SettingsDisplayHelper.OpenContainingFolder(
            PluginSettings.SettingsFilePath, ShowMessage);

    /// <summary>
    /// 复制一份环境信息到剪贴板。刻意不含设备ID、服务器地址等敏感内容，
    /// 保证粘贴到公开渠道求助时不会泄露部署细节。
    /// </summary>
    private void CopyInfoButton_Click(object? sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{DisplayName} 环境信息");
        sb.AppendLine($"插件版本：{ReadPluginVersion()}");
        sb.AppendLine($"插件 API 版本：{ReadManifestValue("apiVersion", "—")}");
        sb.AppendLine($"目标框架：{ReadTargetFramework()}");
        sb.AppendLine($"构建时间：{ReadBuildTime()}");
        sb.AppendLine($"宿主版本：{ReadHostVersion()}");
        sb.AppendLine($"配置路径：{PluginSettings.SettingsFilePath}");
        sb.AppendLine($"日志路径：{Logger.CurrentLogPath}");
        sb.AppendLine($"通道：状态={OnOff(_settings.StatusSendEnabled)}、" +
                      $"课表={OnOff(_settings.ScheduleSendEnabled)}、" +
                      $"心跳={OnOff(_settings.HeartbeatEnabled)}");
        sb.AppendLine($"授权检查：{OnOff(_settings.AuthCheckEnabled)}");
        sb.AppendLine($"日志脱敏：{OnOff(_settings.MaskSensitiveInLog)}");
        sb.AppendLine($"明文 HTTP：{OnOff(_settings.AllowInsecureHttp)}");

        var text = sb.ToString().TrimEnd();

        if (SettingsDisplayHelper.TrySetClipboardText(this, text))
        {
            ShowMessage("环境信息已复制到剪贴板。", isError: false);
        }
        else
        {
            ShowMessage("无法访问剪贴板，请手动选中路径文本复制。", isError: true);
        }
    }

    private static string OnOff(bool value) => value ? "开启" : "关闭";

    // ── 提示条 ──

    private void ShowMessage(string message, bool isError)
        => SettingsDisplayHelper.ShowMessage(MessageBar, MessageText, message, isError);
}
