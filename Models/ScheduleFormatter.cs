using System;
using System.Collections.Generic;
using System.Linq;

namespace CICUser;

/// <summary>
/// 课表内容序列化格式的预设说明，供界面下拉框与说明文字使用。
/// </summary>
public static class ScheduleFormatCatalog
{
    /// <summary>全部预设（含中文名与说明），供界面直接绑定。</summary>
    public static readonly (ScheduleFormat Value, string DisplayName, string Description)[] All =
    {
        (ScheduleFormat.ByIndex, "按节次字典（默认）",
            "{\"1\": {...}, \"2\": {...}}。以节次为键，兼容性最好，老版本服务端无需改动。"),
        (ScheduleFormat.Array, "数组列表",
            "[{...}, {...}]。每节课一个对象，节次作为普通字段，便于前端直接遍历。"),
        (ScheduleFormat.BySubject, "按科目合并",
            "{\"语文\": [{...}], \"数学\": [{...}]}。同一科目的所有节次归到一组，适合按科目统计。"),
        (ScheduleFormat.Flat, "扁平列表",
            "[{...}, {...}]。不带节次键，纯记录列表，适合直接写入数据库表。")
    };

    /// <summary>预设的中文显示名。</summary>
    public static string GetDisplayName(ScheduleFormat format)
        => All.FirstOrDefault(x => x.Value == format).DisplayName ?? format.ToString();

    /// <summary>预设的一句话说明。</summary>
    public static string GetDescription(ScheduleFormat format)
        => All.FirstOrDefault(x => x.Value == format).Description ?? "";

    /// <summary>解析配置中保存的预设名，非法时回落到默认的按节次字典。</summary>
    public static ScheduleFormat Parse(string? raw)
        => Enum.TryParse<ScheduleFormat>(raw, out var parsed) ? parsed : ScheduleFormat.ByIndex;
}

/// <summary>
/// 课表格式预设的下拉选项包装。ToString 返回中文名，
/// 使 ComboBox 无需额外模板即可正确显示。
/// </summary>
public sealed class ScheduleFormatOption
{
    /// <summary>对应的预设枚举值。</summary>
    public ScheduleFormat Value { get; init; }

    /// <summary>中文显示名。</summary>
    public string DisplayName => ScheduleFormatCatalog.GetDisplayName(Value);

    /// <summary>预设说明。</summary>
    public string Description => ScheduleFormatCatalog.GetDescription(Value);

    /// <summary>供下拉框直接显示的文本。</summary>
    public override string ToString() => DisplayName;

    /// <summary>全部选项，供界面绑定。</summary>
    public static IReadOnlyList<ScheduleFormatOption> All { get; } =
        ScheduleFormatCatalog.All.Select(x => new ScheduleFormatOption { Value = x.Value }).ToList();

    /// <summary>由枚举值构造选项。</summary>
    public static ScheduleFormatOption From(ScheduleFormat value) => new() { Value = value };
}

/// <summary>
/// 课表内容的格式转换器。把内部统一的「按节次字典」中间表示
/// 转换成用户选定的预设结构，并支持字段级自定义（键名与取值来源）。
/// </summary>
/// <remarks>
/// 内部中间表示始终是 <c>Dictionary&lt;string, string[]&gt;</c>：
/// 键为节次（从 1 开始），值为 <c>[科目, 开始, 结束, 是否换课]</c>。
/// 这样做的好处是变更检测（哈希）与格式转换相互独立：
/// 无论用户选了哪种预设，只要课表实质内容不变，哈希就不变，
/// 不会因为切换格式而误判为「课表变化」。
/// </remarks>
public static class ScheduleFormatter
{
    /// <summary>
    /// 内部中间表示中每一项的固定顺序：
    /// 0 = 科目，1 = 开始时间，2 = 结束时间，3 = 是否换课。
    /// </summary>
    public const int FieldSubject = 0;
    public const int FieldStart = 1;
    public const int FieldEnd = 2;
    public const int FieldIsChanged = 3;

    /// <summary>
    /// 默认的字段定义：与内部中间表示一一对应。
    /// </summary>
    public static List<MessageField> CreateDefaultItemFields() => new()
    {
        new() { Key = "Subject",   Source = FieldSourceType.Runtime, Value = "Subject",   Enabled = true },
        new() { Key = "Start",     Source = FieldSourceType.Runtime, Value = "Start",     Enabled = true },
        new() { Key = "End",       Source = FieldSourceType.Runtime, Value = "End",       Enabled = true },
        new() { Key = "IsChanged", Source = FieldSourceType.Runtime, Value = "IsChanged", Enabled = true }
    };

    /// <summary>
    /// 课表项可用的运行时变量名（供界面下拉与校验）。
    /// </summary>
    public static readonly (string Key, string Label)[] ItemVariables =
    {
        ("Index",     "节次（序号）"),
        ("Subject",   "科目名称"),
        ("Start",     "开始时间"),
        ("End",       "结束时间"),
        ("IsChanged", "是否换课")
    };

    /// <summary>
    /// 把内部中间表示转换为最终的课表结构，供直接写入报文。
    /// </summary>
    /// <param name="schedule">内部中间表示（键为节次，值为字段数组）。</param>
    /// <param name="format">目标格式预设。</param>
    /// <param name="itemFields">字段级自定义定义；为空时使用默认字段。</param>
    /// <param name="indexKeyPrefix">按节次字典格式下的键名前缀。</param>
    /// <returns>可直接序列化为 JSON 的对象。</returns>
    public static object Convert(
        IReadOnlyDictionary<string, string[]> schedule,
        ScheduleFormat format,
        IReadOnlyList<MessageField>? itemFields = null,
        string? indexKeyPrefix = null)
    {
        if (schedule == null || schedule.Count == 0)
        {
            // 空课表也要保持结构类型一致：字典给对象，列表给空数组
            return format is ScheduleFormat.Array or ScheduleFormat.Flat
                ? new List<object>()
                : new Dictionary<string, object>();
        }

        var fields = ResolveFields(itemFields);
        var prefix = indexKeyPrefix ?? "";

        // 按节次升序排列，保证输出顺序稳定（哈希与预览都依赖这一点）
        var ordered = schedule
            .Select(x => (Index: ParseIndex(x.Key), Raw: x.Key, Values: x.Value))
            .OrderBy(x => x.Index)
            .ToList();

        return format switch
        {
            ScheduleFormat.Array => BuildArray(ordered, fields),
            ScheduleFormat.Flat => BuildFlat(ordered, fields),
            ScheduleFormat.BySubject => BuildBySubject(ordered, fields),
            _ => BuildByIndex(ordered, fields, prefix)
        };
    }

    /// <summary>
    /// 取出启用的字段定义；没有启用项时回落到默认字段，
    /// 避免用户误把所有字段关掉导致课表变成空对象。
    /// </summary>
    private static List<MessageField> ResolveFields(IReadOnlyList<MessageField>? itemFields)
    {
        var enabled = itemFields?
            .Where(x => x != null && x.Enabled && !string.IsNullOrWhiteSpace(x.Key))
            .ToList();

        return enabled is { Count: > 0 } ? enabled : CreateDefaultItemFields();
    }

    /// <summary>「按节次字典」：{"1": {...}, "2": {...}}。</summary>
    private static Dictionary<string, object> BuildByIndex(
        List<(int Index, string Raw, string[] Values)> ordered,
        List<MessageField> fields,
        string prefix)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var item in ordered)
        {
            result[$"{prefix}{item.Index}"] = BuildItem(item.Index, item.Values, fields);
        }

        return result;
    }

    /// <summary>「数组」：每节课一个对象，节次作为普通字段。</summary>
    private static List<object> BuildArray(
        List<(int Index, string Raw, string[] Values)> ordered,
        List<MessageField> fields)
    {
        return ordered.Select(item => (object)BuildItem(item.Index, item.Values, fields)).ToList();
    }

    /// <summary>
    /// 「扁平列表」：与数组结构相同，但不额外注入节次字段，
    /// 完全以用户配置的字段定义为准，适合直接落库。
    /// </summary>
    private static List<object> BuildFlat(
        List<(int Index, string Raw, string[] Values)> ordered,
        List<MessageField> fields)
    {
        return ordered
            .Select(item => (object)BuildItemFromFields(item.Index, item.Values, fields, injectIndex: false))
            .ToList();
    }

    /// <summary>
    /// 「按科目合并」：{科目: [ {...}, {...} ]}。
    /// 同一科目的多个节次归入同一数组，科目名作为一级键。
    /// </summary>
    private static Dictionary<string, object> BuildBySubject(
        List<(int Index, string Raw, string[] Values)> ordered,
        List<MessageField> fields)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var item in ordered)
        {
            var subject = GetValue(item.Values, FieldSubject);
            if (string.IsNullOrWhiteSpace(subject))
            {
                subject = "无";
            }

            if (!result.TryGetValue(subject, out var bucket))
            {
                bucket = new List<object>();
                result[subject] = bucket;
                order.Add(subject);
            }

            if (bucket is List<object> list)
            {
                // 按科目合并时保留节次字段，否则同一科目多次出现无法区分是哪一节
                list.Add(BuildItem(item.Index, item.Values, fields));
            }
        }

        // 按首次出现顺序重排，保证输出稳定
        var sorted = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var key in order)
        {
            sorted[key] = result[key];
        }

        return sorted;
    }

    /// <summary>
    /// 构建单项。默认注入节次字段（键名 <c>Index</c>），
    /// 便于服务端在数组/合并格式下仍能定位到具体节次。
    /// </summary>
    private static Dictionary<string, object> BuildItem(
        int index, string[] values, List<MessageField> fields)
        => BuildItemFromFields(index, values, fields, injectIndex: true);

    /// <summary>
    /// 按字段定义构建单项。
    /// </summary>
    /// <param name="index">节次序号。</param>
    /// <param name="values">内部中间表示的四元组。</param>
    /// <param name="fields">启用的字段定义。</param>
    /// <param name="injectIndex">是否注入节次字段。</param>
    private static Dictionary<string, object> BuildItemFromFields(
        int index,
        string[] values,
        List<MessageField> fields,
        bool injectIndex)
    {
        var item = new Dictionary<string, object>(StringComparer.Ordinal);

        if (injectIndex)
        {
            item["Index"] = index;
        }

        foreach (var field in fields)
        {
            item[field.Key] = ResolveFieldValue(field, index, values);
        }

        return item;
    }

    /// <summary>
    /// 解析单个字段的取值。
    /// Source 为 Runtime 时按内置变量名取值，Fixed 时取字面量，
    /// Global 在课表项中无意义（没有全局配置上下文），返回空串。
    /// </summary>
    private static object ResolveFieldValue(MessageField field, int index, string[] values)
    {
        if (field.Source == FieldSourceType.Fixed)
        {
            return field.Value ?? "";
        }

        if (field.Source == FieldSourceType.Global)
        {
            // 课表项内部不支持引用全局配置，保持结构完整返回空串
            return "";
        }

        return (field.Value ?? "").Trim() switch
        {
            "Index" => index,
            "Subject" => GetValue(values, FieldSubject),
            "Start" => GetValue(values, FieldStart),
            "End" => GetValue(values, FieldEnd),
            "IsChanged" => GetValue(values, FieldIsChanged),
            _ => ""
        };
    }

    /// <summary>安全取数组元素，越界时返回空串。</summary>
    private static string GetValue(string[] values, int index)
        => values != null && index >= 0 && index < values.Length ? values[index] : "";

    /// <summary>把节次键解析为整数；非法时排到末尾，保证顺序稳定。</summary>
    private static int ParseIndex(string key)
        => int.TryParse(key, out var value) ? value : int.MaxValue;

    /// <summary>
    /// 生成课表格式的可读预览（缩进 JSON），供设置页展示。
    /// </summary>
    public static string Preview(
        IReadOnlyDictionary<string, string[]> schedule,
        ScheduleFormat format,
        IReadOnlyList<MessageField>? itemFields = null,
        string? indexKeyPrefix = null)
    {
        var converted = Convert(schedule, format, itemFields, indexKeyPrefix);
        return System.Text.Json.JsonSerializer.Serialize(converted, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    /// <summary>
    /// 用一份样例课表生成预览，供用户在还没上课时也能看到格式效果。
    /// </summary>
    public static Dictionary<string, string[]> CreateSampleSchedule() => new(StringComparer.Ordinal)
    {
        ["1"] = new[] { "语文", "08:00:00", "08:40:00", "0" },
        ["2"] = new[] { "数学", "08:50:00", "09:30:00", "0" },
        ["3"] = new[] { "英语", "09:40:00", "10:20:00", "1" },
        ["4"] = new[] { "语文", "10:30:00", "11:10:00", "0" }
    };
}
