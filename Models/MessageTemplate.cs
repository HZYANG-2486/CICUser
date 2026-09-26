using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CICUser;

/// <summary>
/// 报文字段的值来源类型。
/// </summary>
public enum FieldSourceType
{
    /// <summary>运行时自动填充（科目、状态等动态数据）。</summary>
    Runtime,

    /// <summary>固定文本，由用户直接填写。</summary>
    Fixed,

    /// <summary>引用全局配置项（设备ID、班级名称等）。</summary>
    Global
}

/// <summary>
/// 值来源类型的界面辅助。把枚举值映射为中文名称，
/// 避免下拉框直接显示 Runtime / Fixed / Global 等英文标识。
/// </summary>
public static class FieldSourceTypeExtensions
{
    /// <summary>取值来源的中文显示名。</summary>
    public static string ToDisplayName(this FieldSourceType source) => source switch
    {
        FieldSourceType.Runtime => "运行时变量",
        FieldSourceType.Fixed => "固定文本",
        FieldSourceType.Global => "引用配置",
        _ => source.ToString()
    };

    /// <summary>界面下拉框使用的全部可选来源（含中文名）。</summary>
    public static readonly FieldSourceType[] All =
    {
        FieldSourceType.Runtime,
        FieldSourceType.Fixed,
        FieldSourceType.Global
    };
}

/// <summary>
/// 「取值来源」下拉框的选项包装。ToString 返回中文名，
/// 使 ComboBox 无需额外模板即可正确显示中文。
/// </summary>
public sealed class FieldSourceOption
{
    /// <summary>对应的枚举值。</summary>
    public FieldSourceType Value { get; init; }

    /// <summary>中文显示名。</summary>
    public string DisplayName => Value.ToDisplayName();

    /// <summary>供下拉框直接显示的文本。</summary>
    public override string ToString() => DisplayName;

    /// <summary>由枚举值构造选项。</summary>
    public static FieldSourceOption From(FieldSourceType value) => new() { Value = value };

    /// <summary>全部选项，供界面绑定。</summary>
    public static IReadOnlyList<FieldSourceOption> All { get; } =
        FieldSourceTypeExtensions.All.Select(From).ToList();
}

/// <summary>
/// 报文中的单个字段定义，支持增删改与自定义取值来源。
/// </summary>
/// <summary>
/// 课表内容的序列化格式预设。
/// </summary>
public enum ScheduleFormat
{
    /// <summary>按节次字典：{"1": [...], "2": [...]}。默认。</summary>
    ByIndex,

    /// <summary>数组：每节课一个对象，含节次与科目等字段。</summary>
    Array,

    /// <summary>按科目合并：同一科目的所有节次归到一起。</summary>
    BySubject,

    /// <summary>扁平列表：每节课一条记录，不带节次键。</summary>
    Flat
}

/// <summary>
/// 课表内容每项的字段定义，供格式自定义使用。
/// </summary>
public class MessageField
{
    /// <summary>字段名（报文中的键）。</summary>
    public string Key { get; set; } = "";

    /// <summary>取值来源类型。</summary>
    public FieldSourceType Source { get; set; } = FieldSourceType.Runtime;

    /// <summary>
    /// 取值表达式。
    /// Source 为 Runtime 时填写运行时变量名（如 Subject、State）；
    /// Source 为 Fixed 时填写固定文本；
    /// Source 为 Global 时填写全局配置键（如 DeviceId、ClassName）。
    /// </summary>
    public string Value { get; set; } = "";

    /// <summary>该字段是否启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 课表内容中每一项的默认字段定义。
    /// 与内置的 <c>[科目, 开始, 结束, 是否换课]</c> 数组顺序一一对应。
    /// </summary>
    public static List<MessageField> CreateScheduleItemDefaults() => new()
    {
        new() { Key = "Subject",   Source = FieldSourceType.Runtime, Value = "Subject" },
        new() { Key = "Start",     Source = FieldSourceType.Runtime, Value = "Start" },
        new() { Key = "End",       Source = FieldSourceType.Runtime, Value = "End" },
        new() { Key = "IsChanged", Source = FieldSourceType.Runtime, Value = "IsChanged" }
    };

    /// <summary>界面上的说明文字。</summary>
    [JsonIgnore]
    public string Description => Source switch
    {
        FieldSourceType.Runtime => $"运行时变量：{Value}",
        FieldSourceType.Fixed => $"固定值：{Value}",
        FieldSourceType.Global => $"引用配置：{Value}",
        _ => ""
    };

    /// <summary>
    /// 「取值来源」列只读态的显示文本。用于表格单元格绑定，
    /// 使非编辑状态也能显示中文而不是英文枚举名。
    /// </summary>
    [JsonIgnore]
    public string SourceLabel => Source.ToDisplayName();

    public MessageField Clone() => new()
    {
        Key = Key,
        Source = Source,
        Value = Value,
        Enabled = Enabled
    };
}

/// <summary>
/// 一套报文模板。同一插件可保存多套模板并随时切换。
/// </summary>
public class MessageTemplate
{
    /// <summary>模板名称，用于界面展示与切换。</summary>
    public string Name { get; set; } = "默认模板";

    /// <summary>状态上报接口的相对路径。</summary>
    public string StatusEndpoint { get; set; } = "/class/api/submit";

    /// <summary>课表上报接口的相对路径。</summary>
    public string ScheduleEndpoint { get; set; } = "/class/api/schedule/update";

    /// <summary>状态报文包含的字段。</summary>
    public List<MessageField> StatusFields { get; set; } = new();

    /// <summary>课表报文包含的字段。</summary>
    public List<MessageField> ScheduleFields { get; set; } = new();

    /// <summary>课表字段中，用于承载课表字典的键名。</summary>
    public string ScheduleDataKey { get; set; } = "Schedule";

    public MessageTemplate Clone() => new()
    {
        Name = Name,
        StatusEndpoint = StatusEndpoint,
        ScheduleEndpoint = ScheduleEndpoint,
        ScheduleDataKey = ScheduleDataKey,
        StatusFields = StatusFields.Select(x => x.Clone()).ToList(),
        ScheduleFields = ScheduleFields.Select(x => x.Clone()).ToList()
    };

    /// <summary>
    /// 生成与当前插件默认报文一致的内置模板，用于首次运行或重置。
    /// </summary>
    public static MessageTemplate CreateDefault() => new()
    {
        Name = "默认模板",
        StatusEndpoint = "/class/api/submit",
        ScheduleEndpoint = "/class/api/schedule/update",
        ScheduleDataKey = "Schedule",
        StatusFields = CreateDefaultStatusFields(),
        ScheduleFields = CreateDefaultScheduleFields()
    };

    /// <summary>
    /// 单独构造默认的状态报文字段集合。
    /// 供配置修复时只补齐缺失的一半，避免把用户已配好的另一半一并覆盖。
    /// </summary>
    public static List<MessageField> CreateDefaultStatusFields() => new()
    {
        new() { Key = "DeviceId",      Source = FieldSourceType.Global,  Value = "DeviceId" },
        new() { Key = "Sender",        Source = FieldSourceType.Global,  Value = "ClassName" },
        new() { Key = "Subject",       Source = FieldSourceType.Runtime, Value = "Subject" },
        new() { Key = "TimeRange",     Source = FieldSourceType.Runtime, Value = "TimeRange" },
        new() { Key = "State",         Source = FieldSourceType.Runtime, Value = "State" },
        new() { Key = "NextSubject",   Source = FieldSourceType.Runtime, Value = "NextSubject" },
        new() { Key = "RemainingTime", Source = FieldSourceType.Runtime, Value = "RemainingTime" },
        new() { Key = "SendTime",      Source = FieldSourceType.Runtime, Value = "SendTime" }
    };

    /// <summary>
    /// 单独构造默认的课表报文字段集合。用途同 <see cref="CreateDefaultStatusFields"/>。
    /// </summary>
    public static List<MessageField> CreateDefaultScheduleFields() => new()
    {
        new() { Key = "DeviceId",    Source = FieldSourceType.Global,  Value = "DeviceId" },
        new() { Key = "ClassName",   Source = FieldSourceType.Global,  Value = "ClassName" },
        new() { Key = "Date",        Source = FieldSourceType.Runtime, Value = "Date" },
        new() { Key = "LessonCount", Source = FieldSourceType.Runtime, Value = "LessonCount" },
        new() { Key = "Version",     Source = FieldSourceType.Runtime, Value = "Version" },
        new() { Key = "UpdateTime",  Source = FieldSourceType.Runtime, Value = "UpdateTime" }
    };
}

/// <summary>
/// 可保存并通过界面切换的一整套插件配置。
/// 除连接参数外，同时保存上报策略与报文模板，
/// 确保切换配置后所有开关与文案都能完整恢复。
/// </summary>
public class ConfigProfile
{
    public string Name { get; set; } = "默认配置";
    public string ServerUrl { get; set; } = "";
    public string DeviceId { get; set; } = "";

    /// <summary>该配置对应的设备ID 生成方式。</summary>
    public string DeviceIdSource { get; set; } = nameof(CICUser.DeviceIdSource.MachineId);

    public string DeviceName { get; set; } = "";
    public string ClassName { get; set; } = "";
    public bool AutoSendEnabled { get; set; } = true;

    /// <summary>状态报文开关与间隔。</summary>
    public bool StatusSendEnabled { get; set; } = true;
    public int StatusSendIntervalSeconds { get; set; } = 30;

    /// <summary>课表报文开关与间隔。</summary>
    public bool ScheduleSendEnabled { get; set; } = true;
    public int ScheduleSendIntervalSeconds { get; set; } = 1800;

    /// <summary>心跳开关与间隔。</summary>
    public bool HeartbeatEnabled { get; set; } = true;
    public int HeartbeatIntervalSeconds { get; set; } = 60;

    /// <summary>心跳接口路径。</summary>
    public string HeartbeatEndpoint { get; set; } = "/class/api/heartbeat";

    /// <summary>课表变更时是否立即上报。</summary>
    public bool RealtimeScheduleSync { get; set; } = true;

    /// <summary>课表内容无变化时是否跳过上报。</summary>
    public bool SkipUnchangedSchedule { get; set; } = true;

    /// <summary>是否启用授权检查。</summary>
    public bool AuthCheckEnabled { get; set; } = false;

    /// <summary>授权申请接口路径。</summary>
    public string AuthRequestEndpoint { get; set; } = "/class/api/auth/request";

    /// <summary>授权查询接口路径。</summary>
    public string AuthQueryEndpoint { get; set; } = "/class/api/auth/query";

    /// <summary>课表内容序列化格式预设。</summary>
    public string ScheduleFormat { get; set; } = nameof(CICUser.ScheduleFormat.ByIndex);

    /// <summary>课表项字段级自定义定义。</summary>
    public List<MessageField> ScheduleItemFields { get; set; } = MessageField.CreateScheduleItemDefaults();

    /// <summary>按节次字典格式下的键名前缀。</summary>
    public string ScheduleIndexKeyPrefix { get; set; } = "";

    /// <summary>该配置对应的状态报文模板。</summary>
    public MessageTemplate? StatusTemplate { get; set; }

    /// <summary>该配置对应的课表报文模板。</summary>
    public MessageTemplate? ScheduleTemplate { get; set; }

    public ConfigProfile Clone() => new()
    {
        Name = Name,
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
}

/// <summary>
/// 运行时可选用的变量名，供界面下拉选择。
/// </summary>
public static class RuntimeVariables
{
    /// <summary>状态报文中可用的运行时变量。</summary>
    public static readonly (string Key, string Label)[] Status =
    {
        ("Subject",       "当前科目"),
        ("TimeRange",     "时段描述"),
        ("State",         "课堂状态（OnClass/OffClass）"),
        ("NextSubject",   "下一科目"),
        ("RemainingTime", "剩余时间"),
        ("SendTime",      "发送时间"),
        ("ClassPlanName", "当前课表名称"),
        ("ChangedClass",  "是否为换课（true/false）")
    };

    /// <summary>课表报文中可用的运行时变量。</summary>
    public static readonly (string Key, string Label)[] Schedule =
    {
        ("Schedule",   "当日课表字典"),
        ("Date",       "日期（yyyy-MM-dd）"),
        ("LessonCount","课程节数"),
        ("Version",    "课表版本号"),
        ("UpdateTime", "课表更新时间")
    };

    /// <summary>可引用的全局配置键。</summary>
    public static readonly (string Key, string Label)[] Global =
    {
        ("DeviceId",   "设备ID"),
        ("DeviceName", "设备名称"),
        ("ClassName",  "班级名称"),
        ("ServerUrl",  "服务器地址")
    };
}

/// <summary>
/// 报文序列化辅助，负责把模板展开成最终的 JSON。
/// </summary>
public static class MessageBuilder
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 按模板构建报文。运行时变量从 <paramref name="runtimeValues"/> 取值，
    /// 全局配置从 <paramref name="globalValues"/> 取值，固定值直接写入。
    /// 任一取值失败时用空字符串兜底，保证报文结构始终完整。
    /// </summary>
    public static string Build(
        IEnumerable<MessageField> fields,
        IReadOnlyDictionary<string, object?> runtimeValues,
        IReadOnlyDictionary<string, string> globalValues,
        string? dataKey = null,
        object? dataValue = null)
    {
        var dict = new Dictionary<string, object?>();

        foreach (var field in fields.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Key)))
        {
            object? value = field.Source switch
            {
                FieldSourceType.Fixed => field.Value,
                FieldSourceType.Global => globalValues.TryGetValue(field.Value, out var g) ? g : "",
                FieldSourceType.Runtime => runtimeValues.TryGetValue(field.Value, out var r) ? r : null,
                _ => null
            };
            dict[field.Key] = value ?? "";
        }

        // 课表字典字段作为整体注入，避免被模板逐字段拆解
        if (!string.IsNullOrEmpty(dataKey) && dataValue != null)
        {
            dict[dataKey] = dataValue;
        }

        return JsonSerializer.Serialize(dict, WriteOptions);
    }

    /// <summary>
    /// 校验模板，返回需要提示给用户的问题列表。
    /// </summary>
    public static List<string> Validate(MessageTemplate template, bool isStatus)
    {
        var issues = new List<string>();
        var fields = isStatus ? template.StatusFields : template.ScheduleFields;

        var enabled = fields.Where(x => x.Enabled).ToList();
        if (enabled.Count == 0)
        {
            issues.Add(isStatus ? "状态报文至少需要启用一个字段。" : "课表报文至少需要启用一个字段。");
            return issues;
        }

        var duplicated = enabled
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicated.Count > 0)
        {
            issues.Add($"字段名重复：{string.Join("、", duplicated)}");
        }

        foreach (var f in enabled)
        {
            if (string.IsNullOrWhiteSpace(f.Key))
            {
                issues.Add("存在未填写字段名的启用项。");
                break;
            }
        }

        return issues;
    }
}
