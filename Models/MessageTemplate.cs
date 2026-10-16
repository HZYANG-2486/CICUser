using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CICUser;

/// <summary>
/// 报文字段的值来源类型。
/// </summary>
/// <remarks>
/// <b>枚举按数字持久化</b>（工程内没有配置 <c>JsonStringEnumConverter</c>），
/// 因此新增成员<b>必须追加在末尾</b>，插在中间会让旧配置里的数字落到别的语义上。
/// </remarks>
public enum FieldSourceType
{
    /// <summary>运行时自动填充（科目、状态等动态数据）。</summary>
    Runtime,

    /// <summary>固定文本，由用户直接填写。</summary>
    Fixed,

    /// <summary>引用全局配置项（设备ID、班级名称等）。</summary>
    Global,

    /// <summary>
    /// 模板插值：字面量与变量混排，如 <c>{ClassName}-第{Index}节</c>。
    /// </summary>
    /// <remarks>
    /// 单独作为一种来源，而不是让 <see cref="Fixed"/> 顺带支持插值——
    /// 后者会改变旧配置的输出：原本作为纯文本的 <c>{</c> 会被解释成占位符起点。
    /// </remarks>
    Template
}

/// <summary>
/// 字段输出的 JSON 类型。
/// </summary>
/// <remarks>
/// 同样按数字持久化，新增成员追加在末尾。
/// </remarks>
public enum FieldValueType
{
    /// <summary>
    /// 原样输出，不做类型转换。默认值，也是既有版本的行为。
    /// </summary>
    /// <remarks>
    /// 字符串来源输出字符串；运行时变量里本身是数字的（如节数、版本号）
    /// 仍然输出数字。旧配置读入后即为此值，输出与改造前逐字节一致。
    /// </remarks>
    Auto,

    /// <summary>强制输出为字符串。</summary>
    Text,

    /// <summary>输出为数字。</summary>
    Number,

    /// <summary>输出为布尔值。</summary>
    Boolean,

    /// <summary>把内容当作 JSON 原样嵌入，可用来塞入数组或对象。</summary>
    RawJson
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
        FieldSourceType.Template => "模板插值",
        _ => source.ToString()
    };

    /// <summary>取值来源的一句话说明，供界面提示。</summary>
    public static string ToDescription(this FieldSourceType source) => source switch
    {
        FieldSourceType.Runtime => "从「取值」下拉里选一个运行时变量，取值随课堂状态自动变化。",
        FieldSourceType.Fixed => "「取值」里的内容原样输出，其中的花括号不会被解释。",
        FieldSourceType.Global => "从「取值」下拉里选一个配置项，如设备ID、班级名称。",
        FieldSourceType.Template => "在「取值」里混排固定文字与变量，写成 {ClassName}-第{Index}节 这样。",
        _ => ""
    };

    /// <summary>界面下拉框使用的全部可选来源（含中文名）。</summary>
    public static readonly FieldSourceType[] All =
    {
        FieldSourceType.Runtime,
        FieldSourceType.Fixed,
        FieldSourceType.Global,
        FieldSourceType.Template
    };
}

/// <summary>
/// 值类型的中文名与说明。
/// </summary>
public static class FieldValueTypeExtensions
{
    /// <summary>值类型的中文显示名。</summary>
    public static string ToDisplayName(this FieldValueType type) => type switch
    {
        FieldValueType.Auto => "原样",
        FieldValueType.Text => "文本",
        FieldValueType.Number => "数字",
        FieldValueType.Boolean => "布尔",
        FieldValueType.RawJson => "原始JSON",
        _ => type.ToString()
    };

    /// <summary>值类型的一句话说明，供界面提示。</summary>
    public static string ToDescription(this FieldValueType type) => type switch
    {
        FieldValueType.Auto => "不做转换。数字型变量输出数字，其余输出文本。默认项。",
        FieldValueType.Text => "一律输出为字符串，即使内容是数字。",
        FieldValueType.Number => "输出为数字。内容不是数字时保留原文本并在校验里提示。",
        FieldValueType.Boolean => "输出为 true / false。也接受 1 与 0。",
        FieldValueType.RawJson => "把内容当作 JSON 原样嵌入，可用来发送数组或对象。",
        _ => ""
    };

    /// <summary>界面下拉框使用的全部值类型。</summary>
    public static readonly FieldValueType[] All =
    {
        FieldValueType.Auto,
        FieldValueType.Text,
        FieldValueType.Number,
        FieldValueType.Boolean,
        FieldValueType.RawJson
    };
}

/// <summary>
/// 「值类型」下拉框的选项包装。
/// </summary>
public sealed class FieldValueTypeOption
{
    /// <summary>对应的枚举值。</summary>
    public FieldValueType Value { get; init; }

    /// <summary>中文显示名。</summary>
    public string DisplayName => Value.ToDisplayName();

    /// <summary>供下拉框直接显示的文本。</summary>
    public override string ToString() => DisplayName;

    /// <summary>由枚举值构造选项。</summary>
    public static FieldValueTypeOption From(FieldValueType value) => new() { Value = value };

    /// <summary>全部选项，供界面绑定。</summary>
    public static IReadOnlyList<FieldValueTypeOption> All { get; } =
        FieldValueTypeExtensions.All.Select(From).ToList();
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
/// <remarks>
/// <b>枚举按数字持久化</b>（工程内没有配置 <c>JsonStringEnumConverter</c>），
/// 新增成员必须追加在末尾。
/// </remarks>
public enum ScheduleFormat
{
    /// <summary>按节次字典：{"1": {...}, "2": {...}}。默认。</summary>
    ByIndex,

    /// <summary>数组：每节课一个对象，含节次与科目等字段。</summary>
    Array,

    /// <summary>按科目合并：同一科目的所有节次归到一起。</summary>
    BySubject,

    /// <summary>扁平列表：每节课一条记录，不带节次键。</summary>
    Flat,

    /// <summary>
    /// 按节次数组：{"1": ["语文", "08:00:00", "08:40:00", "0"]}。
    /// </summary>
    /// <remarks>
    /// 安全加固之前的版本使用的就是这个形态，保留它以便对接既有的服务端实现。
    /// 数组元素没有键名，因此元素的含义由字段的<b>声明顺序</b>决定。
    /// </remarks>
    ByIndexArray
}

/// <summary>
/// 课表内容每项的字段定义，供格式自定义使用。
/// </summary>
public class MessageField
{
    /// <summary>
    /// 字段名（报文中的键）。
    /// </summary>
    /// <remarks>
    /// 含点号时会被解释为路径并生成嵌套对象：
    /// <c>data.device.id</c> 产出 <c>{"data":{"device":{"id":…}}}</c>。
    /// 需要字面量点号时用反斜杠转义，写作 <c>a\.b</c>。
    /// </remarks>
    public string Key { get; set; } = "";

    /// <summary>取值来源类型。</summary>
    public FieldSourceType Source { get; set; } = FieldSourceType.Runtime;

    /// <summary>
    /// 取值表达式。
    /// Source 为 Runtime 时填写运行时变量名（如 Subject、State）；
    /// Source 为 Fixed 时填写固定文本；
    /// Source 为 Global 时填写全局配置键（如 DeviceId、ClassName）；
    /// Source 为 Template 时填写含 <c>{变量}</c> 占位符的文本。
    /// </summary>
    public string Value { get; set; } = "";

    /// <summary>该字段是否启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 输出值的 JSON 类型。
    /// </summary>
    /// <remarks>
    /// 默认 <see cref="FieldValueType.Auto"/>，即不做转换——
    /// 这与改造前的行为完全一致，因此旧配置读入后输出逐字节不变。
    /// </remarks>
    public FieldValueType ValueType { get; set; } = FieldValueType.Auto;

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
        FieldSourceType.Template => $"模板：{Value}",
        _ => ""
    };

    /// <summary>
    /// 「取值来源」列只读态的显示文本。用于表格单元格绑定，
    /// 使非编辑状态也能显示中文而不是英文枚举名。
    /// </summary>
    [JsonIgnore]
    public string SourceLabel => Source.ToDisplayName();

    /// <summary>「值类型」列只读态的显示文本。</summary>
    [JsonIgnore]
    public string ValueTypeLabel => ValueType.ToDisplayName();

    public MessageField Clone() => new()
    {
        Key = Key,
        Source = Source,
        Value = Value,
        Enabled = Enabled,
        // 新增属性务必一并拷贝：Clone 是界面收集、撤销快照、
        // 保存配置、切换配置的公共路径，漏一行就会静默丢数据。
        ValueType = ValueType
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

    /// <summary>
    /// 该模板要求使用的课表内容格式预设。
    /// </summary>
    /// <remarks>
    /// 留空表示「跟随全局设置」，此时用主设置里的课表发送格式。
    /// 之所以让模板也能指定格式，是因为<b>格式本身也是协议的一部分</b>：
    /// 旧版协议用数组形态，新版用对象形态，只靠字段定义区分不出来。
    /// 模板里写死格式，切换模板就能整套切换协议，不必再去手动改格式下拉。
    /// </remarks>
    public string ScheduleFormat { get; set; } = "";

    public MessageTemplate Clone() => new()
    {
        Name = Name,
        StatusEndpoint = StatusEndpoint,
        ScheduleEndpoint = ScheduleEndpoint,
        ScheduleDataKey = ScheduleDataKey,
        ScheduleFormat = ScheduleFormat,
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
        // 留空表示跟随全局设置，默认即「按节次字典」
        ScheduleFormat = "",
        StatusFields = CreateDefaultStatusFields(),
        ScheduleFields = CreateDefaultScheduleFields()
    };

    /// <summary>
    /// 安全加固之前的协议模板。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与原版本协议的唯一差别在<b>课表内容的形态</b>：
    /// 旧版是 <c>{"1": ["语文", "08:00:00", "08:40:00", "0"]}</c>，
    /// 每节的值是数组；加固后的默认改成了带键名的对象。
    /// </para>
    /// <para>
    /// 状态报文与课表报文的顶层字段与默认模板一致，因此这里直接复用默认字段集，
    /// 只把课表格式钉在 <see cref="CICUser.ScheduleFormat.ByIndexArray"/> 上。
    /// </para>
    /// <para>
    /// 另一处差异是该版本没有心跳通道，但心跳走独立端点、不属于报文模板，
    /// 需要时在「上报频率」里单独关掉即可。
    /// </para>
    /// </remarks>
    public static MessageTemplate CreateLegacy() => new()
    {
        Name = LegacyTemplateName,
        StatusEndpoint = "/class/api/submit",
        ScheduleEndpoint = "/class/api/schedule/update",
        ScheduleDataKey = "Schedule",
        ScheduleFormat = nameof(CICUser.ScheduleFormat.ByIndexArray),
        StatusFields = CreateDefaultStatusFields(),
        ScheduleFields = CreateDefaultScheduleFields()
    };

    /// <summary>
    /// 旧版协议内置模板的名称。它是识别该模板的唯一凭据，
    /// 改名后会被当成普通模板，配置加载时会重新播种一份。
    /// </summary>
    public const string LegacyTemplateName = "旧版协议（数组课表）";

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

    // ── 协议与安全模式（三套内置版本预设靠这几个字段区分）──

    /// <summary>该配置是否启用请求签名。</summary>
    public bool SignatureEnabled { get; set; } = false;

    /// <summary>
    /// 该配置是否允许明文 HTTP 服务器地址。
    /// </summary>
    /// <remarks>
    /// 与签名开关组合出两种「向下兼容」档位：
    /// 关签名 + 允许明文 = 纯设备码（最老的服务端）；
    /// 开签名 + 允许明文 = 手输密钥且服务端尚未上 TLS。
    /// </remarks>
    public bool AllowInsecureHttp { get; set; } = true;

    /// <summary>该配置是否在缺少密钥时自动向服务端注册（要求 https）。</summary>
    public bool AutoEnroll { get; set; } = false;

    /// <summary>该配置绑定的服务端证书 SHA-256 指纹（空表示走系统信任链）。</summary>
    public string ServerCertificateFingerprint { get; set; } = "";

    /// <summary>
    /// 是否为内置的版本预设配置。
    /// </summary>
    /// <remarks>
    /// 内置预设<b>只切换协议与安全模式</b>，不动服务器地址、班级名等连接参数——
    /// 它们是「协议档位」而不是完整快照；套用完整快照会把当前地址覆盖成过时值。
    /// </remarks>
    public bool IsBuiltInPreset { get; set; } = false;

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
        ScheduleTemplate = ScheduleTemplate?.Clone(),
        SignatureEnabled = SignatureEnabled,
        AllowInsecureHttp = AllowInsecureHttp,
        AutoEnroll = AutoEnroll,
        ServerCertificateFingerprint = ServerCertificateFingerprint,
        IsBuiltInPreset = IsBuiltInPreset
    };
}

/// <summary>
/// 运行时可选用的变量名，供界面下拉选择。
/// </summary>
/// <remarks>
/// 这里的条目全部由 <see cref="KeywordCatalog"/> 投影而来。
/// 改造前它是独立维护的一份列表，与 <c>ScheduleFormatter.ItemVariables</c>
/// 以及界面里的手写提示三处并存且互不同步，现在统一由目录兜底，
/// 避免再出现「代码里定义了但界面看不到」的情况。
/// </remarks>
public static class RuntimeVariables
{
    /// <summary>状态报文中可用的运行时变量。</summary>
    public static (string Key, string Label)[] Status { get; } =
        Project(KeywordScope.Status);

    /// <summary>课表报文中可用的运行时变量。</summary>
    public static (string Key, string Label)[] Schedule { get; } =
        Project(KeywordScope.Schedule);

    /// <summary>可引用的全局配置键。</summary>
    public static (string Key, string Label)[] Global { get; } =
        Project(KeywordScope.Global);

    /// <summary>按作用域投影为「键名 + 中文名」列表。</summary>
    private static (string Key, string Label)[] Project(KeywordScope scope)
        => KeywordCatalog.ForScope(scope)
            .Select(x => (x.Key, x.DisplayName))
            .ToArray();
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
    /// <remarks>
    /// 字段名含点号时会生成嵌套对象；同一路径重复出现时后写覆盖，
    /// 结构冲突（某键既是普通字段又要当对象容器）时先到者胜。
    /// 课表字典在最后注入，因此它与普通字段同路径时以课表字典为准。
    /// </remarks>
    /// <param name="fields">字段定义集合。</param>
    /// <param name="runtimeValues">运行时变量。</param>
    /// <param name="globalValues">全局配置项。</param>
    /// <param name="scope">字段所处作用域，用于取值与校验。</param>
    /// <param name="dataKey">课表字典的键名，可含点号路径。</param>
    /// <param name="dataValue">课表字典内容，整体注入。</param>
    /// <param name="issues">构建过程中收集到的问题，可为 null。</param>
    public static string Build(
        IEnumerable<MessageField> fields,
        IReadOnlyDictionary<string, object?> runtimeValues,
        IReadOnlyDictionary<string, string> globalValues,
        KeywordScope scope = KeywordScope.Status,
        string? dataKey = null,
        object? dataValue = null,
        List<string>? issues = null)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        var context = new ResolutionContext
        {
            RuntimeValues = runtimeValues ?? ResolutionContext.EmptyRuntime,
            GlobalValues = globalValues ?? ResolutionContext.EmptyGlobal,
            Scope = scope
        };

        foreach (var field in fields.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Key)))
        {
            var value = ValueResolver.Resolve(field, context, out var error);

            if (error != null)
            {
                issues?.Add($"字段「{field.Key}」：{error}");
            }

            if (!NestedObjectBuilder.Set(dict, field.Key, value, out var pathError)
                && pathError != null)
            {
                issues?.Add(pathError);
            }
        }

        // 课表字典作为整体注入，避免被模板逐字段拆解。
        // 放在最后，与改造前的覆盖语义保持一致。
        if (!string.IsNullOrEmpty(dataKey) && dataValue != null)
        {
            if (!NestedObjectBuilder.Set(dict, dataKey, dataValue, out var dataError)
                && dataError != null)
            {
                issues?.Add(dataError);
            }
        }

        return JsonSerializer.Serialize(dict, WriteOptions);
    }

    /// <summary>
    /// 校验模板，返回需要提示给用户的问题列表。
    /// </summary>
    /// <param name="template">待校验的模板。</param>
    /// <param name="isStatus">true 校验状态字段，false 校验课表字段。</param>
    /// <param name="scheduleItemFields">课表项字段，只在课表侧一并校验。</param>
    /// <param name="runtimeKeys">该作用域下运行时实际会提供的键名，可为 null 表示跳过该项检查。</param>
    public static List<string> Validate(
        MessageTemplate template,
        bool isStatus,
        IReadOnlyList<MessageField>? scheduleItemFields = null,
        IReadOnlySet<string>? runtimeKeys = null)
    {
        var issues = new List<string>();
        var fields = isStatus ? template.StatusFields : template.ScheduleFields;
        var scope = isStatus ? KeywordScope.Status : KeywordScope.Schedule;

        ValidateFields(fields, scope, isStatus ? "状态报文" : "课表报文", issues, runtimeKeys);

        // 课表项字段此前完全没有校验，配错了只会在运行时静默取到空串
        if (!isStatus && scheduleItemFields != null)
        {
            ValidateFields(scheduleItemFields, KeywordScope.ScheduleItem, "课表项", issues,
                KeywordCatalog.ForScope(KeywordScope.ScheduleItem).Select(x => x.Key).ToHashSet(StringComparer.Ordinal));
        }

        // 键名结构冲突：把启用字段与课表字典键名放一起试写一遍
        if (!isStatus)
        {
            var probe = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var f in fields.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Key)))
            {
                if (!NestedObjectBuilder.Set(probe, f.Key, null, out var conflict) && conflict != null)
                {
                    issues.Add(conflict);
                }
            }

            if (!string.IsNullOrWhiteSpace(template.ScheduleDataKey)
                && !NestedObjectBuilder.Set(probe, template.ScheduleDataKey, null, out var dataConflict)
                && dataConflict != null)
            {
                issues.Add(dataConflict);
            }
        }

        return issues;
    }

    /// <summary>
    /// 校验一组字段。
    /// </summary>
    /// <param name="fields">字段集合。</param>
    /// <param name="scope">作用域。</param>
    /// <param name="sceneName">场景名，用于拼提示语。</param>
    /// <param name="issues">收集问题的列表。</param>
    /// <param name="runtimeKeys">运行时实际提供的键名；为 null 时用目录兜底。</param>
    private static void ValidateFields(
        IReadOnlyList<MessageField>? fields,
        KeywordScope scope,
        string sceneName,
        List<string> issues,
        IReadOnlySet<string>? runtimeKeys)
    {
        if (fields == null)
        {
            return;
        }

        var enabled = fields.Where(x => x.Enabled).ToList();
        if (enabled.Count == 0)
        {
            issues.Add($"{sceneName}至少需要启用一个字段。");
            return;
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
                issues.Add($"{sceneName}中存在未填写字段名的启用项。");
                break;
            }
        }

        var known = runtimeKeys
            ?? KeywordCatalog.ForScope(scope).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var f in enabled.Where(x => !string.IsNullOrWhiteSpace(x.Key)))
        {
            ValidateFieldValue(f, scope, known, issues);
        }
    }

    /// <summary>
    /// 校验单个字段的取值表达式与值类型是否自洽。
    /// </summary>
    private static void ValidateFieldValue(
        MessageField field,
        KeywordScope scope,
        IReadOnlySet<string> knownKeys,
        List<string> issues)
    {
        var expression = (field.Value ?? "").Trim();

        switch (field.Source)
        {
            case FieldSourceType.Runtime:
                if (expression.Length == 0)
                {
                    issues.Add($"字段「{field.Key}」的取值为空。");
                }
                else if (!knownKeys.Contains(expression))
                {
                    var hint = KeywordCatalog.SuggestClosest(expression, scope);
                    issues.Add(hint == null
                        ? $"字段「{field.Key}」引用了未知的运行时变量「{expression}」。"
                        : $"字段「{field.Key}」引用了未知的运行时变量「{expression}」，是否想写「{hint}」？");
                }

                break;

            case FieldSourceType.Global:
                if (expression.Length == 0)
                {
                    issues.Add($"字段「{field.Key}」的取值为空。");
                }
                else if (!KeywordCatalog.IsKnownGlobalKey(expression))
                {
                    var hint = KeywordCatalog.SuggestClosest(expression, KeywordScope.Global);
                    issues.Add(hint == null
                        ? $"字段「{field.Key}」引用了未知的配置项「{expression}」。"
                        : $"字段「{field.Key}」引用了未知的配置项「{expression}」，是否想写「{hint}」？");
                }

                break;

            case FieldSourceType.Template:
                // 模板里的占位符逐个核对，未命中说明拼错了
                if (expression.Length == 0)
                {
                    issues.Add($"字段「{field.Key}」的模板内容为空。");
                    break;
                }

                foreach (var key in FindUnresolvedPlaceholders(field.Value ?? "", scope, knownKeys))
                {
                    var hint = KeywordCatalog.SuggestClosest(key, scope)
                               ?? KeywordCatalog.SuggestClosest(key, KeywordScope.Global);
                    issues.Add(hint == null
                        ? $"字段「{field.Key}」的模板里引用了未知的关键字「{key}」。"
                        : $"字段「{field.Key}」的模板里引用了未知的关键字「{key}」，是否想写「{hint}」？");
                }

                break;
        }

        ValidateValueTypeCoherence(field, expression, scope, knownKeys, issues);
    }

    /// <summary>找出模板里所有无法解析的占位符键名。</summary>
    private static IReadOnlyList<string> FindUnresolvedPlaceholders(
        string template, KeywordScope scope, IReadOnlySet<string> knownKeys)
    {
        var result = Interpolation.Expand(template, key =>
        {
            if (knownKeys.Contains(key)) return "";
            if (KeywordCatalog.IsKnownGlobalKey(key)) return "";
            return null;      // 返回 null 即判定为未命中
        });

        // 全局配置键是跨作用域可用的，前面已放行，这里不再重复报
        return result.UnresolvedKeys;
    }

    /// <summary>
    /// 检查值类型与取值内容是否可能转换成功。
    /// </summary>
    /// <remarks>
    /// 只在「明知不可能成功」时提示，不试图预测运行时的具体内容——
    /// 例如运行时变量本身是数字，配数字类型当然没问题。
    /// </remarks>
    private static void ValidateValueTypeCoherence(
        MessageField field,
        string expression,
        KeywordScope scope,
        IReadOnlySet<string> knownKeys,
        List<string> issues)
    {
        // 字面量配数字/布尔：内容固定，转不动就是转不动
        if (field.Source == FieldSourceType.Fixed && expression.Length > 0)
        {
            if (field.ValueType == FieldValueType.Number
                && !long.TryParse(expression, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out _)
                && !double.TryParse(expression, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                issues.Add($"字段「{field.Key}」的值类型是数字，但固定文本「{expression}」不是数字。");
            }

            if (field.ValueType == FieldValueType.RawJson)
            {
                try
                {
                    System.Text.Json.Nodes.JsonNode.Parse(expression);
                }
                catch (JsonException)
                {
                    issues.Add($"字段「{field.Key}」的值类型是原始JSON，但固定文本不是合法 JSON。");
                }
            }
        }

        // 引用型来源配了明显不兼容的类型：文本类变量转不成数字
        if (field.Source is FieldSourceType.Runtime)
        {
            var descriptor = KeywordCatalog.Find(expression, scope);
            if (descriptor == null)
            {
                return;
            }

            var suggested = descriptor.SuggestedValueType;

            if (field.ValueType == FieldValueType.Number
                && suggested != FieldValueType.Number
                && suggested != FieldValueType.Auto)
            {
                issues.Add(
                    $"字段「{field.Key}」的变量「{expression}」（{descriptor.DisplayName}）不是数字型，"
                    + $"设为数字类型可能转换失败。");
            }

            if (field.ValueType == FieldValueType.Boolean
                && suggested != FieldValueType.Boolean)
            {
                issues.Add(
                    $"字段「{field.Key}」的变量「{expression}」（{descriptor.DisplayName}）不是布尔型，"
                    + $"设为布尔类型可能转换失败。");
            }
        }
    }
}
