using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;

namespace CICUser.Views.SettingsPages;

/// <summary>
/// CICUser 主设置页。把「上报与运行」放在页面最前，
/// 日常最关心的运行状态一眼可见；连接、授权与身份信息依次排在下方。
/// 报文模板、课表格式等偏底层的设置收纳在「高级设置」子页中。
/// </summary>
[SettingsPageInfo(Plugin.MainSettingsPageId, Plugin.DisplayName, "\ue7ba", "\ue7ba")]
public partial class CICUserSettingsPage : SettingsPageBase
{
    private PluginSettings _settings = new();

    /// <summary>状态刷新定时器。仅在页面加载后启动，卸载时停止。</summary>
    private DispatcherTimer? _statusTimer;

    /// <summary>多步撤回栈，快照为整份配置的 JSON。</summary>
    private readonly UndoStack<string> _undoStack = new(
        clone: x => x,
        serialize: x => x);

    /// <summary>上一次压栈时的配置快照，用于判断是否真的发生了变化。</summary>
    private string _lastSnapshot = "";

    public CICUserSettingsPage()
    {
        InitializeComponent();
    }

    // ── 生命周期 ──

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        LoadSettings();

        // 定时刷新运行状态。仅在页面加载期间运行，避免产生游离定时器。
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatusDisplay();
        _statusTimer.Start();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        _statusTimer?.Stop();
        _statusTimer = null;
    }

    // ── 加载与回显 ──

    private void LoadSettings()
    {
        _settings = PluginSettings.Load();

        // 配置可能来自旧版本而缺少设备ID，这里兜底生成一次
        if (_settings.EnsureDeviceId())
        {
            _settings.Save();
        }

        ServerUrlTextBox.Text = _settings.ServerUrl;
        DeviceIdTextBox.Text = _settings.DeviceId;
        DeviceNameTextBox.Text = _settings.DeviceName;
        ClassNameTextBox.Text = _settings.ClassName;

        UpdateDeviceIdHint();
        UpdateServerUrlHint();
        UpdateAuthStatus();
        UpdateChannelSummary();
        UpdateStatusDisplay();

        _lastSnapshot = Snapshot();
        _undoStack.Clear();
        UpdateUndoState();
    }

    /// <summary>刷新设备ID 下方的来源说明。</summary>
    private void UpdateDeviceIdHint()
    {
        var source = SettingsDisplayHelper.ParseSource(_settings.DeviceIdSource);
        var text = $"生成方式：{DeviceIdGenerator.GetSourceDisplayName(source)}。" +
                   DeviceIdGenerator.GetSourceDescription(source);

        if (_settings.DeviceIdManuallyEdited)
        {
            text = "该设备ID 已被手动修改。" + text;
            DeviceIdSourceHint.Foreground = new SolidColorBrush(Color.Parse("#C62828"));
        }
        else
        {
            DeviceIdSourceHint.Foreground = new SolidColorBrush(Color.Parse("#6B6B6B"));
        }

        DeviceIdSourceHint.Text = text;
    }

    /// <summary>
    /// 服务器地址下方的提示。地址格式错误或使用明文 HTTP 时给出醒目提示，
    /// 但不阻断保存，以免影响校园内网中常见的 http 部署。
    /// </summary>
    private void UpdateServerUrlHint()
    {
        var url = (_settings.ServerUrl ?? "").Trim();

        if (string.IsNullOrEmpty(url))
        {
            ServerUrlHint.Text = "";
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ServerUrlHint.Text = "地址格式不正确，需以 http:// 或 https:// 开头，否则无法上报。";
            ServerUrlHint.Foreground = new SolidColorBrush(Color.Parse("#C62828"));
            return;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !_settings.AllowInsecureHttp)
        {
            ServerUrlHint.Text = "当前使用明文 HTTP，但「允许明文 HTTP」已在高级设置中关闭，发送会被拒绝。";
            ServerUrlHint.Foreground = new SolidColorBrush(Color.Parse("#C62828"));
            return;
        }

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            ServerUrlHint.Text = $"将向 {url} 上报数据。明文 HTTP 适用于校园内网，但同网段设备可能窃听上报内容。";
            ServerUrlHint.Foreground = new SolidColorBrush(Color.Parse("#8A5300"));
            return;
        }

        ServerUrlHint.Text = $"将向 {url} 加密上报数据。";
        ServerUrlHint.Foreground = new SolidColorBrush(Color.Parse("#2E7D32"));
    }

    /// <summary>刷新三通道开关速览卡片。</summary>
    private void UpdateChannelSummary()
    {
        SetChannelDisplay(
            StatusChannelText, StatusChannelHint,
            _settings.StatusSendEnabled,
            $"每 {_settings.StatusSendIntervalSeconds} 秒");

        SetChannelDisplay(
            ScheduleChannelText, ScheduleChannelHint,
            _settings.ScheduleSendEnabled,
            $"每 {_settings.ScheduleSendIntervalSeconds} 秒");

        SetChannelDisplay(
            HeartbeatChannelText, HeartbeatChannelHint,
            _settings.HeartbeatEnabled,
            $"每 {_settings.HeartbeatIntervalSeconds} 秒");
    }

    /// <summary>设置单个通道卡片的启用态与间隔文字。</summary>
    private static void SetChannelDisplay(TextBlock stateText, TextBlock hintText,
        bool enabled, string intervalText)
    {
        stateText.Text = enabled ? "已启用" : "已停用";
        stateText.Foreground = new SolidColorBrush(
            Color.Parse(enabled ? "#2E7D32" : "#9E9E9E"));

        hintText.Text = enabled ? intervalText : "未参与定时上报";
    }

    // ── 撤回栈 ──

    /// <summary>把当前界面字段收集进配置对象并序列化为快照。</summary>
    private string Snapshot()
    {
        CommitConnectionFields();
        return JsonSerializer.Serialize(_settings, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    /// <summary>
    /// 在会改变配置的操作之前调用，记录一份可回退的快照。
    /// 内容与上次相同时不重复压栈。
    /// </summary>
    private void PushUndoSnapshot()
    {
        var current = Snapshot();
        if (current == _lastSnapshot)
        {
            return;
        }

        _undoStack.Push(_lastSnapshot);
        _lastSnapshot = current;
        UpdateUndoState();
    }

    private void UpdateUndoState()
    {
        UndoButton.IsEnabled = _undoStack.CanUndo;
        UndoHintText.Text = _undoStack.CanUndo
            ? $"「撤回上一步」可回退最近的改动（当前可撤回 {_undoStack.Count} 步）；「恢复默认」会把上报频率等设置还原，但保留服务器地址与设备ID。"
            : "「撤回上一步」可回退最近的改动（最多 20 步）；「恢复默认」会把上报频率等设置还原，但保留服务器地址与设备ID。";
    }

    private void UndoButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_undoStack.CanUndo)
        {
            ShowMessage("没有可撤回的改动。", isError: true);
            return;
        }

        var snapshot = _undoStack.Pop();
        if (string.IsNullOrEmpty(snapshot))
        {
            ShowMessage("撤回失败：快照已损坏。", isError: true);
            return;
        }

        PluginSettings? restored;
        try
        {
            restored = JsonSerializer.Deserialize<PluginSettings>(snapshot,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            ShowMessage($"撤回失败：{ex.Message}", isError: true);
            return;
        }

        if (restored == null)
        {
            ShowMessage("撤回失败：快照无法解析。", isError: true);
            return;
        }

        restored.Normalize();
        restored.Save();
        Plugin.ReloadSettings();

        _settings = restored;
        RefreshAllFromSettings();
        _lastSnapshot = Snapshot();
        UpdateUndoState();

        ShowMessage("已撤回上一步改动。", isError: false);
    }

    private void ResetButton_Click(object? sender, RoutedEventArgs e)
    {
        PushUndoSnapshot();

        // 保留身份与连接参数：服务器地址、设备ID、名称属于部署信息，
        // 恢复默认不应把它们清空，否则会连带断开与已授权服务端的关联。
        var preserved = new PluginSettings
        {
            ServerUrl = _settings.ServerUrl,
            DeviceId = _settings.DeviceId,
            DeviceIdSource = _settings.DeviceIdSource,
            DeviceIdManuallyEdited = _settings.DeviceIdManuallyEdited,
            DeviceName = _settings.DeviceName,
            ClassName = _settings.ClassName,
            AuthStatus = _settings.AuthStatus,
            AuthMessage = _settings.AuthMessage
        };

        preserved.Normalize();
        preserved.Save();
        Plugin.ReloadSettings();

        _settings = preserved;
        RefreshAllFromSettings();
        _lastSnapshot = Snapshot();
        UpdateUndoState();

        ShowMessage("已恢复默认设置（服务器地址、设备ID 与名称予以保留）。", isError: false);
    }

    /// <summary>把当前配置完整回显到界面控件。</summary>
    private void RefreshAllFromSettings()
    {
        ServerUrlTextBox.Text = _settings.ServerUrl;
        DeviceIdTextBox.Text = _settings.DeviceId;
        DeviceNameTextBox.Text = _settings.DeviceName;
        ClassNameTextBox.Text = _settings.ClassName;

        UpdateDeviceIdHint();
        UpdateServerUrlHint();
        UpdateAuthStatus();
        UpdateChannelSummary();
        UpdateStatusDisplay();
    }

    // ── 设备ID ──

    private void RegenerateDeviceIdButton_Click(object? sender, RoutedEventArgs e)
    {
        CommitConnectionFields();
        PushUndoSnapshot();

        var source = SettingsDisplayHelper.ParseSource(_settings.DeviceIdSource);

        // 「班级+设备名派生」需要先有名称，否则回退到机器码哈希
        if (source == DeviceIdSource.ClassNameDerived &&
            string.IsNullOrWhiteSpace(_settings.ClassName) &&
            string.IsNullOrWhiteSpace(_settings.DeviceName))
        {
            ShowMessage("请先填写班级名称或设备名称，再按此方式生成。", isError: true);
            return;
        }

        // 生成前先落盘，使生成结果能基于最新的班级名称
        _settings.Save();

        var generated = Plugin.RegenerateDeviceId(source);
        _settings = PluginSettings.Load();
        DeviceIdTextBox.Text = generated;
        UpdateDeviceIdHint();
        _lastSnapshot = Snapshot();

        ShowMessage($"已重新生成设备ID：{generated}。授权状态已重置，请重新申请授权。", isError: false);
    }

    // ── 授权 ──

    private void UpdateAuthStatus()
    {
        // 授权检查未开启时，说明该部署不在插件侧做授权判断，
        // 界面如实告知，避免用户误以为「待授权」会阻断上报。
        AuthCardHint.Text = _settings.AuthCheckEnabled
            ? "已启用授权检查：只有服务端返回已授权，插件才会发送数据。"
            : "当前未启用授权检查（可在高级设置中开启）。插件不会因授权状态阻断上报，是否接受数据由服务端决定。";

        switch (_settings.AuthStatus)
        {
            case "authorized":
                AuthStatusText.Text = "已授权";
                AuthStatusText.Foreground = new SolidColorBrush(Color.Parse("#2E7D32"));
                AuthStatusPill.Background = new SolidColorBrush(Color.Parse("#DCEDC8"));
                RequestAuthButton.IsEnabled = false;
                RequestAuthButton.Content = "已授权";
                break;

            case "rejected":
                AuthStatusText.Text = string.IsNullOrWhiteSpace(_settings.AuthMessage)
                    ? "已被拒绝"
                    : $"已被拒绝：{_settings.AuthMessage}";
                AuthStatusText.Foreground = new SolidColorBrush(Color.Parse("#C62828"));
                AuthStatusPill.Background = new SolidColorBrush(Color.Parse("#FFCDD2"));
                RequestAuthButton.IsEnabled = true;
                RequestAuthButton.Content = "重新申请授权";
                break;

            default:
                AuthStatusText.Text = "待授权";
                AuthStatusText.Foreground = new SolidColorBrush(Color.Parse("#E65100"));
                AuthStatusPill.Background = new SolidColorBrush(Color.Parse("#FFE0B2"));
                RequestAuthButton.IsEnabled = true;
                RequestAuthButton.Content = "申请授权";
                break;
        }
    }

    private async void RequestAuthButton_Click(object? sender, RoutedEventArgs e)
    {
        CommitConnectionFields();
        if (!ValidateRequiredFields(out var error))
        {
            ShowMessage(error, isError: true);
            return;
        }

        _settings.Save();
        Plugin.ReloadSettings();

        using var httpClient = new HttpClient();
        using var apiClient = new ApiClient(httpClient, _settings, ownsClient: false);

        RequestAuthButton.IsEnabled = false;
        var originalContent = RequestAuthButton.Content;
        RequestAuthButton.Content = "申请中…";

        try
        {
            var response = await apiClient.RequestAuthAsync();
            SettingsDisplayHelper.ApplyAuthResponse(_settings, response, ShowMessage);
            _settings = PluginSettings.Load();
            UpdateAuthStatus();
        }
        finally
        {
            RequestAuthButton.Content = originalContent;
            RequestAuthButton.IsEnabled = _settings.AuthStatus != "authorized";
            UpdateAuthStatus();
        }
    }

    private async void CheckAuthButton_Click(object? sender, RoutedEventArgs e)
    {
        CommitConnectionFields();
        if (!ValidateRequiredFields(out var error))
        {
            ShowMessage(error, isError: true);
            return;
        }

        CheckAuthButton.IsEnabled = false;
        CheckAuthButton.Content = "查询中…";

        try
        {
            using var httpClient = new HttpClient();
            using var apiClient = new ApiClient(httpClient, _settings, ownsClient: false);

            // 查询走独立接口，与申请解耦，避免把「查询」误当作一次新的申请
            var response = await apiClient.QueryAuthAsync();
            SettingsDisplayHelper.ApplyAuthResponse(_settings, response, ShowMessage);
            _settings = PluginSettings.Load();
            UpdateAuthStatus();
        }
        finally
        {
            CheckAuthButton.Content = "查询授权结果";
            CheckAuthButton.IsEnabled = true;
        }
    }

    // ── 字段收集与校验 ──

    private void CommitConnectionFields()
    {
        _settings.ServerUrl = (ServerUrlTextBox.Text ?? "").Trim();
        _settings.DeviceId = (DeviceIdTextBox.Text ?? "").Trim();
        _settings.DeviceName = (DeviceNameTextBox.Text ?? "").Trim();
        _settings.ClassName = (ClassNameTextBox.Text ?? "").Trim();
    }

    private bool ValidateRequiredFields(out string error)
    {
        if (string.IsNullOrWhiteSpace(_settings.ServerUrl))
        {
            error = "请填写服务器地址。";
            return false;
        }

        if (!Uri.TryCreate(_settings.ServerUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "服务器地址格式不正确，需以 http:// 或 https:// 开头。";
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !_settings.AllowInsecureHttp)
        {
            error = "服务器地址为明文 HTTP，而「允许明文 HTTP」已在高级设置中关闭。请改用 https:// 或开启该选项。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_settings.DeviceId))
        {
            error = "设备ID 为空，请点击「重新生成」。";
            return false;
        }

        error = "";
        return true;
    }

    // ── 操作按钮 ──

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        CommitConnectionFields();

        if (!ValidateRequiredFields(out var error))
        {
            ShowMessage(error, isError: true);
            return;
        }

        _settings.Save();
        Plugin.ReloadSettings();

        _lastSnapshot = Snapshot();
        UpdateUndoState();

        UpdateDeviceIdHint();
        UpdateServerUrlHint();
        UpdateAuthStatus();
        UpdateChannelSummary();
        ShowMessage("设置已保存并立即生效。", isError: false);
    }

    private void SendNowButton_Click(object? sender, RoutedEventArgs e)
    {
        CommitConnectionFields();
        if (!ValidateRequiredFields(out var error))
        {
            ShowMessage(error, isError: true);
            return;
        }

        _settings.Save();
        Plugin.ReloadSettings();
        Plugin.TriggerSend();
        ShowMessage("已触发上报，请稍候查看运行状态。", isError: false);
    }

    private void OpenLogButton_Click(object? sender, RoutedEventArgs e)
        => SettingsDisplayHelper.OpenPath(Logger.CurrentLogPath, "日志文件尚未生成。", ShowMessage);

    // ── 运行状态 ──

    private void UpdateStatusDisplay()
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusSendCountText.Text = Plugin.StatusSendCount.ToString();
            ScheduleSendCountText.Text = Plugin.ScheduleSendCount.ToString();
            HeartbeatSendCountText.Text = Plugin.HeartbeatSendCount.ToString();

            ScheduleVersionText.Text = Plugin.LastReportedScheduleVersion > 0
                ? $"课表版本 v{Plugin.LastReportedScheduleVersion}"
                : "课表版本 —";

            LessonCountText.Text = Plugin.LastReportedLessonCount > 0
                ? $"{Plugin.LastReportedLessonCount} 节"
                : "—";

            if (!string.IsNullOrWhiteSpace(Plugin.LastSendResult))
            {
                LastSendResultText.Text = Plugin.LastSendResult;
            }

            UpdateRunningIndicator();
        });
    }

    /// <summary>
    /// 运行指示灯：根据「总开关 + 是否有最近成功上报」给出直观状态。
    /// </summary>
    private void UpdateRunningIndicator()
    {
        var totalSends = Plugin.StatusSendCount + Plugin.ScheduleSendCount + Plugin.HeartbeatSendCount;

        string text;
        string color;

        if (!_settings.AutoSendEnabled)
        {
            text = "自动上报已关闭";
            color = "#9E9E9E";
        }
        else if (string.IsNullOrWhiteSpace(_settings.ServerUrl)
                 || string.IsNullOrWhiteSpace(_settings.DeviceId))
        {
            text = "待配置";
            color = "#E65100";
        }
        else if (totalSends > 0)
        {
            text = "运行中";
            color = "#2E7D32";
        }
        else
        {
            text = "等待首次上报";
            color = "#E65100";
        }

        RunningStateText.Text = text;
        RunningStateText.Foreground = new SolidColorBrush(Color.Parse(color));
        RunningDot.Fill = new SolidColorBrush(Color.Parse(color));
        RunningPill.Background = new SolidColorBrush(Color.Parse($"{color}22"));
    }

    // ── 提示条 ──

    private void ShowMessage(string message, bool isError)
        => SettingsDisplayHelper.ShowMessage(MessageBorder, MessageText, message, isError);
}
