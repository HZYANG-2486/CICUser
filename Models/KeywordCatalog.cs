using System;
using System.Collections.Generic;
using System.Linq;

namespace CICUser;

/// <summary>
/// 关键字所属的作用域。三张字段表格天然对应前三个作用域，
/// <see cref="Global"/> 则对三张表通用。
/// </summary>
public enum KeywordScope
{
    /// <summary>状态报文（当前课堂状态相关）。</summary>
    Status,

    /// <summary>课表报文的顶层（日期、节数、版本等）。</summary>
    Schedule,

    /// <summary>课表中的单节课。</summary>
    ScheduleItem,

    /// <summary>全局配置项，三张表都能引用。</summary>
    Global
}

/// <summary>
/// 一个可用关键字的完整描述。
/// </summary>
/// <remarks>
/// 这是关键字信息的<b>唯一事实来源</b>。此前同样的信息散落在
/// <c>RuntimeVariables</c>、<c>ScheduleFormatter.ItemVariables</c> 与
/// 界面里的一句手写提示中，三处互不同步，且其中两处从未被界面引用，
/// 导致用户看不到任何可用变量清单。
/// </remarks>
public sealed class KeywordDescriptor
{
    /// <summary>关键字本身，填写在取值表达式里的名字。</summary>
    public required string Key { get; init; }

    /// <summary>中文名，用于界面展示。</summary>
    public required string DisplayName { get; init; }

    /// <summary>详细说明，解释这个值的含义与形态。</summary>
    public required string Description { get; init; }

    /// <summary>示例值，帮助用户判断是否符合预期。</summary>
    public string Example { get; init; } = "";

    /// <summary>所属作用域。</summary>
    public required KeywordScope Scope { get; init; }

    /// <summary>
    /// 选中该关键字时建议使用的值类型。
    /// 例如节次、节数天然是数字，选它就自动把值类型设为数字。
    /// </summary>
    public FieldValueType SuggestedValueType { get; init; } = FieldValueType.Text;

    /// <summary>
    /// 是否可被选入取值表达式。
    /// </summary>
    /// <remarks>
    /// 少数关键字只用于「解释」，不应当被直接引用——例如课表字典
    /// 由「课表字典键名」整体承载，若用户在字段里引用它只会得到空串。
    /// 这类条目仍会出现在参考面板里（用于说明），但不出现在下拉候选中。
    /// </remarks>
    public bool Selectable { get; init; } = true;

    /// <summary>界面展示文本：键名 ｜ 中文名（例：示例值）。</summary>
    public string DisplayText
        => string.IsNullOrEmpty(Example)
            ? $"{Key} ｜ {DisplayName}"
            : $"{Key} ｜ {DisplayName}（例：{Example}）";
}

/// <summary>
/// 关键字目录。集中登记所有可在取值表达式中引用的变量及其说明，
/// 供界面下拉候选、参考面板、模板校验三处共用。
/// </summary>
public static class KeywordCatalog
{
    /// <summary>取值表达式的建议长度上限，用于校验提示。</summary>
    public const int MaxExpressionLength = 512;

    /// <summary>全部关键字，按作用域分组登记。</summary>
    public static IReadOnlyList<KeywordDescriptor> All { get; } = Build();

    /// <summary>按作用域取全部条目（含不可选的说明项），供参考面板使用。</summary>
    public static IEnumerable<KeywordDescriptor> ForScope(KeywordScope scope)
        => All.Where(x => x.Scope == scope);

    /// <summary>
    /// 按「取值来源 + 表格作用域」取出下拉候选。
    /// </summary>
    /// <remarks>
    /// 映射规则：
    /// <list type="bullet">
    /// <item><see cref="FieldSourceType.Runtime"/>：当前作用域的运行时变量</item>
    /// <item><see cref="FieldSourceType.Global"/>：全局配置项，三张表通用</item>
    /// <item><see cref="FieldSourceType.Template"/>：运行时变量与全局配置的并集，
    /// 因为插值文本里可以同时引用两类</item>
    /// <item><see cref="FieldSourceType.Fixed"/>：无候选，纯字面量</item>
    /// </list>
    /// 无论哪种来源，都只返回 <see cref="KeywordDescriptor.Selectable"/> 为真的条目。
    /// </remarks>
    public static IEnumerable<KeywordDescriptor> ForEditor(FieldSourceType source, KeywordScope scope)
    {
        var items = source switch
        {
            FieldSourceType.Runtime => ForScope(scope),
            FieldSourceType.Global => ForScope(KeywordScope.Global),
            FieldSourceType.Template => ForScope(scope).Concat(ForScope(KeywordScope.Global)),
            _ => Enumerable.Empty<KeywordDescriptor>()
        };

        return items.Where(x => x.Selectable).ToList();
    }

    /// <summary>按作用域与键名查找条目。</summary>
    public static KeywordDescriptor? Find(string? key, KeywordScope scope)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var trimmed = key.Trim();
        return All.FirstOrDefault(x =>
            x.Scope == scope && string.Equals(x.Key, trimmed, StringComparison.Ordinal));
    }

    /// <summary>
    /// 判断键名是否是当前作用域下合法的运行时关键字。
    /// </summary>
    public static bool IsKnownRuntimeKey(string? key, KeywordScope scope)
        => Find(key, scope) != null;

    /// <summary>判断键名是否是合法的全局配置键。</summary>
    public static bool IsKnownGlobalKey(string? key)
        => Find(key, KeywordScope.Global) != null;

    /// <summary>
    /// 在指定作用域内找一个与给定键名最接近的候选，用于「是不是想写 X」这类提示。
    /// </summary>
    /// <param name="key">用户写的键名。</param>
    /// <param name="scope">作用域。</param>
    /// <param name="maxDistance">允许的最大编辑距离，超过则视为没有相近项。</param>
    public static string? SuggestClosest(string? key, KeywordScope scope, int maxDistance = 3)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var trimmed = key.Trim();
        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var candidate in ForScope(scope).Where(x => x.Selectable))
        {
            var distance = Levenshtein(trimmed, candidate.Key);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate.Key;
            }
        }

        return bestDistance <= maxDistance ? best : null;
    }

    /// <summary>
    /// 编辑距离。仅用于给出「是不是想写 X」的建议，不追求性能。
    /// </summary>
    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            Array.Copy(current, previous, current.Length);
        }

        return previous[b.Length];
    }

    /// <summary>
    /// 构造目录。内容与运行时真正产出的变量逐一核对过：
    /// <list type="bullet">
    /// <item>Status ← <c>Plugin.BuildStatusRuntimeValues()</c></item>
    /// <item>Schedule ← <c>Plugin.SendScheduleAsync</c> 内联构造的运行时字典</item>
    /// <item>ScheduleItem ← <c>Plugin.BuildScheduleCore()</c> 的四元组</item>
    /// <item>Global ← <c>PluginSettings.GetGlobalValues()</c></item>
    /// </list>
    /// </summary>
    private static List<KeywordDescriptor> Build() => new()
    {
        // ── 状态报文 ──
        new()
        {
            Key = "Subject", DisplayName = "当前科目", Scope = KeywordScope.Status,
            Description = "当前正在上的科目名称。课间与空闲时段为「无」。",
            Example = "语文"
        },
        new()
        {
            Key = "TimeRange", DisplayName = "时段描述", Scope = KeywordScope.Status,
            Description = "当前时段的可读描述，含节次与起止时间。课间会标注为「课间 起-止」。",
            Example = "第3节 09:40-10:20"
        },
        new()
        {
            Key = "State", DisplayName = "课堂状态", Scope = KeywordScope.Status,
            Description = "上课为 OnClass，课间、放学与空闲均为 OffClass。",
            Example = "OnClass"
        },
        new()
        {
            Key = "NextSubject", DisplayName = "下一科目", Scope = KeywordScope.Status,
            Description = "下一节课的科目名称。没有下一节时为「无」。",
            Example = "数学"
        },
        new()
        {
            Key = "RemainingTime", DisplayName = "剩余时间", Scope = KeywordScope.Status,
            Description = "距当前时段结束的剩余时长，中文可读格式。放学后为 0分0秒。",
            Example = "23分15秒"
        },
        new()
        {
            Key = "SendTime", DisplayName = "发送时间", Scope = KeywordScope.Status,
            Description = "本条报文的生成时刻，格式 yyyy-MM-dd HH:mm:ss。",
            Example = "2026-09-27 09:41:03"
        },
        new()
        {
            Key = "ClassPlanName", DisplayName = "当前课表名称", Scope = KeywordScope.Status,
            Description = "当前生效的课表（作息表）名称，如「冬季作息」。",
            Example = "冬季作息"
        },
        new()
        {
            Key = "ChangedClass", DisplayName = "是否换课", Scope = KeywordScope.Status,
            Description = "当前这节课是否为临时调换的课。输出 true 或 false。",
            Example = "false", SuggestedValueType = FieldValueType.Boolean
        },

        // ── 课表报文顶层 ──
        new()
        {
            Key = "Date", DisplayName = "日期", Scope = KeywordScope.Schedule,
            Description = "课表所对应的日期，格式 yyyy-MM-dd。",
            Example = "2026-09-27"
        },
        new()
        {
            Key = "LessonCount", DisplayName = "课程节数", Scope = KeywordScope.Schedule,
            Description = "当日课程总节数，输出数字。",
            Example = "8", SuggestedValueType = FieldValueType.Number
        },
        new()
        {
            Key = "Version", DisplayName = "课表版本号", Scope = KeywordScope.Schedule,
            Description = "课表内容的版本号，内容变化时自增，供服务端判断新旧。输出数字。",
            Example = "12", SuggestedValueType = FieldValueType.Number
        },
        new()
        {
            Key = "UpdateTime", DisplayName = "课表更新时间", Scope = KeywordScope.Schedule,
            Description = "本条课表报文的生成时刻，格式 yyyy-MM-dd HH:mm:ss。",
            Example = "2026-09-27 09:41:03"
        },
        new()
        {
            Key = "Schedule", DisplayName = "当日课表字典", Scope = KeywordScope.Schedule,
            Description = "课表内容整体。这个值由「课表字典键名」统一承载，不要在这里单独引用——"
                          + "字段里引用它只会得到空串。",
            Selectable = false
        },

        // ── 课表中的单节课 ──
        new()
        {
            Key = "Index", DisplayName = "节次", Scope = KeywordScope.ScheduleItem,
            Description = "该节课的序号，从 1 开始。输出数字。",
            Example = "3", SuggestedValueType = FieldValueType.Number
        },
        new()
        {
            Key = "Subject", DisplayName = "科目名称", Scope = KeywordScope.ScheduleItem,
            Description = "该节课的科目名称，未排课为「无」。",
            Example = "语文"
        },
        new()
        {
            Key = "Start", DisplayName = "开始时间", Scope = KeywordScope.ScheduleItem,
            Description = "该节课的开始时间，格式 hh:mm:ss。",
            Example = "08:00:00"
        },
        new()
        {
            Key = "End", DisplayName = "结束时间", Scope = KeywordScope.ScheduleItem,
            Description = "该节课的结束时间，格式 hh:mm:ss。",
            Example = "08:40:00"
        },
        new()
        {
            Key = "IsChanged", DisplayName = "是否换课", Scope = KeywordScope.ScheduleItem,
            Description = "该节课是否为临时调换。内部值为 1 或 0，选布尔类型会转成 true / false。",
            Example = "1", SuggestedValueType = FieldValueType.Boolean
        },

        // ── 全局配置（三张表通用）──
        new()
        {
            Key = "DeviceId", DisplayName = "设备ID", Scope = KeywordScope.Global,
            Description = "本机在服务端登记的设备标识，由插件自动生成。",
            Example = "701-A-BD-3F9A2C71"
        },
        new()
        {
            Key = "DeviceName", DisplayName = "设备名称", Scope = KeywordScope.Global,
            Description = "在主设置页填写的设备名称。",
            Example = "701班电子白板"
        },
        new()
        {
            Key = "ClassName", DisplayName = "班级名称", Scope = KeywordScope.Global,
            Description = "在主设置页填写的班级名称。",
            Example = "高一(1)班"
        },
        new()
        {
            Key = "ServerUrl", DisplayName = "服务器地址", Scope = KeywordScope.Global,
            Description = "当前配置的服务端地址，一般不需要放进报文。",
            Example = "http://192.168.1.10:8080"
        }
    };
}

/// <summary>
/// 关键字下拉选项的包装。ToString 返回带中文名的展示文本，
/// 使 ComboBox 无需额外模板即可正确显示。
/// </summary>
public sealed class KeywordOption
{
    /// <summary>被包装的关键字描述。</summary>
    public required KeywordDescriptor Descriptor { get; init; }

    /// <summary>下拉框直接显示的文本。</summary>
    public override string ToString() => Descriptor.DisplayText;

    /// <summary>由关键字描述构造选项。</summary>
    public static KeywordOption From(KeywordDescriptor descriptor) => new() { Descriptor = descriptor };

    /// <summary>批量包装。</summary>
    public static List<KeywordOption> FromAll(IEnumerable<KeywordDescriptor> descriptors)
        => descriptors.Select(From).ToList();
}
