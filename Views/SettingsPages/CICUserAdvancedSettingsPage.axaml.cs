using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;

namespace CICUser.Views.SettingsPages;

/// <summary>
/// CICUser 高级设置页。收纳上报频率、课表发送格式、报文模板、
/// 授权与安全参数、多套配置，以及设备ID 的高级选项。
/// 主设置页只保留日常所需的操作。
/// </summary>
[SettingsPageInfo("classisland.cicuser.settings.advanced", "高级设置", "\ue713", "\ue712")]
public partial class CICUserAdvancedSettingsPage : SettingsPageBase
{
    /// <summary>供「取值来源」列下拉使用的选项，带中文显示名。</summary>
    private static readonly IReadOnlyList<FieldSourceOption> SourceOptions = FieldSourceOption.All;

    private PluginSettings _settings = new();

    /// <summary>界面态：当前编辑中的模板名。</summary>
    private string _editingTemplateName = "默认模板";

    /// <summary>标记是否正在程序化填充控件，避免触发 SelectionChanged 递归。</summary>
    private bool _suppressEvents;

    /// <summary>多步撤回栈，快照为整份配置的 JSON。</summary>
    private readonly UndoStack<string> _undoStack = new(
        clone: x => x,
        serialize: x => x);

    /// <summary>上一次压栈时的配置快照，用于判断是否真的发生了变化。</summary>
    private string _lastSnapshot = "";

    public CICUserAdvancedSettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        LoadSettings();
    }

    // ── 加载与回显 ──

    private void LoadSettings()
    {
        _settings = PluginSettings.Load();

        _suppressEvents = true;
        try
        {
            LoadStrategyFields();
            LoadScheduleFormatFields();
            LoadAuthFields();
            LoadSafetyFields();
            LoadTemplateFields();
            RefreshProfileList();
            RefreshDeviceIdSourceList();
        }
        finally
        {
            _suppressEvents = false;
        }

        _lastSnapshot = Snapshot();
        _undoStack.Clear();
        UpdateUndoState();

        RefreshPreview();
        RefreshScheduleFormatPreview();
    }

    /// <summary>回显三通道开关与间隔。</summary>
    private void LoadStrategyFields()
    {
        AutoSendCheckBox.IsChecked = _settings.AutoSendEnabled;

        StatusSendCheckBox.IsChecked = _settings.StatusSendEnabled;
        StatusIntervalUpDown.Value = Math.Clamp(_settings.StatusSendIntervalSeconds, 5, 3600);

        ScheduleSendCheckBox.IsChecked = _settings.ScheduleSendEnabled;
        ScheduleIntervalUpDown.Value = Math.Clamp(_settings.ScheduleSendIntervalSeconds, 60, 86400);

        HeartbeatCheckBox.IsChecked = _settings.HeartbeatEnabled;
        HeartbeatIntervalUpDown.Value = Math.Clamp(_settings.HeartbeatIntervalSeconds, 10, 3600);
        HeartbeatEndpointTextBox.Text = _settings.HeartbeatEndpoint;

        RealtimeScheduleCheckBox.IsChecked = _settings.RealtimeScheduleSync;
        SkipUnchangedCheckBox.IsChecked = _settings.SkipUnchangedSchedule;
    }

    /// <summary>回显课表发送格式。</summary>
    private void LoadScheduleFormatFields()
    {
        var options = ScheduleFormatOption.All;
        ScheduleFormatComboBox.ItemsSource = options;

        var current = ScheduleFormatCatalog.Parse(_settings.ScheduleFormat);
        ScheduleFormatComboBox.SelectedItem =
            options.FirstOrDefault(x => x.Value == current) ?? options[0];

        IndexKeyPrefixTextBox.Text = _settings.ScheduleIndexKeyPrefix ?? "";

        ScheduleItemFieldsGrid.ItemsSource = (_settings.ScheduleItemFields ?? new List<MessageField>())
            .Select(x => x.Clone())
            .ToList();

        UpdateScheduleFormatDescription();
    }

    /// <summary>回显授权相关配置。</summary>
    private void LoadAuthFields()
    {
        AuthCheckEnabledCheckBox.IsChecked = _settings.AuthCheckEnabled;
        AuthRequestEndpointTextBox.Text = _settings.AuthRequestEndpoint;
        AuthQueryEndpointTextBox.Text = _settings.AuthQueryEndpoint;
        AuthDeviceIdFieldTextBox.Text = _settings.AuthDeviceIdField;
        AuthDeviceNameFieldTextBox.Text = _settings.AuthDeviceNameField;
        AuthClassNameFieldTextBox.Text = _settings.AuthClassNameField;
    }

    /// <summary>回显稳定性与安全性配置。</summary>
    private void LoadSafetyFields()
    {
        TimeoutUpDown.Value = Math.Clamp(_settings.RequestTimeoutSeconds, 3, 120);
        RetryUpDown.Value = Math.Clamp(_settings.MaxRetryCount, 0, 5);
        MaskSensitiveCheckBox.IsChecked = _settings.MaskSensitiveInLog;
        AllowInsecureHttpCheckBox.IsChecked = _settings.AllowInsecureHttp;

        LogPathText.Text = Logger.CurrentLogPath;
        UpdateInsecureHttpHint();
    }

    /// <summary>回显报文模板相关控件。</summary>
    private void LoadTemplateFields()
    {
        _editingTemplateName = _settings.StatusTemplate?.Name ?? "默认模板";
        RefreshTemplateList();
        LoadTemplateIntoEditor();
    }

    /// <summary>刷新模板下拉框内容。</summary>
    private void RefreshTemplateList()
    {
        var names = new List<string> { _settings.StatusTemplate?.Name ?? "默认模板" };
        names.AddRange(_settings.StatusTemplates
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => x.Name)
            .Where(n => !names.Contains(n)));

        TemplateComboBox.ItemsSource = names;
        TemplateComboBox.SelectedItem = names.Contains(_editingTemplateName)
            ? _editingTemplateName
            : names.FirstOrDefault();
    }

    /// <summary>刷新配置下拉框内容。</summary>
    private void RefreshProfileList()
    {
        var names = _settings.SavedProfiles.Select(x => x.Name).ToList();
        if (names.Count == 0)
        {
            names.Add("默认配置");
        }

        ProfileComboBox.ItemsSource = names;
        ProfileComboBox.SelectedItem = names.Contains(_settings.CurrentProfileName)
            ? _settings.CurrentProfileName
            : names.FirstOrDefault();
    }

    /// <summary>刷新设备ID 生成方式下拉框。</summary>
    private void RefreshDeviceIdSourceList()
    {
        var options = DeviceIdGenerator.SelectableSources
            .Select(DeviceIdSourceOption.From)
            .ToList();

        DeviceIdSourceComboBox.ItemsSource = options;

        var current = SettingsDisplayHelper.ParseSource(_settings.DeviceIdSource);
        DeviceIdSourceComboBox.SelectedItem =
            options.FirstOrDefault(x => x.Value == current) ?? options.FirstOrDefault();

        ManualDeviceIdTextBox.Text = _settings.DeviceId;
        UpdateSourceDescription();
    }

    private void UpdateSourceDescription()
    {
        if (DeviceIdSourceComboBox.SelectedItem is DeviceIdSourceOption opt)
        {
            SourceDescriptionText.Text = DeviceIdGenerator.GetSourceDescription(opt.Value);
        }
    }

    /// <summary>更新课表格式预设的说明文字。</summary>
    private void UpdateScheduleFormatDescription()
    {
        if (ScheduleFormatComboBox.SelectedItem is ScheduleFormatOption opt)
        {
            ScheduleFormatDescriptionText.Text = opt.Description;

            // 键名前缀仅对「按节次字典」有意义
            var isByIndex = opt.Value == ScheduleFormat.ByIndex;
            IndexKeyPrefixTextBox.IsEnabled = isByIndex;
        }
    }

    /// <summary>
    /// 明文 HTTP 提示。校园内网常用 http，默认只提示不阻断，
    /// 但用醒目颜色让用户意识到风险。
    /// </summary>
    private void UpdateInsecureHttpHint()
    {
        var isHttp = (_settings.ServerUrl ?? "").StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        var allowed = AllowInsecureHttpCheckBox.IsChecked == true;

        if (isHttp && allowed)
        {
            InsecureHttpWarningText.Text =
                "当前服务器地址使用明文 HTTP。校园内网通常可以接受，但同网段内的其它设备可能窃听上报内容；若服务端支持 https://，建议改用加密地址。";
            InsecureHttpWarningText.Foreground = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse("#8A5300"));
        }
        else if (isHttp && !allowed)
        {
            InsecureHttpWarningText.Text =
                "当前服务器地址使用明文 HTTP，而「允许明文 HTTP」已关闭，发送会被拒绝。请改为 https:// 或重新开启该开关。";
            InsecureHttpWarningText.Foreground = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse("#C62828"));
        }
        else
        {
            InsecureHttpWarningText.Text =
                "校园内网常以 http:// 提供服务，明文传输存在被窃听的风险。若服务端支持 https://，建议改用加密地址。";
            InsecureHttpWarningText.Foreground = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse("#6B6B6B"));
        }
    }

    /// <summary>把当前编辑中的模板载入编辑控件。</summary>
    private void LoadTemplateIntoEditor()
    {
        var template = GetEditingTemplate();

        StatusEndpointTextBox.Text = template.StatusEndpoint;
        ScheduleEndpointTextBox.Text = template.ScheduleEndpoint;
        ScheduleDataKeyTextBox.Text = template.ScheduleDataKey;

        // 用副本绑定，取消编辑时不影响已保存内容
        StatusFieldsGrid.ItemsSource = template.StatusFields.Select(x => x.Clone()).ToList();
        ScheduleFieldsGrid.ItemsSource = template.ScheduleFields.Select(x => x.Clone()).ToList();

        ValidateTemplate();
    }

    /// <summary>取得当前正在编辑的模板对象。</summary>
    private MessageTemplate GetEditingTemplate()
    {
        var saved = _settings.StatusTemplates.FirstOrDefault(x => x.Name == _editingTemplateName);
        return saved ?? _settings.StatusTemplate ?? MessageTemplate.CreateDefault();
    }

    // ── 撤回栈 ──

    /// <summary>把当前所有界面字段收集进配置对象并序列化为快照。</summary>
    private string Snapshot()
    {
        CommitStrategyFields();
        CommitScheduleFormatFields();
        CommitAuthFields();
        CommitSafetyFields();
        CollectTemplateFromEditor();

        return JsonSerializer.Serialize(_settings, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    /// <summary>
    /// 在任何会改变配置的操作之前调用，记录一份可回退的快照。
    /// 内容与上次相同时不重复压栈，避免撤回栈被无效条目占满。
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

    /// <summary>刷新撤回按钮的可用状态与提示文字。</summary>
    private void UpdateUndoState()
    {
        UndoButton.IsEnabled = _undoStack.CanUndo;
        UndoHintText.Text = _undoStack.CanUndo
            ? $"「撤回上一步」可回退最近的改动（当前可撤回 {_undoStack.Count} 步）；「恢复默认」会把本页所有设置还原为出厂状态。"
            : "「撤回上一步」可回退最近的改动（最多 20 步）；「恢复默认」会把本页所有设置还原为出厂状态。";
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
        _suppressEvents = true;
        try
        {
            LoadStrategyFields();
            LoadScheduleFormatFields();
            LoadAuthFields();
            LoadSafetyFields();
            LoadTemplateFields();
        }
        finally
        {
            _suppressEvents = false;
        }

        _lastSnapshot = Snapshot();
        UpdateUndoState();
        RefreshPreview();
        RefreshScheduleFormatPreview();

        ShowMessage("已撤回上一步改动。", isError: false);
    }

    private void ResetAllButton_Click(object? sender, RoutedEventArgs e)
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
        _suppressEvents = true;
        try
        {
            LoadStrategyFields();
            LoadScheduleFormatFields();
            LoadAuthFields();
            LoadSafetyFields();
            LoadTemplateFields();
        }
        finally
        {
            _suppressEvents = false;
        }

        _lastSnapshot = Snapshot();
        UpdateUndoState();
        RefreshPreview();
        RefreshScheduleFormatPreview();

        ShowMessage("已恢复默认设置（服务器地址、设备ID 与名称予以保留）。", isError: false);
    }

    // ── 取值来源下拉 ──

    /// <summary>
    /// 为字段表格中「取值来源」列的编辑态下拉框填充可选项。
    /// 模板列不在可视化树中，命名生成器无法捕获，故在附加时用代码挂载。
    /// </summary>
    private void AttachSourceOptions(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not ComboBox combo || combo.ItemsSource != null)
        {
            return;
        }

        combo.ItemsSource = SourceOptions;

        // 把字段当前的枚举值换算成对应的中文选项并选中
        if (combo.DataContext is MessageField field)
        {
            combo.SelectedItem = SourceOptions.FirstOrDefault(x => x.Value == field.Source);
        }

        // 切换来源后写回字段，使预览与最终报文同步更新
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.DataContext is MessageField f && combo.SelectedItem is FieldSourceOption opt)
            {
                f.Source = opt.Value;
            }
        };
    }

    // ── 上报频率 ──

    private void StrategyCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        PushUndoSnapshot();
        CommitStrategyFields();
        _settings.Save();
        Plugin.ReloadSettings();
        RefreshPreview();
    }

    private void AuthCheckEnabledCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        PushUndoSnapshot();
        CommitAuthFields();
        _settings.Save();
        Plugin.ReloadSettings();
    }

    private void CommitStrategyFields()
    {
        _settings.AutoSendEnabled = AutoSendCheckBox.IsChecked == true;

        _settings.StatusSendEnabled = StatusSendCheckBox.IsChecked == true;
        if (StatusIntervalUpDown.Value.HasValue)
        {
            _settings.StatusSendIntervalSeconds =
                Math.Clamp((int)StatusIntervalUpDown.Value.Value, 5, 3600);
        }

        _settings.ScheduleSendEnabled = ScheduleSendCheckBox.IsChecked == true;
        if (ScheduleIntervalUpDown.Value.HasValue)
        {
            _settings.ScheduleSendIntervalSeconds =
                Math.Clamp((int)ScheduleIntervalUpDown.Value.Value, 60, 86400);
        }

        _settings.HeartbeatEnabled = HeartbeatCheckBox.IsChecked == true;
        if (HeartbeatIntervalUpDown.Value.HasValue)
        {
            _settings.HeartbeatIntervalSeconds =
                Math.Clamp((int)HeartbeatIntervalUpDown.Value.Value, 10, 3600);
        }

        _settings.HeartbeatEndpoint =
            PluginSettings.NormalizeEndpoint(HeartbeatEndpointTextBox.Text, "/class/api/heartbeat");

        _settings.RealtimeScheduleSync = RealtimeScheduleCheckBox.IsChecked == true;
        _settings.SkipUnchangedSchedule = SkipUnchangedCheckBox.IsChecked == true;
    }

    // ── 课表发送格式 ──

    private void ScheduleFormatComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        PushUndoSnapshot();
        UpdateScheduleFormatDescription();
        CommitScheduleFormatFields();
        _settings.Save();
        Plugin.ReloadSettings();
        RefreshScheduleFormatPreview();
        RefreshPreview();
    }

    private void CommitScheduleFormatFields()
    {
        if (ScheduleFormatComboBox.SelectedItem is ScheduleFormatOption opt)
        {
            _settings.ScheduleFormat = opt.Value.ToString();
        }

        _settings.ScheduleIndexKeyPrefix = (IndexKeyPrefixTextBox.Text ?? "").Trim();

        _settings.ScheduleItemFields = (ScheduleItemFieldsGrid.ItemsSource as IEnumerable<MessageField>)
            ?.Select(x => x.Clone())
            .ToList() ?? _settings.ScheduleItemFields;

        // 清理空键名项，避免生成空字段名
        _settings.ScheduleItemFields.RemoveAll(x => string.IsNullOrWhiteSpace(x.Key));
        if (_settings.ScheduleItemFields.Count == 0)
        {
            _settings.ScheduleItemFields = MessageField.CreateScheduleItemDefaults();
        }
    }

    private void AddItemFieldButton_Click(object? sender, RoutedEventArgs e)
    {
        var list = (ScheduleItemFieldsGrid.ItemsSource as IEnumerable<MessageField>)?.ToList()
                   ?? new List<MessageField>();

        list.Add(new MessageField
        {
            Key = $"字段{list.Count + 1}",
            Source = FieldSourceType.Runtime,
            Value = "Subject",
            Enabled = true
        });

        ScheduleItemFieldsGrid.ItemsSource = list;
        PushUndoSnapshot();
        CommitScheduleFormatFields();
        RefreshScheduleFormatPreview();
    }

    private void RemoveItemFieldButton_Click(object? sender, RoutedEventArgs e)
    {
        var list = (ScheduleItemFieldsGrid.ItemsSource as IEnumerable<MessageField>)?.ToList()
                   ?? new List<MessageField>();

        if (ScheduleItemFieldsGrid.SelectedItem is MessageField selected)
        {
            list.Remove(selected);
        }
        else if (list.Count > 0)
        {
            list.RemoveAt(list.Count - 1);
        }

        if (list.Count == 0)
        {
            list = MessageField.CreateScheduleItemDefaults();
        }

        ScheduleItemFieldsGrid.ItemsSource = list;
        PushUndoSnapshot();
        CommitScheduleFormatFields();
        RefreshScheduleFormatPreview();
    }

    private void ResetItemFieldsButton_Click(object? sender, RoutedEventArgs e)
    {
        PushUndoSnapshot();
        ScheduleItemFieldsGrid.ItemsSource = MessageField.CreateScheduleItemDefaults();
        CommitScheduleFormatFields();
        _settings.Save();
        Plugin.ReloadSettings();
        RefreshScheduleFormatPreview();
        ShowMessage("课表项字段已恢复默认。", isError: false);
    }

    /// <summary>刷新课表格式预览。</summary>
    private void RefreshScheduleFormatPreview()
    {
        try
        {
            var format = ScheduleFormatComboBox.SelectedItem is ScheduleFormatOption opt
                ? opt.Value
                : ScheduleFormat.ByIndex;
            var fields = (ScheduleItemFieldsGrid.ItemsSource as IEnumerable<MessageField>)?.ToList();

            ScheduleFormatPreviewText.Text = ScheduleFormatter.Preview(
                ScheduleFormatter.CreateSampleSchedule(), format, fields,
                (IndexKeyPrefixTextBox.Text ?? "").Trim());
        }
        catch (Exception ex)
        {
            ScheduleFormatPreviewText.Text = $"预览生成失败：{ex.Message}";
        }
    }

    // ── 授权配置 ──

    private void CommitAuthFields()
    {
        _settings.AuthCheckEnabled = AuthCheckEnabledCheckBox.IsChecked == true;
        _settings.AuthRequestEndpoint =
            PluginSettings.NormalizeEndpoint(AuthRequestEndpointTextBox.Text, "/class/api/auth/request");
        _settings.AuthQueryEndpoint =
            PluginSettings.NormalizeEndpoint(AuthQueryEndpointTextBox.Text, "/class/api/auth/query");

        _settings.AuthDeviceIdField =
            PluginSettings.NormalizeFieldName(AuthDeviceIdFieldTextBox.Text, "DeviceId");
        _settings.AuthDeviceNameField =
            PluginSettings.NormalizeFieldName(AuthDeviceNameFieldTextBox.Text, "DeviceName");
        _settings.AuthClassNameField =
            PluginSettings.NormalizeFieldName(AuthClassNameFieldTextBox.Text, "ClassName");
    }

    // ── 稳定性与安全性 ──

    private void CommitSafetyFields()
    {
        if (TimeoutUpDown.Value.HasValue)
        {
            _settings.RequestTimeoutSeconds = Math.Clamp((int)TimeoutUpDown.Value.Value, 3, 120);
        }

        if (RetryUpDown.Value.HasValue)
        {
            _settings.MaxRetryCount = Math.Clamp((int)RetryUpDown.Value.Value, 0, 5);
        }

        _settings.MaskSensitiveInLog = MaskSensitiveCheckBox.IsChecked == true;
        _settings.AllowInsecureHttp = AllowInsecureHttpCheckBox.IsChecked == true;
    }

    // ── 报文模板编辑 ──

    private void TemplateComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || TemplateComboBox.SelectedItem is not string name)
        {
            return;
        }

        // 切换前先把当前编辑内容落盘，避免丢失
        CollectTemplateFromEditor();
        _editingTemplateName = name;
        LoadTemplateIntoEditor();
        RefreshPreview();
    }

    private void TemplateComboBox_DropDownOpened(object? sender, EventArgs e)
    {
        _suppressEvents = true;
        RefreshTemplateList();
        _suppressEvents = false;
    }

    /// <summary>把编辑控件中的内容收集回模板对象。</summary>
    private void CollectTemplateFromEditor()
    {
        var template = GetEditingTemplate();
        template.StatusEndpoint = StatusEndpointTextBox.Text ?? "/class/api/submit";
        template.ScheduleEndpoint = ScheduleEndpointTextBox.Text ?? "/class/api/schedule/update";
        template.ScheduleDataKey = ScheduleDataKeyTextBox.Text ?? "Schedule";

        template.StatusFields = (StatusFieldsGrid.ItemsSource as IEnumerable<MessageField>)
            ?.Select(x => x.Clone()).ToList() ?? template.StatusFields;
        template.ScheduleFields = (ScheduleFieldsGrid.ItemsSource as IEnumerable<MessageField>)
            ?.Select(x => x.Clone()).ToList() ?? template.ScheduleFields;

        // 活动模板需要同步到实际生效的字段
        if (ReferenceEquals(template, _settings.StatusTemplate) ||
            template.Name == _settings.StatusTemplate?.Name)
        {
            _settings.StatusTemplate = template.Clone();
            _settings.ScheduleTemplate = template.Clone();
        }
    }

    private void SaveTemplateAsButton_Click(object? sender, RoutedEventArgs e)
    {
        CollectTemplateFromEditor();
        var baseTemplate = GetEditingTemplate();

        var name = $"模板{DateTime.Now:MMddHHmm}";
        var clone = baseTemplate.Clone();
        clone.Name = name;

        _settings.StatusTemplates.RemoveAll(x => x.Name == name);
        _settings.StatusTemplates.Add(clone);

        _editingTemplateName = name;
        _settings.StatusTemplate = clone.Clone();
        _settings.ScheduleTemplate = clone.Clone();
        _settings.Save();
        Plugin.ReloadSettings();

        _suppressEvents = true;
        RefreshTemplateList();
        _suppressEvents = false;

        ShowMessage($"已另存为「{name}」并设为当前模板。", isError: false);
    }

    private void DeleteTemplateButton_Click(object? sender, RoutedEventArgs e)
    {
        var name = _editingTemplateName;
        var target = _settings.StatusTemplates.FirstOrDefault(x => x.Name == name);
        if (target == null)
        {
            ShowMessage("默认模板不可删除。", isError: true);
            return;
        }

        // 删除前判断它是否正是当前生效的模板，决定删除后是否需要回退
        var wasActive = _settings.StatusTemplate?.Name == name;

        _settings.StatusTemplates.Remove(target);
        _settings.ScheduleTemplates.RemoveAll(x => x.Name == name);

        if (wasActive)
        {
            var fallback = _settings.StatusTemplates.FirstOrDefault();
            _settings.StatusTemplate = fallback?.Clone() ?? MessageTemplate.CreateDefault();
            _settings.ScheduleTemplate = fallback?.Clone() ?? MessageTemplate.CreateDefault();
            _editingTemplateName = _settings.StatusTemplate.Name;
        }

        // 修正活动模板名，清理可能残留的幽灵引用
        _settings.Normalize();
        _settings.Save();
        Plugin.ReloadSettings();

        _suppressEvents = true;
        RefreshTemplateList();
        _suppressEvents = false;
        LoadTemplateIntoEditor();
        RefreshPreview();

        var suffix = wasActive ? "，已切换到其它模板。" : "。";
        ShowMessage($"模板「{name}」已删除{suffix}", isError: false);
    }

    private void ResetTemplateButton_Click(object? sender, RoutedEventArgs e)
    {
        var defaultTemplate = MessageTemplate.CreateDefault();

        var template = GetEditingTemplate();
        var name = template.Name;
        defaultTemplate.Name = name;

        var index = _settings.StatusTemplates.FindIndex(x => x.Name == name);
        if (index >= 0)
        {
            _settings.StatusTemplates[index] = defaultTemplate;
        }

        // 只要该模板正是当前生效的模板，就必须同步刷新活动模板
        var isActive = _settings.StatusTemplate?.Name == name || index < 0;
        if (isActive)
        {
            _settings.StatusTemplate = defaultTemplate.Clone();
            _settings.ScheduleTemplate = defaultTemplate.Clone();
        }

        _settings.Save();
        Plugin.ReloadSettings();
        LoadTemplateIntoEditor();
        RefreshPreview();

        ShowMessage("已恢复为默认报文字段。", isError: false);
    }

    private void AddStatusFieldButton_Click(object? sender, RoutedEventArgs e)
        => AddField(StatusFieldsGrid);

    private void AddScheduleFieldButton_Click(object? sender, RoutedEventArgs e)
        => AddField(ScheduleFieldsGrid);

    private void AddField(DataGrid grid)
    {
        var list = (grid.ItemsSource as IEnumerable<MessageField>)?.ToList() ?? new List<MessageField>();
        list.Add(new MessageField
        {
            Key = $"新字段{list.Count + 1}",
            Source = FieldSourceType.Fixed,
            Value = "",
            Enabled = true
        });

        grid.ItemsSource = list;
        ValidateTemplate();
        RefreshPreview();
    }

    private void RemoveStatusFieldButton_Click(object? sender, RoutedEventArgs e)
        => RemoveField(StatusFieldsGrid);

    private void RemoveScheduleFieldButton_Click(object? sender, RoutedEventArgs e)
        => RemoveField(ScheduleFieldsGrid);

    private void RemoveField(DataGrid grid)
    {
        var list = (grid.ItemsSource as IEnumerable<MessageField>)?.ToList() ?? new List<MessageField>();
        if (grid.SelectedItem is MessageField selected)
        {
            list.Remove(selected);
        }
        else if (list.Count > 0)
        {
            list.RemoveAt(list.Count - 1);
        }

        grid.ItemsSource = list;
        ValidateTemplate();
        RefreshPreview();
    }

    /// <summary>运行模板校验并把问题展示在界面上。</summary>
    private void ValidateTemplate()
    {
        CollectTemplateFromEditor();
        var template = _settings.StatusTemplate ?? MessageTemplate.CreateDefault();

        var issues = MessageBuilder.Validate(template, isStatus: true);
        issues.AddRange(MessageBuilder.Validate(_settings.ScheduleTemplate ?? template, isStatus: false));

        if (issues.Count == 0)
        {
            TemplateValidationBorder.IsVisible = false;
            TemplateValidationText.Text = "";
        }
        else
        {
            TemplateValidationBorder.IsVisible = true;
            TemplateValidationText.Text = "模板存在问题：" + string.Join("；", issues.Distinct());
        }
    }

    private void FieldsGrid_CellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        // 课表项字段表格的编辑结果需要立刻反映到格式预览
        if (ReferenceEquals(sender, ScheduleItemFieldsGrid))
        {
            PushUndoSnapshot();
            CommitScheduleFormatFields();
            RefreshScheduleFormatPreview();
            return;
        }

        CollectTemplateFromEditor();
        ValidateTemplate();
        RefreshPreview();
    }

    private void RefreshPreviewButton_Click(object? sender, RoutedEventArgs e) => RefreshPreview();

    /// <summary>刷新报文预览区。</summary>
    private void RefreshPreview()
    {
        try
        {
            StatusPreviewText.Text = Plugin.PreviewStatusMessage();
            SchedulePreviewText.Text = Plugin.PreviewScheduleMessage();
        }
        catch (Exception ex)
        {
            StatusPreviewText.Text = $"预览生成失败：{ex.Message}";
            SchedulePreviewText.Text = "";
        }
    }

    // ── 配置管理 ──

    private void ApplyProfileButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ProfileComboBox.SelectedItem is not string name)
        {
            return;
        }

        PushUndoSnapshot();

        if (!_settings.ApplyProfile(name))
        {
            ShowMessage($"未找到配置「{name}」。", isError: true);
            return;
        }

        _settings.Save();
        Plugin.ReloadSettings();

        _suppressEvents = true;
        try
        {
            LoadStrategyFields();
            LoadScheduleFormatFields();
            LoadAuthFields();
            LoadSafetyFields();
            LoadTemplateFields();
        }
        finally
        {
            _suppressEvents = false;
        }

        _lastSnapshot = Snapshot();
        RefreshPreview();
        RefreshScheduleFormatPreview();

        ShowMessage($"已切换到配置「{name}」，原授权状态已重置，请重新申请授权。", isError: false);
    }

    private void SaveProfileButton_Click(object? sender, RoutedEventArgs e)
    {
        CommitStrategyFields();
        CommitScheduleFormatFields();

        var name = string.IsNullOrWhiteSpace(_settings.ClassName)
            ? $"配置{DateTime.Now:MMddHHmm}"
            : _settings.ClassName;

        _settings.SaveAsProfile(name);
        _settings.Save();
        Plugin.ReloadSettings();

        _suppressEvents = true;
        RefreshProfileList();
        _suppressEvents = false;

        ShowMessage($"当前设置已保存为配置「{name}」。", isError: false);
    }

    private void RemoveProfileButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ProfileComboBox.SelectedItem is not string name)
        {
            return;
        }

        if (!_settings.RemoveProfile(name))
        {
            ShowMessage($"配置「{name}」不存在。", isError: true);
            return;
        }

        _settings.Save();

        _suppressEvents = true;
        RefreshProfileList();
        _suppressEvents = false;

        ShowMessage($"配置「{name}」已删除。", isError: false);
    }

    // ── 配置导入导出 ──

    private IStorageProvider? GetStorageProvider()
        => TopLevel.GetTopLevel(this)?.StorageProvider;

    private async void ExportConfigButton_Click(object? sender, RoutedEventArgs e)
    {
        CommitStrategyFields();
        CommitScheduleFormatFields();
        CommitAuthFields();
        CommitSafetyFields();
        CollectTemplateFromEditor();
        _settings.Save();

        var storage = GetStorageProvider();
        if (storage == null)
        {
            ShowMessage("当前环境不支持文件选择，请手动复制配置文件。", isError: true);
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出插件配置",
            SuggestedFileName = $"CICUser-配置-{DateTime.Now:yyyyMMdd-HHmm}.json",
            DefaultExtension = "json",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("JSON 配置文件") { Patterns = new[] { "*.json" } }
            }
        });

        if (file == null)
        {
            return;
        }

        var path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            ShowMessage("无法获取所选路径。", isError: true);
            return;
        }

        if (PluginSettings.ExportTo(path, _settings))
        {
            ShowMessage($"配置已导出到：{path}", isError: false);
        }
        else
        {
            ShowMessage("导出失败，请查看日志了解详情。", isError: true);
        }
    }

    private async void ImportConfigButton_Click(object? sender, RoutedEventArgs e)
    {
        var storage = GetStorageProvider();
        if (storage == null)
        {
            ShowMessage("当前环境不支持文件选择，请手动复制配置文件。", isError: true);
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入插件配置",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("JSON 配置文件") { Patterns = new[] { "*.json" } }
            }
        });

        var file = files.FirstOrDefault();
        if (file == null)
        {
            return;
        }

        var path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            ShowMessage("无法获取所选路径。", isError: true);
            return;
        }

        PushUndoSnapshot();

        var imported = PluginSettings.ImportFrom(path);
        if (imported == null)
        {
            ShowMessage("导入失败：文件不存在或格式不正确。", isError: true);
            return;
        }

        // 保留旧的授权状态，其余以导入内容为准
        imported.AuthStatus = _settings.AuthStatus;
        imported.AuthMessage = _settings.AuthMessage;
        imported.Save();

        Plugin.ReloadSettings();

        _settings = imported;
        _suppressEvents = true;
        try
        {
            LoadStrategyFields();
            LoadScheduleFormatFields();
            LoadAuthFields();
            LoadSafetyFields();
            LoadTemplateFields();
            RefreshProfileList();
            RefreshDeviceIdSourceList();
        }
        finally
        {
            _suppressEvents = false;
        }

        _lastSnapshot = Snapshot();
        RefreshPreview();
        RefreshScheduleFormatPreview();

        ShowMessage($"已从 {Path.GetFileName(path)} 导入配置。", isError: false);
    }

    private void OpenConfigFolderButton_Click(object? sender, RoutedEventArgs e)
        => SettingsDisplayHelper.OpenContainingFolder(PluginSettings.SettingsFilePath, ShowMessage);

    // ── 设备ID 高级 ──

    private void DeviceIdSourceComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        => UpdateSourceDescription();

    private void RegenerateFromComboButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DeviceIdSourceComboBox.SelectedItem is not DeviceIdSourceOption opt)
        {
            return;
        }

        if (opt.Value == DeviceIdSource.ClassNameDerived &&
            string.IsNullOrWhiteSpace(_settings.ClassName) &&
            string.IsNullOrWhiteSpace(_settings.DeviceName))
        {
            ShowMessage("按「班级名称 + 设备名称派生」生成前，请先在主设置页填写班级名称或设备名称。", isError: true);
            return;
        }

        PushUndoSnapshot();

        var generated = Plugin.RegenerateDeviceId(opt.Value);
        _settings = PluginSettings.Load();
        RefreshDeviceIdSourceList();

        _lastSnapshot = Snapshot();

        ShowMessage($"已按「{opt.DisplayName}」生成设备ID：{generated}。授权状态已重置。", isError: false);
    }

    private void ApplyManualDeviceIdButton_Click(object? sender, RoutedEventArgs e)
    {
        var raw = (ManualDeviceIdTextBox.Text ?? "").Trim();

        var error = Plugin.SetDeviceIdManually(raw);
        if (error != null)
        {
            ShowMessage(error, isError: true);
            return;
        }

        PushUndoSnapshot();

        _settings = PluginSettings.Load();
        RefreshDeviceIdSourceList();
        _lastSnapshot = Snapshot();

        ShowMessage($"设备ID 已修改为 {raw}，请重新申请授权。", isError: false);
    }

    // ── 操作 ──

    private void BackButton_Click(object? sender, RoutedEventArgs e)
    {
        CommitStrategyFields();
        CommitScheduleFormatFields();
        CommitAuthFields();
        CommitSafetyFields();
        CollectTemplateFromEditor();
        _settings.Save();
        Plugin.ReloadSettings();

        SettingsDisplayHelper.NavigateToPage(Plugin.MainSettingsPageId, ShowMessage);
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        CommitStrategyFields();
        CommitScheduleFormatFields();
        CommitAuthFields();
        CommitSafetyFields();
        CollectTemplateFromEditor();
        _settings.Save();
        Plugin.ReloadSettings();

        _lastSnapshot = Snapshot();
        UpdateUndoState();

        ValidateTemplate();
        RefreshScheduleFormatPreview();
        ShowMessage("设置已保存并立即生效。", isError: false);
    }

    private void OpenLogButton_Click(object? sender, RoutedEventArgs e)
        => SettingsDisplayHelper.OpenPath(Logger.CurrentLogPath, "日志文件尚未生成。", ShowMessage);

    private void ShowMessage(string message, bool isError)
        => SettingsDisplayHelper.ShowMessage(MessageBorder, MessageText, message, isError);
}
