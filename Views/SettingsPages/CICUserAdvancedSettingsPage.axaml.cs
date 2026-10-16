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
using Avalonia.VisualTree;
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
            RefreshAllKeywordLists();
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

    /// <summary>
    /// 回显课表发送格式。
    /// </summary>
    /// <remarks>
    /// 取值以活动模板的指定为准——旧版协议模板会把格式钉成数组形态，
    /// 切换模板时应当看到格式跟着变，否则用户会以为协议没切过去。
    /// </remarks>
    private void LoadScheduleFormatFields()
    {
        var options = ScheduleFormatOption.All;
        ScheduleFormatComboBox.ItemsSource = options;

        var current = _settings.ResolveScheduleFormat();
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

        SignatureEnabledCheckBox.IsChecked = _settings.SignatureEnabled;
        DeviceSecretTextBox.Text = _settings.DeviceSecret;
        AutoEnrollCheckBox.IsChecked = _settings.AutoEnroll;
        CertificateFingerprintTextBox.Text = _settings.ServerCertificateFingerprint;
        UpdateSignatureHint();
        UpdateEnrollmentStatus();

        LogPathText.Text = Logger.CurrentLogPath;
        UpdateInsecureHttpHint();
    }

    /// <summary>
    /// 刷新请求签名下方的状态提示。
    /// </summary>
    /// <remarks>
    /// 「开了开关却没填密钥」是配置上最容易漏的一步：此时客户端会在本地
    /// 直接拒绝发送（见 ApiClient.ValidateConnection），提示必须说清原因，
    /// 否则用户只会看到上报一直失败而不知为何。
    /// </remarks>
    private void UpdateSignatureHint()
    {
        var enabled = SignatureEnabledCheckBox.IsChecked == true;
        var hasSecret = !string.IsNullOrWhiteSpace(DeviceSecretTextBox.Text);

        if (enabled && !hasSecret)
        {
            SignatureHintText.Text = "已启用签名但共享密钥为空，上报会在本地被拒绝。请点「生成新密钥」" +
                                     "并在服务端登记同一份密钥。";
            SignatureHintText.Foreground = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse("#C62828"));
            return;
        }

        if (enabled)
        {
            SignatureHintText.Text = "签名已启用。请确认服务端已为该设备ID 登记同一份密钥，" +
                                     "否则上报会被服务端拒绝。";
            SignatureHintText.Foreground = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse("#2E7D32"));
            return;
        }

        SignatureHintText.Text = "更换密钥后需在服务端同步更新，否则上报会被拒绝。" +
                                 "若怀疑整份配置（含密钥）已经外泄，请同时重置设备ID 并在服务端删除旧设备记录。";
        SignatureHintText.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse("#6B6B6B"));
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

        // 活动模板的「状态半」与「课表半」分别落在 _settings 的两个字段里。
        // 一旦两者分叉（导入的配置、或只重建了其中一半），
        // 用同一个 template 对象去填两张表就会把课表那一半显示错，
        // 并在收集时把真正的课表模板整体覆盖掉。
        var scheduleSource = IsEditingActiveTemplate(template) && _settings.ScheduleTemplate != null
            ? _settings.ScheduleTemplate.ScheduleFields
            : template.ScheduleFields;

        ScheduleFieldsGrid.ItemsSource = scheduleSource.Select(x => x.Clone()).ToList();

        UpdateTemplateFormatHint();
        ValidateTemplate();
    }

    /// <summary>
    /// 显示当前编辑的模板要求使用的课表格式。
    /// </summary>
    /// <remarks>
    /// 模板下拉只切换编辑对象、不改变生效中的协议，因此光看字段表格
    /// 分辨不出「默认模板」和「旧版协议」——两者字段完全相同，
    /// 差别只在课表格式。这行提示就是用来把这个差别摆到明面上的。
    /// </remarks>
    private void UpdateTemplateFormatHint()
    {
        if (TemplateFormatHint == null)
        {
            return;
        }

        var raw = GetEditingTemplate().ScheduleFormat;

        TemplateFormatHint.Text = string.IsNullOrWhiteSpace(raw)
            ? "该模板未指定课表格式，跟随「课表发送格式」中的全局设置"
              + $"（当前为「{ScheduleFormatCatalog.GetDisplayName(_settings.ResolveScheduleFormat())}」）。"
            : $"该模板指定课表格式为「{ScheduleFormatCatalog.GetDisplayName(ScheduleFormatCatalog.Parse(raw))}」，"
              + "点「启用」后生效。";
    }

    /// <summary>
    /// 判断给定模板是否为当前生效的活动模板。
    /// </summary>
    /// <remarks>
    /// 只有活动模板才需要同步回 <c>_settings</c>；
    /// 从模板列表里取出来的命名模板是就地编辑的。
    /// </remarks>
    private bool IsEditingActiveTemplate(MessageTemplate template)
        => ReferenceEquals(template, _settings.StatusTemplate)
           || ReferenceEquals(template, _settings.ScheduleTemplate)
           || string.Equals(template.Name, _settings.StatusTemplate?.Name, StringComparison.Ordinal);

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

    // ── 取值列：可编辑下拉 ──

    /// <summary>
    /// 为「取值」列的编辑态下拉框挂载候选。
    /// </summary>
    /// <remarks>
    /// 候选内容取决于两件事：该行当前的<b>取值来源</b>，以及该行所在的<b>表格</b>。
    /// 同一个「Subject」在状态表里是「当前科目」、在课表项表里是「某一节的科目」，
    /// 所以候选必须按作用域区分。
    /// <para>
    /// 这里不做实时联动：数据表格同一时刻只有一个单元格处于编辑态，
    /// 用户在「取值来源」列改完并提交后再来编辑本列，读到的已是更新后的来源。
    /// </para>
    /// </remarks>
    private void AttachValueOptions(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not ComboBox combo || combo.ItemsSource != null)
        {
            return;
        }

        if (combo.DataContext is not MessageField field)
        {
            return;
        }

        var scope = ResolveScope(combo.FindAncestorOfType<DataGrid>()) ?? KeywordScope.Status;

        combo.ItemsSource = KeywordOption.FromAll(KeywordCatalog.ForEditor(field.Source, scope));
        combo.PlaceholderText = BuildValuePlaceholder(field.Source);

        // 同一单元格里的自由输入框。选中候选后要同步刷新它，
        // 否则 MessageField 没有变更通知，输入框会停留在旧值上。
        var input = combo.FindAncestorOfType<Grid>()?
            .GetVisualDescendants().OfType<TextBox>().FirstOrDefault();

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.DataContext is not MessageField f || combo.SelectedItem is not KeywordOption option)
            {
                return;
            }

            var descriptor = option.Descriptor;

            if (f.Source == FieldSourceType.Template)
            {
                // 模板来源：把占位符并进已有文本，而不是整段替换掉用户写好的内容
                f.Value = AppendPlaceholder(f.Value, descriptor.Key);
            }
            else
            {
                f.Value = descriptor.Key;
            }

            f.ValueType = descriptor.SuggestedValueType;

            if (input != null)
            {
                input.Text = f.Value;
            }
        };
    }

    /// <summary>为「值类型」列的编辑态下拉框挂载选项。</summary>
    private void AttachValueTypeOptions(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not ComboBox combo || combo.ItemsSource != null)
        {
            return;
        }

        combo.ItemsSource = FieldValueTypeOption.All;

        if (combo.DataContext is MessageField field)
        {
            combo.SelectedItem = FieldValueTypeOption.All.FirstOrDefault(x => x.Value == field.ValueType);
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.DataContext is MessageField f && combo.SelectedItem is FieldValueTypeOption opt)
            {
                f.ValueType = opt.Value;
            }
        };
    }

    /// <summary>取值输入框的占位提示，按来源给出不同的填写引导。</summary>
    private static string BuildValuePlaceholder(FieldSourceType source) => source switch
    {
        FieldSourceType.Runtime => "从下拉选择运行时变量，或直接输入变量名",
        FieldSourceType.Global => "从下拉选择配置项",
        FieldSourceType.Template => "混排文字与变量，如 第{Index}节 {Subject}",
        _ => "直接输入固定文本"
    };

    /// <summary>
    /// 把 <c>{Key}</c> 占位符并入已有模板文本；已存在时不重复追加。
    /// </summary>
    private static string AppendPlaceholder(string? current, string key)
    {
        var text = current ?? "";
        var token = "{" + key + "}";

        return text.Contains(token, StringComparison.Ordinal) ? text : text + token;
    }

    // ── 可用关键字参考面板 ──

    /// <summary>
    /// 三张字段表格与各自的关键字参考面板的对应关系。
    /// </summary>
    private IEnumerable<(string GridName, KeywordScope Scope, ListBox List, TextBox Search)> KeywordPanels()
    {
        yield return (nameof(StatusFieldsGrid), KeywordScope.Status,
            StatusKeywordList, StatusKeywordSearchBox);
        yield return (nameof(ScheduleFieldsGrid), KeywordScope.Schedule,
            ScheduleKeywordList, ScheduleKeywordSearchBox);
        yield return (nameof(ScheduleItemFieldsGrid), KeywordScope.ScheduleItem,
            ItemKeywordList, ItemKeywordSearchBox);
    }

    /// <summary>按名字取字段表格。</summary>
    private DataGrid GridByName(string name) => name switch
    {
        nameof(ScheduleFieldsGrid) => ScheduleFieldsGrid,
        nameof(ScheduleItemFieldsGrid) => ScheduleItemFieldsGrid,
        _ => StatusFieldsGrid
    };

    /// <summary>判断字段表格所处的作用域。</summary>
    private KeywordScope? ResolveScope(DataGrid? grid)
    {
        if (ReferenceEquals(grid, StatusFieldsGrid)) return KeywordScope.Status;
        if (ReferenceEquals(grid, ScheduleFieldsGrid)) return KeywordScope.Schedule;
        if (ReferenceEquals(grid, ScheduleItemFieldsGrid)) return KeywordScope.ScheduleItem;
        return null;
    }

    /// <summary>刷新全部参考面板（搜索框为空时列出该作用域的全部关键字）。</summary>
    private void RefreshAllKeywordLists()
    {
        foreach (var panel in KeywordPanels())
        {
            FillKeywordList(panel.GridName, panel.Scope, panel.List, panel.Search?.Text);
        }
    }

    private void FillKeywordList(string gridName, KeywordScope scope, ListBox list, string? filter)
    {
        if (list == null)
        {
            return;
        }

        IEnumerable<KeywordDescriptor> items = KeywordCatalog.ForScope(scope);

        var keyword = (filter ?? "").Trim();
        if (keyword.Length > 0)
        {
            items = items.Where(x =>
                x.Key.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || x.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || x.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        list.ItemsSource = KeywordOption.FromAll(items);
    }

    private void KeywordSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressEvents || sender is not TextBox box)
        {
            return;
        }

        // 面板 Border 的 Tag 上写着它服务哪张表格
        var gridName = box.FindAncestorOfType<Border>()?.Tag as string;
        if (gridName == null)
        {
            return;
        }

        var panel = KeywordPanels().FirstOrDefault(x => x.GridName == gridName);
        FillKeywordList(panel.GridName, panel.Scope, panel.List, box.Text);
    }

    /// <summary>
    /// 把参考面板中选中的关键字填入表格里当前选中的那一行。
    /// </summary>
    /// <remarks>
    /// 刻意做成「选好再点按钮」而不是「选中即写入」：
    /// 用户浏览关键字列表是很自然的动作，若一碰就改配置会很难用。
    /// </remarks>
    private void InsertKeywordButton_Click(object? sender, RoutedEventArgs e)
    {
        var gridName = (sender as Visual)?.FindAncestorOfType<Border>()?.Tag as string;
        if (gridName == null)
        {
            return;
        }

        var panel = KeywordPanels().FirstOrDefault(x => x.GridName == gridName);

        if (panel.List?.SelectedItem is not KeywordOption option)
        {
            ShowMessage("请先在关键字列表里选中一项。", isError: true);
            return;
        }

        var grid = GridByName(gridName);
        if (grid.SelectedItem is not MessageField field)
        {
            ShowMessage("请先在左侧表格里点选要填入的那一行。", isError: true);
            return;
        }

        PushUndoSnapshot();

        var descriptor = option.Descriptor;

        if (descriptor.Scope == KeywordScope.Global)
        {
            // 全局配置项只能走「引用配置」来源
            field.Source = FieldSourceType.Global;
            field.Value = descriptor.Key;
        }
        else if (field.Source == FieldSourceType.Template)
        {
            // 已经是模板插值，就把占位符并进去
            field.Value = AppendPlaceholder(field.Value, descriptor.Key);
        }
        else
        {
            field.Source = FieldSourceType.Runtime;
            field.Value = descriptor.Key;
        }

        field.ValueType = descriptor.SuggestedValueType;

        RebindGrid(grid);
        ValidateTemplate();
        RefreshPreview();
        RefreshScheduleFormatPreview();

        ShowMessage($"已把「{descriptor.DisplayName}」填入选中行。", isError: false);
    }

    /// <summary>
    /// 就地重绑表格数据。
    /// </summary>
    /// <remarks>
    /// <see cref="MessageField"/> 没有实现变更通知，直接改属性界面上不会刷新，
    /// 因此用一份新副本重建列表来强制刷新。副作用是会清空选中行。
    /// </remarks>
    private void RebindGrid(DataGrid grid)
    {
        if (grid.ItemsSource is not IEnumerable<MessageField> source)
        {
            return;
        }

        grid.ItemsSource = source.Select(x => x.Clone()).ToList();
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
            var name = opt.Value.ToString();
            _settings.ScheduleFormat = name;

            // 同时写进活动课表模板：格式是协议的一部分，模板带着它才能在
            // 切换模板时整套切换协议。全局那份保留是为了兼容
            // 「模板未指定格式」的旧配置。
            if (_settings.ScheduleTemplate != null)
            {
                _settings.ScheduleTemplate.ScheduleFormat = name;
            }
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
                (IndexKeyPrefixTextBox.Text ?? "").Trim(),
                // 传入全局配置，否则引用配置的字段在预览里恒为空，
                // 用户会以为功能坏了
                _settings.GetGlobalValues());
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

        _settings.SignatureEnabled = SignatureEnabledCheckBox.IsChecked == true;
        _settings.DeviceSecret = (DeviceSecretTextBox.Text ?? "").Trim();
        _settings.AutoEnroll = AutoEnrollCheckBox.IsChecked == true;
        _settings.ServerCertificateFingerprint = (CertificateFingerprintTextBox.Text ?? "").Trim();
    }

    /// <summary>
    /// 切换签名开关时即时刷新提示，避免用户在「开了却没填密钥」的状态下反复试错。
    /// </summary>
    private void SignatureEnabledCheckBox_Click(object? sender, RoutedEventArgs e)
        => UpdateSignatureHint();

    /// <summary>
    /// 生成一份新的共享密钥并回填到输入框。
    /// </summary>
    /// <remarks>
    /// 只换密钥、不动设备ID：签名机制下密钥才是凭据，
    /// 仅轮换密钥就足以让此前泄漏的报文彻底作废，
    /// 而设备ID 保持不变可以让服务端少改一处记录。
    /// </remarks>
    private void GenerateSecretButton_Click(object? sender, RoutedEventArgs e)
    {
        DeviceSecretTextBox.Text = RequestSigner.GenerateSecret();
        UpdateSignatureHint();
        ShowMessage("已生成新密钥，请复制后在服务端登记（保存后生效）。", isError: false);
    }

    private void CopySecretButton_Click(object? sender, RoutedEventArgs e)
    {
        var secret = (DeviceSecretTextBox.Text ?? "").Trim();
        if (secret.Length == 0)
        {
            ShowMessage("密钥为空，请先点「生成新密钥」。", isError: true);
            return;
        }

        if (SettingsDisplayHelper.TrySetClipboardText(this, secret))
        {
            ShowMessage("密钥已复制到剪贴板。", isError: false);
        }
        else
        {
            ShowMessage("无法访问剪贴板，请手动选中密钥文本复制。", isError: true);
        }
    }

    /// <summary>
    /// 手动触发一次自动注册。
    /// </summary>
    /// <remarks>
    /// 先把界面上的改动收进配置再注册——否则用户刚改的服务器地址或证书指纹
    /// 不会生效，注册会拿着旧配置去连一个早已不存在的地址。
    /// </remarks>
    private async void AutoEnrollButton_Click(object? sender, RoutedEventArgs e)
    {
        CommitSafetyFields();
        _settings.Save();

        AutoEnrollButton.IsEnabled = false;
        SetEnrollmentStatusText("正在注册…", "#6B6B6B");

        try
        {
            await Plugin.TryAutoEnrollAsync();
        }
        catch (Exception ex)
        {
            Logger.Error("手动注册失败", ex);
        }
        finally
        {
            AutoEnrollButton.IsEnabled = true;
        }

        // 注册写盘的是另一个配置对象，这里重新读一份以取回最新密钥
        _settings = PluginSettings.Load();
        DeviceSecretTextBox.Text = _settings.DeviceSecret;
        UpdateSignatureHint();
        UpdateEnrollmentStatus();
    }

    /// <summary>
    /// 刷新注册状态提示。
    /// </summary>
    /// <remarks>
    /// 三种状态各有对应文案：已吊销（红）、已持有密钥（绿）、尚未注册（灰）。
    /// 吊销态单独列出，因为它需要用户去服务端处理，不是本机重试能解决的。
    /// </remarks>
    private void UpdateEnrollmentStatus()
    {
        if (EnrollmentStatusText == null)
        {
            return;
        }

        if (_settings.EnrollmentRejected)
        {
            SetEnrollmentStatusText(
                "已被服务端吊销，上报与自动注册均已停止。服务端恢复该设备后，清空配置里的吊销标记即可重新上线。",
                "#C62828");
            return;
        }

        if (!string.IsNullOrWhiteSpace(_settings.DeviceSecret))
        {
            SetEnrollmentStatusText(
                string.IsNullOrWhiteSpace(_settings.EnrollmentMessage)
                    ? "已持有密钥。"
                    : _settings.EnrollmentMessage,
                "#2E7D32");
            return;
        }

        SetEnrollmentStatusText(
            string.IsNullOrWhiteSpace(_settings.EnrollmentMessage)
                ? "尚未注册。开启签名与自动注册后，重启宿主或点「立即注册」。"
                : _settings.EnrollmentMessage,
            "#6B6B6B");
    }

    private void SetEnrollmentStatusText(string text, string colorHex)
    {
        EnrollmentStatusText.Text = text;
        EnrollmentStatusText.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse(colorHex));
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
        if (IsEditingActiveTemplate(template))
        {
            _settings.StatusTemplate = template.Clone();

            // 课表模板只更新它的「课表半」，保留原有的状态半。
            // 原先这里把整份状态模板盖到课表模板上，
            // 两者一旦分叉，课表模板里那一半独立内容就没了。
            if (_settings.ScheduleTemplate != null)
            {
                _settings.ScheduleTemplate.ScheduleFields =
                    template.ScheduleFields.Select(x => x.Clone()).ToList();
            }
            else
            {
                _settings.ScheduleTemplate = template.Clone();
            }
        }
    }

    /// <summary>
    /// 把当前编辑中的模板设为生效模板。
    /// </summary>
    /// <remarks>
    /// 模板下拉只切换「编辑对象」，选中并不会让它生效——编辑与生效分开是有意的，
    /// 免得用户浏览模板时就顺手改掉了线上协议。但这样一来，
    /// 保存过的模板（包括内置的旧版协议预设）就没有任何被启用的入口，故补上此按钮。
    /// <para>
    /// 启用时会连同模板指定的课表格式一起带过去，因此从「默认模板」
    /// 切到「旧版协议（数组课表）」是一次点击完成整套协议切换。
    /// </para>
    /// </remarks>
    private void ApplyTemplateButton_Click(object? sender, RoutedEventArgs e)
    {
        // 先把编辑区里的字段改动收进模板对象，避免启用的是未落盘的旧内容
        CollectTemplateFromEditor();

        var clone = GetEditingTemplate().Clone();

        _settings.StatusTemplate = clone;
        _settings.ScheduleTemplate = clone.Clone();

        // 全局格式同步为新模板解析出的结果。这样即便之后有人把模板里的
        // 格式清空（改回「跟随全局」），行为也不会突然回退到别的形态。
        _settings.ScheduleFormat = _settings.ResolveScheduleFormat().ToString();
        _editingTemplateName = clone.Name;

        _settings.Save();
        Plugin.ReloadSettings();

        _suppressEvents = true;
        try
        {
            RefreshTemplateList();
            // 格式与字段都要跟着新模板重刷，否则界面上显示的仍是上一个模板
            LoadScheduleFormatFields();
            LoadTemplateIntoEditor();
        }
        finally
        {
            _suppressEvents = false;
        }

        RefreshPreview();
        RefreshScheduleFormatPreview();

        ShowMessage($"已启用模板「{clone.Name}」。", isError: false);
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
        var itemFields = (ScheduleItemFieldsGrid.ItemsSource as IEnumerable<MessageField>)?.ToList();

        var issues = MessageBuilder.Validate(template, isStatus: true);
        issues.AddRange(MessageBuilder.Validate(
            _settings.ScheduleTemplate ?? template, isStatus: false, itemFields));

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

        // 预设是「协议档位」，措辞要与完整配置切换区分开，
        // 否则用户会以为服务器地址也被一起换掉了。
        var applied = _settings.SavedProfiles.FirstOrDefault(x => x.Name == name);
        if (applied?.IsBuiltInPreset == true)
        {
            ShowMessage($"已套用协议预设「{name}」。服务器地址等连接参数保持不变，" +
                        "请确认地址与密钥符合该档位的要求。", isError: false);
        }
        else
        {
            ShowMessage($"已切换到配置「{name}」，原授权状态已重置，请重新申请授权。", isError: false);
        }
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
