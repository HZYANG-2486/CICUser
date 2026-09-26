using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Shared;
using ClassIsland.Shared.Enums;
using ClassIsland.Shared.Models.Profile;
using CICUser.Actions;
using CICUser.Services;
using CICUser.Views.SettingsPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CICUser;

/// <summary>
/// CICUser 插件。负责把当前课堂状态与当日课表上报到自建服务端，
/// 并在课表发生调整（换课、调课、时间改动）时立即重传最新版本。
/// </summary>
[PluginEntrance]
public class Plugin : PluginBase
{
    private static Plugin? _instance;

    private PluginSettings _settings = new();
    private ILessonsService? _lessonsService;
    private IProfileService? _profileService;
    private ApiClient? _apiClient;
    private HttpClient? _httpClient;

    /// <summary>状态上报的节流时间戳。仅在真正发起请求时更新。</summary>
    private DateTime _lastStatusSendTime = DateTime.MinValue;

    /// <summary>课表定时上报的节流时间戳（与变更即时通道相互独立）。</summary>
    private DateTime _lastScheduleTimerSendTime = DateTime.MinValue;

    /// <summary>心跳上报的节流时间戳。</summary>
    private DateTime _lastHeartbeatSendTime = DateTime.MinValue;

    /// <summary>课表上报的节流时间戳，避免变更风暴导致重复请求。</summary>
    private DateTime _lastScheduleSendTime = DateTime.MinValue;

    /// <summary>课表变更后的合并延迟，用于吞掉短时间内的连续改动。</summary>
    private static readonly TimeSpan ScheduleChangeDebounce = TimeSpan.FromSeconds(3);

    // ── 通道并发闸门 ──
    // 宿主计时器约 20Hz 触发，而一次上报可能因超时+退避耗时数十秒。
    // 没有闸门时同一通道会出现重叠请求：两条报文同时在途、各自递增版本号与计数，
    // 轻则重复上报，重则课表版本跳跃。用「是否已在执行」的交换标记串行化，
    // 在途请求未结束时后续触发直接跳过，不做排队。

    /// <summary>状态通道是否正在发送。</summary>
    private int _statusSending;

    /// <summary>课表通道是否正在发送。</summary>
    private int _scheduleSending;

    /// <summary>心跳通道是否正在发送。</summary>
    private int _heartbeatSending;

    /// <summary>
    /// 尝试进入某通道。成功返回 true，调用方负责在 <c>finally</c> 中调用
    /// <see cref="ExitChannel"/> 释放，否则该通道会永久卡死。
    /// </summary>
    /// <param name="gate">通道标记字段的引用。</param>
    private static bool TryEnterChannel(ref int gate)
        => Interlocked.CompareExchange(ref gate, 1, 0) == 0;

    /// <summary>释放通道标记。</summary>
    /// <param name="gate">通道标记字段的引用。</param>
    private static void ExitChannel(ref int gate)
        => Interlocked.Exchange(ref gate, 0);

    /// <summary>已订阅变更通知的课表对象，用于换课时正确退订。</summary>
    private ClassPlan? _subscribedClassPlan;

    private readonly object _scheduleLock = new();
    private CancellationTokenSource? _scheduleDebounceCts;

    // ── 供设置页读取的运行时统计 ──

    /// <summary>状态上报成功次数。</summary>
    public static int StatusSendCount { get; private set; }

    /// <summary>课表上报成功次数。</summary>
    public static int ScheduleSendCount { get; private set; }

    /// <summary>心跳上报成功次数。</summary>
    public static int HeartbeatSendCount { get; private set; }

    /// <summary>最近一次上报结果描述。</summary>
    public static string? LastSendResult { get; private set; }

    /// <summary>最近一次上报发生的时间。</summary>
    public static DateTime? LastSendTime { get; private set; }

    /// <summary>最近一次课表上报的版本号。</summary>
    public static int LastReportedScheduleVersion { get; private set; }

    /// <summary>当前已上报的课表节数。</summary>
    public static int LastReportedLessonCount { get; private set; }

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        // 主设置页与高级设置页共用同一个导航分组，形成左侧导航的两级树。
        // 分组 API 在新宿主中才提供，编译期 SDK 尚无，故用反射调用，
        // 不支持的宿主会自动退化为平铺显示。
        SettingsPageGroupHelper.TryAddGroup(services, SettingsGroupId, "\ue7ba", DisplayName);

        services.AddSettingsPage<CICUserSettingsPage>();
        services.AddSettingsPage<CICUserAdvancedSettingsPage>();
        services.AddSettingsPage<CICUserAboutPage>();

        SettingsPageGroupHelper.TryAssignGroup(typeof(CICUserSettingsPage), SettingsGroupId);
        SettingsPageGroupHelper.TryAssignGroup(typeof(CICUserAdvancedSettingsPage), SettingsGroupId);
        SettingsPageGroupHelper.TryAssignGroup(typeof(CICUserAboutPage), SettingsGroupId);

        // 注册自动化行动，供用户在自动化流程中调用
        services.AddAction<ReportStatusAction>();

        _settings = PluginSettings.Load();
        Logger.MaskSensitive = _settings.MaskSensitiveInLog;

        // 首次运行或配置缺失时自动生成设备ID 并落盘。
        // 已有值一律保留，避免覆盖老用户的手填标识。
        if (_settings.EnsureDeviceId())
        {
            _settings.Save();
        }

        _instance = this;
        Logger.Info($"{DisplayName} 插件初始化完成");

        AppBase.Current.AppStarted += OnAppStarted;
        AppBase.Current.AppStopping += OnAppStopping;
    }

    /// <summary>设置页导航分组 ID，主页面与高级设置页共用。</summary>
    public const string SettingsGroupId = "classisland.cicuser";

    /// <summary>主设置页 ID，供高级设置页返回时跳转使用。</summary>
    public const string MainSettingsPageId = "classisland.cicuser.settings";

    /// <summary>关于页 ID。</summary>
    public const string AboutSettingsPageId = "classisland.cicuser.about";

    /// <summary>
    /// 插件清单 ID。该值一旦发布即不能更改，
    /// 否则宿主会把它当成另一个插件，已有用户的配置与授权状态都会失效。
    /// </summary>
    public const string PluginIdFallback = "CICUser";

    /// <summary>插件显示名，界面与文案统一使用该常量。</summary>
    public const string DisplayName = "CICUser";

    /// <summary>
    /// 重新生成设备ID 并立即生效，供设置页调用。
    /// </summary>
    /// <param name="source">生成方式。</param>
    /// <returns>生成的设备ID。</returns>
    public static string RegenerateDeviceId(DeviceIdSource source)
    {
        if (_instance == null)
        {
            return "";
        }

        var settings = _instance._settings;
        var generated = DeviceIdGenerator.Generate(source, settings.ClassName, settings.DeviceName);

        settings.DeviceId = generated;
        settings.DeviceIdSource = source.ToString();
        settings.DeviceIdManuallyEdited = false;

        // 设备标识变化后原授权不再适用
        settings.AuthStatus = "pending";
        settings.AuthMessage = "";
        settings.Save();
        ReloadSettings();

        Logger.Info($"设备ID 已重新生成（方式：{DeviceIdGenerator.GetSourceDisplayName(source)}）");
        return generated;
    }

    /// <summary>
    /// 手动设定设备ID，供高级设置页在用户确认警示后调用。
    /// </summary>
    /// <returns>校验失败时返回错误描述，成功返回 null。</returns>
    public static string? SetDeviceIdManually(string deviceId)
    {
        if (_instance == null)
        {
            return "插件尚未初始化。";
        }

        var error = DeviceIdValidator.Validate(deviceId);
        if (error != null)
        {
            return error;
        }

        var settings = _instance._settings;
        settings.DeviceId = deviceId.Trim();
        settings.DeviceIdSource = nameof(DeviceIdSource.Manual);
        settings.DeviceIdManuallyEdited = true;

        settings.AuthStatus = "pending";
        settings.AuthMessage = "";
        settings.Save();
        ReloadSettings();

        Logger.Warning($"设备ID 已被手动修改为 {settings.DeviceId}，服务端会将其识别为新设备");
        return null;
    }

    private void OnAppStarted(object? sender, EventArgs e)
    {
        _httpClient = new HttpClient();
        _apiClient = new ApiClient(_httpClient, _settings, ownsClient: false);

        _lessonsService = IAppHost.GetService<ILessonsService>();
        _profileService = IAppHost.GetService<IProfileService>();

        _lessonsService.OnClass += OnClassChanged;
        _lessonsService.OnBreakingTime += OnBreakingTime;
        _lessonsService.OnAfterSchool += OnAfterSchool;
        _lessonsService.PostMainTimerTicked += OnTimerTicked;

        SubscribeScheduleChanges();

        Logger.Info("已订阅课堂事件与课表变更");
        _ = SendScheduleAsync("启动");
        _ = SendStatusAsync();
    }

    private void OnAppStopping(object? sender, EventArgs e)
    {
        UnsubscribeScheduleChanges();

        if (_lessonsService != null)
        {
            _lessonsService.OnClass -= OnClassChanged;
            _lessonsService.OnBreakingTime -= OnBreakingTime;
            _lessonsService.OnAfterSchool -= OnAfterSchool;
            _lessonsService.PostMainTimerTicked -= OnTimerTicked;
        }

        _scheduleDebounceCts?.Cancel();
        _scheduleDebounceCts?.Dispose();
        _scheduleDebounceCts = null;
        _apiClient?.Dispose();
        _apiClient = null;
        _httpClient = null;
    }

    /// <summary>
    /// 供设置页「立即发送」按钮调用。
    /// </summary>
    public static void TriggerSend()
    {
        if (_instance == null)
        {
            return;
        }

        Logger.Info("用户手动触发数据发送");
        _instance.ResendAll(reason: "手动发送");
    }

    /// <summary>
    /// 重新加载配置，使界面上的改动立即生效。
    /// </summary>
    public static void ReloadSettings()
    {
        if (_instance == null)
        {
            return;
        }

        _instance._settings = PluginSettings.Load();
        _instance._lastStatusSendTime = DateTime.MinValue;
        _instance._lastScheduleTimerSendTime = DateTime.MinValue;
        _instance._lastHeartbeatSendTime = DateTime.MinValue;
        _instance._lastScheduleSendTime = DateTime.MinValue;
        Logger.MaskSensitive = _instance._settings.MaskSensitiveInLog;
        Logger.Info("配置已重新加载并立即生效");
    }

    /// <summary>
    /// 预览当前配置将要发送的状态报文，供设置页展示。
    /// </summary>
    public static string PreviewStatusMessage()
        => _instance?.BuildStatusMessage() ?? "{}";

    /// <summary>
    /// 预览当前配置将要发送的课表报文，供设置页展示。
    /// </summary>
    public static string PreviewScheduleMessage()
        => _instance?.BuildScheduleMessage(out _) ?? "{}";

    /// <summary>
    /// 用样例课表预览指定格式的课表结构，供设置页在无课时也能看到效果。
    /// 返回缩进后的 JSON 片段。
    /// </summary>
    /// <param name="format">目标格式预设。</param>
    /// <param name="itemFields">字段级自定义定义；为空时使用默认字段。</param>
    /// <param name="indexKeyPrefix">按节次字典格式下的键名前缀。</param>
    public static string PreviewScheduleFormat(
        ScheduleFormat format,
        IReadOnlyList<MessageField>? itemFields,
        string? indexKeyPrefix)
        => ScheduleFormatter.Preview(
            ScheduleFormatter.CreateSampleSchedule(), format, itemFields, indexKeyPrefix);

    // ── 课表变更监听 ──

    /// <summary>
    /// 订阅当前课表的变更事件。换课时 CurrentClassPlan 会变化，
    /// 因此每次上报课表后都会重新绑定到最新的课表对象。
    /// </summary>
    private void SubscribeScheduleChanges()
    {
        if (!_settings.RealtimeScheduleSync || _lessonsService == null)
        {
            return;
        }

        var plan = _lessonsService.CurrentClassPlan as ClassPlan;
        if (ReferenceEquals(plan, _subscribedClassPlan))
        {
            return;
        }

        UnsubscribeScheduleChanges();

        if (plan == null)
        {
            return;
        }

        plan.ClassesChanged += OnClassPlanChanged;
        plan.PropertyChanged += OnClassPlanPropertyChanged;

        foreach (var classInfo in plan.Classes)
        {
            classInfo.PropertyChanged += OnClassInfoPropertyChanged;
        }

        _subscribedClassPlan = plan;
        Logger.Debug($"已订阅课表变更：{plan.Name}");
    }

    private void UnsubscribeScheduleChanges()
    {
        if (_subscribedClassPlan == null)
        {
            return;
        }

        try
        {
            _subscribedClassPlan.ClassesChanged -= OnClassPlanChanged;
            _subscribedClassPlan.PropertyChanged -= OnClassPlanPropertyChanged;

            foreach (var classInfo in _subscribedClassPlan.Classes)
            {
                classInfo.PropertyChanged -= OnClassInfoPropertyChanged;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("退订课表变更事件失败", ex);
        }

        _subscribedClassPlan = null;
    }

    private void OnClassPlanChanged(object? sender, EventArgs e)
    {
        // 课程集合变化后，需同步更新课程项的订阅
        if (sender is ClassPlan plan)
        {
            foreach (var classInfo in plan.Classes)
            {
                classInfo.PropertyChanged -= OnClassInfoPropertyChanged;
                classInfo.PropertyChanged += OnClassInfoPropertyChanged;
            }
        }

        ScheduleChangeDetected("课表内容变更");
    }

    private void OnClassPlanPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClassPlan.Classes) or nameof(ClassPlan.TimeLayout))
        {
            ScheduleChangeDetected($"课表属性变更（{e.PropertyName}）");
        }
    }

    private void OnClassInfoPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 换课、调课、启用状态变化都属于需要重传的场景
        if (e.PropertyName is nameof(ClassInfo.SubjectId)
            or nameof(ClassInfo.IsChangedClass)
            or nameof(ClassInfo.IsEnabled)
            or nameof(ClassInfo.CurrentTimeLayoutItem))
        {
            ScheduleChangeDetected($"课程项变更（{e.PropertyName}）");
        }
    }

    /// <summary>
    /// 检测到课表变更。做短延迟合并后触发上报，
    /// 避免一次编辑操作触发多次请求。
    /// </summary>
    private void ScheduleChangeDetected(string reason)
    {
        if (!_settings.RealtimeScheduleSync)
        {
            return;
        }

        lock (_scheduleLock)
        {
            _scheduleDebounceCts?.Cancel();
            _scheduleDebounceCts?.Dispose();
            _scheduleDebounceCts = new CancellationTokenSource();
            var token = _scheduleDebounceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(ScheduleChangeDebounce, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    Logger.Info($"检测到{reason}，立即上报最新课表");
                    await SendScheduleAsync($"课表变更：{reason}", force: true).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 被后续变更合并，属正常流程
                }
                catch (Exception ex)
                {
                    Logger.Error("课表变更上报异常", ex);
                }
            }, token);
        }
    }

    // ── 课堂事件 ──

    private void OnClassChanged(object? sender, EventArgs e)
    {
        Logger.Info("上课事件触发");
        Dispatch(SendStatusAsync(), "上课事件");
    }

    private void OnBreakingTime(object? sender, EventArgs e)
    {
        Logger.Info("课间休息事件触发");
        Dispatch(SendStatusAsync(), "课间事件");
    }

    private void OnAfterSchool(object? sender, EventArgs e)
    {
        Logger.Info("放学事件触发");
        Dispatch(SendStatusAsync(), "放学事件");
    }

    /// <summary>
    /// 主计时器回调（宿主约 20Hz 触发）。此处仅做时间判断与分发，
    /// 三个通道（状态 / 课表 / 心跳）各自独立判断开关与间隔。
    /// </summary>
    /// <remarks>
    /// 三个通道用<b>并发分发</b>而非依次 <c>await</c>：一次超时重试最长可达
    /// 数十秒，串行等待会让课表与心跳被状态通道拖住，心跳在服务端看起来就是掉线。
    /// 各通道内部已有互斥闸门与异常兜底，并发触发是安全的。
    /// </remarks>
    private void OnTimerTicked(object? sender, EventArgs e)
    {
        try
        {
            if (!_settings.AutoSendEnabled)
            {
                return;
            }

            var now = DateTime.Now;
            var interval = Math.Max(_settings.StatusSendIntervalSeconds,
                PluginSettings.MinStatusIntervalSeconds);

            // ① 状态通道
            if (_settings.StatusSendEnabled &&
                (now - _lastStatusSendTime).TotalSeconds >= interval)
            {
                Dispatch(SendStatusAsync(), "状态通道");
            }

            // ② 课表通道（定时兜底；变更即时通道独立于此处）
            var scheduleInterval = Math.Max(_settings.ScheduleSendIntervalSeconds,
                PluginSettings.MinScheduleIntervalSeconds);
            if (_settings.ScheduleSendEnabled &&
                (now - _lastScheduleTimerSendTime).TotalSeconds >= scheduleInterval)
            {
                _lastScheduleTimerSendTime = now;
                Dispatch(SendScheduleAsync("定时上报"), "课表通道");
            }

            // ③ 心跳通道
            var hbInterval = Math.Max(_settings.HeartbeatIntervalSeconds,
                PluginSettings.MinHeartbeatIntervalSeconds);
            if (_settings.HeartbeatEnabled &&
                (now - _lastHeartbeatSendTime).TotalSeconds >= hbInterval)
            {
                Dispatch(SendHeartbeatAsync(), "心跳通道");
            }
        }
        catch (Exception ex)
        {
            // 计时器回调异常不得逃逸，否则会连带影响宿主计时器
            Logger.Error("定时上报分发异常", ex);
        }
    }

    /// <summary>
    /// 以「即发即忘」方式启动一次上报，并保证其异常不会无人接手。
    /// </summary>
    /// <remarks>
    /// 各 <c>Send*Async</c> 内部已自行 try/catch，这里再兜一层是为了防止
    /// 同步阶段（如构造报文时）抛出的异常逃逸成未观察任务异常。
    /// </remarks>
    private void Dispatch(Task task, string scene)
    {
        _ = task.ContinueWith(t =>
        {
            var error = t.Exception?.GetBaseException();
            if (error != null)
            {
                Logger.Error($"{scene}未观察异常", error);
            }
        }, TaskScheduler.Default);
    }

    // ── 上报逻辑 ──

    /// <summary>
    /// 同时重发课表与状态，用于手动触发或配置变更后刷新。
    /// </summary>
    private void ResendAll(string reason)
    {
        _lastStatusSendTime = DateTime.MinValue;
        _lastScheduleTimerSendTime = DateTime.MinValue;
        _lastHeartbeatSendTime = DateTime.MinValue;
        _lastScheduleSendTime = DateTime.MinValue;
        Dispatch(SendScheduleAsync(reason, force: true), "课表通道");
        Dispatch(SendStatusAsync(), "状态通道");
        Dispatch(SendHeartbeatAsync(), "心跳通道");
    }

    /// <summary>
    /// 构造状态报文所需的运行时变量。
    /// </summary>
    private Dictionary<string, object?> BuildStatusRuntimeValues()
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (_lessonsService == null)
        {
            return values;
        }

        var subject = NormalizeSubject(_lessonsService.CurrentSubject?.Name);
        var nextSubject = NormalizeSubject(_lessonsService.NextClassSubject?.Name);

        string state;
        string remainingTime;
        switch (_lessonsService.CurrentState)
        {
            case TimeState.OnClass:
                state = "OnClass";
                remainingTime = FormatDuration(ComputeRemainingToPeriodEnd());
                break;
            case TimeState.Breaking:
                state = "OffClass";
                remainingTime = FormatDuration(ComputeRemainingToPeriodEnd());
                break;
            case TimeState.AfterSchool:
                state = "OffClass";
                remainingTime = "0分0秒";
                break;
            default:
                state = "OffClass";
                remainingTime = "0分0秒";
                break;
        }

        var currentLayout = _lessonsService.CurrentTimeLayoutItem;
        string timeRange;
        if (currentLayout != null && !currentLayout.Equals(TimeLayoutItem.Empty))
        {
            // TimeType == 0 为上课时段，== 1 为课间休息。
            // 课间时 CurrentSelectedIndex 指向的是上一节课，直接写「第N节」会误导，
            // 因此课间单独标注。
            var slot = _lessonsService.CurrentSelectedIndex + 1;
            var prefix = currentLayout.TimeType == 1
                ? (string.IsNullOrWhiteSpace(currentLayout.BreakNameText) ? "课间" : currentLayout.BreakNameText)
                : (slot > 0 ? $"第{slot}节" : "上课");

            timeRange = $"{prefix} {currentLayout.StartTime:hh\\:mm}-{currentLayout.EndTime:hh\\:mm}";
        }
        else
        {
            timeRange = "空闲";
        }

        var isChanged = false;
        try
        {
            var plan = _lessonsService.CurrentClassPlan;
            var idx = _lessonsService.CurrentSelectedIndex;
            if (plan != null && idx >= 0 && idx < plan.Classes.Count)
            {
                isChanged = plan.Classes[idx].IsChangedClass;
            }
        }
        catch
        {
            // 课表未就绪时忽略
        }

        values["Subject"] = subject;
        values["NextSubject"] = nextSubject;
        values["State"] = state;
        values["RemainingTime"] = remainingTime;
        values["TimeRange"] = timeRange;
        values["SendTime"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        values["ClassPlanName"] = _lessonsService.CurrentClassPlan?.Name ?? "";
        values["ChangedClass"] = isChanged ? "true" : "false";

        return values;
    }

    private string BuildStatusMessage()
    {
        var values = BuildStatusRuntimeValues();
        var template = _settings.StatusTemplate;
        return MessageBuilder.Build(template.StatusFields, values, _settings.GetGlobalValues());
    }

    /// <summary>
    /// 发送前的统一前置检查：配置是否就绪、授权是否需要放行。
    /// </summary>
    /// <param name="scene">场景名，用于日志。</param>
    /// <param name="logKey">日志节流键。</param>
    /// <returns>可以发送返回 true。</returns>
    internal bool CanSend(string scene, string logKey)
    {
        if (_apiClient == null || _lessonsService == null)
        {
            Logger.Throttled($"{logKey}-not-ready", TimeSpan.FromMinutes(1),
                $"{scene}已跳过：服务尚未就绪");
            return false;
        }

        if (string.IsNullOrWhiteSpace(_settings.ServerUrl)
            || string.IsNullOrWhiteSpace(_settings.DeviceId))
        {
            Logger.Throttled($"{logKey}-no-config", TimeSpan.FromMinutes(1),
                $"{scene}已跳过：尚未填写服务器地址或设备ID");
            return false;
        }

        // 授权检查仅在开启时生效；未开启则一律放行，
        // 以便在未实现授权机制的服务端上正常上报。
        if (_settings.AuthCheckEnabled
            && !string.Equals(_settings.AuthStatus, "authorized", StringComparison.OrdinalIgnoreCase))
        {
            Logger.Throttled($"{logKey}-unauthorized", TimeSpan.FromMinutes(1),
                $"{scene}已跳过：授权未通过（当前状态 {_settings.AuthStatus}）");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 心跳保活：发送一个轻量报文，仅含设备标识与在线状态，不含课表内容。
    /// </summary>
    private async Task SendHeartbeatAsync()
    {
        // 上一轮尚未结束就跳过本次，避免心跳在网络上堆积
        if (!TryEnterChannel(ref _heartbeatSending))
        {
            return;
        }

        try
        {
            _lastHeartbeatSendTime = DateTime.Now;

            if (!CanSend("心跳上报", "heartbeat"))
            {
                return;
            }

            try
            {
                var payload = BuildHeartbeatPayload();
                var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
                var response = await _apiClient!
                    .PostRawAsync(_settings.HeartbeatEndpoint, json)
                    .ConfigureAwait(false);

                if (response.IsSuccess)
                {
                    HeartbeatSendCount++;
                    Logger.Debug("心跳上报成功");
                }
                else
                {
                    Logger.Throttled("heartbeat-fail", TimeSpan.FromMinutes(1),
                        $"心跳上报未成功：Code={response.Code}，Msg={response.Message}");
                }

                HandleAuthRejection(response, "心跳上报");
            }
            catch (Exception ex)
            {
                Logger.Error("心跳上报异常", ex);
            }
        }
        finally
        {
            ExitChannel(ref _heartbeatSending);
        }
    }

    /// <summary>
    /// 构造心跳保活报文：设备标识 + 在线状态 + 时间。
    /// </summary>
    internal Dictionary<string, object?> BuildHeartbeatPayload()
    {
        var global = _settings.GetGlobalValues();
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["DeviceId"] = global.TryGetValue("DeviceId", out var id) ? id : _settings.DeviceId,
            ["Online"] = true,
            ["SendTime"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };

        if (!string.IsNullOrWhiteSpace(_settings.DeviceName))
        {
            payload["DeviceName"] = _settings.DeviceName;
        }

        if (!string.IsNullOrWhiteSpace(_settings.ClassName))
        {
            payload["ClassName"] = _settings.ClassName;
        }

        return payload;
    }

    private async Task SendStatusAsync()
    {
        // 上一轮状态报文仍在途时直接跳过。状态是「快照」语义，
        // 后发的报文天然覆盖先发的，因此丢掉本次比排队更有意义。
        if (!TryEnterChannel(ref _statusSending))
        {
            return;
        }

        try
        {
            // 先更新时间戳，确保无论后续走哪个分支都不会被高频重复触发
            _lastStatusSendTime = DateTime.Now;

            if (!CanSend("状态上报", "status"))
            {
                return;
            }

            try
            {
                var values = BuildStatusRuntimeValues();
                Logger.Debug($"准备上报状态：科目={values["Subject"]}，状态={values["State"]}，" +
                             $"剩余={values["RemainingTime"]}，时段={values["TimeRange"]}");

                var response = await _apiClient!.SubmitStatusAsync(values).ConfigureAwait(false);

                LastSendTime = DateTime.Now;
                LastSendResult = $"[{LastSendTime:HH:mm:ss}] 状态上报：{FormatResult(response)}";

                if (response.IsSuccess)
                {
                    StatusSendCount++;
                    Logger.Debug($"状态上报成功：{response.Message}");
                }
                else
                {
                    Logger.Warning($"状态上报未成功：Code={response.Code}，Msg={response.Message}");
                }

                HandleAuthRejection(response, "状态上报");
            }
            catch (Exception ex)
            {
                Logger.Error("状态上报异常", ex);
                LastSendResult = $"[{DateTime.Now:HH:mm:ss}] 状态上报失败：{ex.Message}";
            }
        }
        finally
        {
            ExitChannel(ref _statusSending);
        }
    }

    /// <summary>
    /// 构造当日课表并上报。返回是否真正执行了网络请求。
    /// </summary>
    private async Task<bool> SendScheduleAsync(string reason, bool force = false)
    {
        // 课表通道串行化。课表报文带版本号，两轮并发会各自递增版本、
        // 服务端按到达顺序落库，先发的反而覆盖后发的，导致状态回退。
        if (!TryEnterChannel(ref _scheduleSending))
        {
            Logger.Throttled("schedule-busy", TimeSpan.FromSeconds(30),
                $"课表上报已跳过（{reason}）：上一轮仍在进行中");
            return false;
        }

        try
        {
            if (!CanSend("课表上报", "schedule"))
            {
                return false;
            }

            try
            {
                var schedule = BuildTodaySchedule(out var lessonCount, out _);
                if (schedule.Count == 0)
                {
                    Logger.Info("今日无课程安排，跳过课表上报");
                    return false;
                }

                // 内容未变化时跳过，避免无意义的重复请求
                var hash = ComputeHash(schedule);
                if (!force && _settings.SkipUnchangedSchedule && hash == _settings.LastScheduleHash)
                {
                    Logger.Throttled("schedule-unchanged", TimeSpan.FromMinutes(5),
                        "课表内容未变化，跳过上报");
                    return false;
                }

                _settings.ScheduleVersion++;
                _lastScheduleSendTime = DateTime.Now;

                var runtimeValues = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Date"] = DateTime.Today.ToString("yyyy-MM-dd"),
                    ["LessonCount"] = lessonCount,
                    ["Version"] = _settings.ScheduleVersion,
                    ["UpdateTime"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };

                Logger.Info($"准备上报课表（{reason}）：共 {lessonCount} 节，版本 v{_settings.ScheduleVersion}");

                var response = await _apiClient!.UpdateScheduleAsync(schedule, runtimeValues).ConfigureAwait(false);

                LastSendTime = DateTime.Now;
                LastSendResult = $"[{LastSendTime:HH:mm:ss}] 课表上报：{FormatResult(response)}";

                if (response.IsSuccess)
                {
                    ScheduleSendCount++;
                    LastReportedScheduleVersion = _settings.ScheduleVersion;
                    LastReportedLessonCount = lessonCount;

                    // 仅在服务端确认后才记录哈希与版本，失败时可自动重试
                    _settings.LastScheduleHash = hash;
                    _settings.Save();

                    Logger.Info($"课表上报成功：共 {lessonCount} 节，版本 v{_settings.ScheduleVersion}");
                }
                else
                {
                    // 上报失败时回退版本号，避免服务端版本跳跃
                    _settings.ScheduleVersion--;
                    Logger.Warning($"课表上报未成功：Code={response.Code}，Msg={response.Message}");
                }

                HandleAuthRejection(response, "课表上报");
                SubscribeScheduleChanges();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("课表上报异常", ex);
                LastSendResult = $"[{DateTime.Now:HH:mm:ss}] 课表上报失败：{ex.Message}";
                return false;
            }
        }
        finally
        {
            ExitChannel(ref _scheduleSending);
        }
    }

    /// <summary>
    /// 取得课表格式的解析结果（预设 + 字段级自定义 + 键前缀）。
    /// </summary>
    private (ScheduleFormat Format, List<MessageField> Fields, string Prefix) GetScheduleFormatOptions()
        => (ScheduleFormatCatalog.Parse(_settings.ScheduleFormat),
            _settings.ScheduleItemFields,
            _settings.ScheduleIndexKeyPrefix ?? "");

    private string? BuildScheduleMessage(out int lessonCount)
    {
        var schedule = BuildTodaySchedule(out lessonCount, out _);
        if (schedule.Count == 0)
        {
            return null;
        }

        var runtimeValues = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Date"] = DateTime.Today.ToString("yyyy-MM-dd"),
            ["LessonCount"] = lessonCount,
            ["Version"] = _settings.ScheduleVersion + 1,
            ["UpdateTime"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };

        var template = _settings.ScheduleTemplate;
        var (format, itemFields, prefix) = GetScheduleFormatOptions();
        var data = ScheduleFormatter.Convert(schedule, format, itemFields, prefix);

        return MessageBuilder.Build(template.ScheduleFields, runtimeValues,
            _settings.GetGlobalValues(), template.ScheduleDataKey, data);
    }

    /// <summary>
    /// 读取当日课表并转换为待上报的字典结构。
    /// 键为节次（从 1 开始），值为 [科目, 开始时间, 结束时间, 是否换课]。
    /// </summary>
    private Dictionary<string, string[]> BuildTodaySchedule(out int lessonCount, out string planName)
    {
        lessonCount = 0;
        planName = "";

        if (_lessonsService == null)
        {
            return new Dictionary<string, string[]>();
        }

        try
        {
            var todayPlan = _lessonsService.GetClassPlanByDate(DateTime.Today, out _) as ClassPlan;
            var result = BuildScheduleFromPlan(todayPlan, GetSubjectName);
            lessonCount = result.Count;
            planName = todayPlan?.Name ?? "";
            return result;
        }
        catch (Exception ex)
        {
            Logger.Error("读取当日课表异常", ex);
            return new Dictionary<string, string[]>();
        }
    }

    /// <summary>
    /// 由课表对象构建待上报的课表字典。
    /// </summary>
    /// <param name="plan">当日课表，可为 null。</param>
    /// <param name="subjectNameResolver">科目 ID 到名称的解析函数。</param>
    internal static Dictionary<string, string[]> BuildScheduleFromPlan(
        ClassPlan? plan,
        Func<Guid, string> subjectNameResolver)
        => BuildScheduleCore(plan?.TimeLayout?.Layouts, plan?.Classes, subjectNameResolver);

    /// <summary>
    /// 课表构建的核心实现。仅依赖时间轴时段与课程列表两个集合，
    /// 与具体的课表对象解耦，便于独立验证。
    /// 只统计 <see cref="TimeLayoutItem.TimeType"/> 为 0 的正课时段，
    /// 并与课程列表按下标顺序一一对应。
    /// </summary>
    /// <param name="layouts">时间轴时段集合（含课间等非正课时段）。</param>
    /// <param name="classes">课程列表。</param>
    /// <param name="subjectNameResolver">科目 ID 到名称的解析函数。</param>
    internal static Dictionary<string, string[]> BuildScheduleCore(
        IEnumerable<TimeLayoutItem>? layouts,
        IEnumerable<ClassInfo>? classes,
        Func<Guid, string> subjectNameResolver)
    {
        var result = new Dictionary<string, string[]>();
        if (layouts == null)
        {
            return result;
        }

        var classList = classes?.ToList() ?? new List<ClassInfo>();
        var index = 0;

        foreach (var layout in layouts)
        {
            if (layout.TimeType != 0)
            {
                continue;
            }

            var classInfo = index < classList.Count ? classList[index] : null;
            var subjectName = "无";
            var isChanged = false;

            if (classInfo != null && classInfo.SubjectId != Guid.Empty)
            {
                subjectName = subjectNameResolver(classInfo.SubjectId);
                isChanged = classInfo.IsChangedClass;
            }

            result[(index + 1).ToString()] = new[]
            {
                subjectName,
                layout.StartTime.ToString("hh\\:mm\\:ss"),
                layout.EndTime.ToString("hh\\:mm\\:ss"),
                isChanged ? "1" : "0"
            };
            index++;
        }

        return result;
    }

    /// <summary>
    /// 计算课表内容哈希，用于判断是否发生实质变化。
    /// </summary>
    internal static string ComputeHash(IReadOnlyDictionary<string, string[]> schedule)
    {
        var normalized = string.Join("|", schedule
            .OrderBy(x => int.TryParse(x.Key, out var k) ? k : int.MaxValue)
            .Select(x => $"{x.Key}:{string.Join(",", x.Value)}"));

        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes);
    }

    private string GetSubjectName(Guid subjectId)
    {
        try
        {
            var profile = _profileService?.Profile;
            if (profile != null && profile.Subjects.TryGetValue(subjectId, out var subject))
            {
                return subject.Name;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("获取科目名异常", ex);
        }

        return "未知科目";
    }

    /// <summary>
    /// 规范化科目名称。空值、纯空白与占位符统一处理为「无」，
    /// 避免把 "???" 或不可见字符直接上报给服务端。
    /// </summary>
    /// <remarks>
    /// 这里必须连同<b>不可见字符</b>一起剔除。上游 ClassIsland 的科目名可能来自
    /// 用户录入或课表文件导入，若其中混入零宽空格（U+200B）或字节序标记（U+FEFF），
    /// <see cref="string.IsNullOrWhiteSpace"/> 会判定为「非空白」而原样放行，
    /// 最终序列化成 <c>{"NextSubject":"\u200B"}</c> 这类「看起来存在、实际为空」
    /// 的值，服务端校验时会报「缺少必要参数」。此处先剥离再判定，落到「无」。
    /// </remarks>
    internal static string NormalizeSubject(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return "无";
        }

        var cleaned = TextSanitizer.Clean(raw);

        // 占位符与规范化后为空的内容一律视为「没有科目」
        if (cleaned.Length == 0 || cleaned == "???" || cleaned == "未知科目")
        {
            return "无";
        }

        // 过长的科目名多半是导入异常，截断以免污染报文
        return cleaned.Length > 64 ? cleaned[..64] : cleaned;
    }

    /// <summary>
    /// 剔除零宽与其它不可见格式字符。保留该入口是为了兼容既有调用方，
    /// 实际实现已下沉到 <see cref="TextSanitizer"/>。
    /// </summary>
    internal static string StripInvisibleCharacters(string? raw)
        => TextSanitizer.StripInvisible(raw);

    /// <summary>
    /// 格式化剩余时长。使用 TotalMinutes 以正确覆盖超过一小时的情况。
    /// </summary>
    /// <summary>
    /// 自行计算「当前时段距离结束还剩多久」。
    /// </summary>
    /// <remarks>
    /// 不直接使用 <c>ILessonsService.OnClassLeftTime</c> / <c>OnBreakingTimeLeftTime</c>，
    /// 因为上游 ClassIsland 2.1.1.1 的实现存在缺陷：
    /// 该实现每次 tick 先把内部变量重置为 null，随后仅在
    /// <c>CurrentState != OnClass</c> 时赋值给「上课剩余」，
    /// 于是处于上课状态时该值恒为 null，最终回落为 <c>TimeSpan.Zero</c>，
    /// 表现为剩余时间永远上报 0 分 0 秒。
    /// 这里改为直接取当前时段的 <c>EndTime</c> 与当前时刻相减，口径直观且与状态一致。
    /// </remarks>
    internal static TimeSpan ComputeRemainingToPeriodEnd(
        TimeLayoutItem? currentItem, TimeSpan now)
    {
        if (currentItem == null || currentItem.Equals(TimeLayoutItem.Empty))
        {
            return TimeSpan.Zero;
        }

        var remaining = currentItem.EndTime - now;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// 实例包装：取当前时段的剩余时间。
    /// </summary>
    private TimeSpan ComputeRemainingToPeriodEnd()
        => ComputeRemainingToPeriodEnd(
            _lessonsService?.CurrentTimeLayoutItem,
            DateTime.Now.TimeOfDay);

    internal static string FormatDuration(TimeSpan timeSpan)
    {
        if (timeSpan.TotalSeconds <= 0)
        {
            return "0分0秒";
        }

        var totalMinutes = (int)timeSpan.TotalMinutes;
        var seconds = timeSpan.Seconds;

        return totalMinutes > 0 ? $"{totalMinutes}分{seconds}秒" : $"{seconds}秒";
    }

    private static string FormatResult(ApiResponse response)
        => response.IsSuccess
            ? $"成功（{response.Message}）"
            : $"失败 Code={response.Code}，{response.Message}";

    /// <summary>
    /// 统一处理后端返回的未授权响应。
    /// </summary>
    private void HandleAuthRejection(ApiResponse response, string scene)
    {
        if (response.Code != 403)
        {
            return;
        }

        Logger.Warning($"{scene}被拒绝：设备未授权，状态已标记为 rejected");
        _settings.AuthStatus = "rejected";
        _settings.AuthMessage = response.Message;
        _settings.Save();
    }
}
