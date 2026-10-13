using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CICUser;

/// <summary>
/// 取值解析所需的上下文：运行时变量、全局配置，以及当前字段所处的作用域。
/// </summary>
public sealed class ResolutionContext
{
    /// <summary>运行时变量。值的类型可以是字符串、数字或布尔。</summary>
    public IReadOnlyDictionary<string, object?> RuntimeValues { get; init; }
        = EmptyRuntime;

    /// <summary>全局配置项。</summary>
    public IReadOnlyDictionary<string, string> GlobalValues { get; init; }
        = EmptyGlobal;

    /// <summary>当前字段所在表格的作用域，用于关键字校验。</summary>
    public KeywordScope Scope { get; init; } = KeywordScope.Status;

    /// <summary>空的运行时变量表。</summary>
    public static readonly IReadOnlyDictionary<string, object?> EmptyRuntime
        = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>空的全局配置表。</summary>
    public static readonly IReadOnlyDictionary<string, string> EmptyGlobal
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// 统一的字段取值解析器。
/// </summary>
/// <remarks>
/// <para>
/// 改造前，报文模板与课表格式各自写了一套取值逻辑
/// （<c>MessageBuilder.Build</c> 里的 switch 与 <c>ScheduleFormatter.ResolveFieldValue</c>），
/// 两套语义还不一致——课表项里引用全局配置会得到空串，且都没有类型控制能力。
/// 这里把两者收敛到一处。
/// </para>
/// <para>
/// 解析分两步：先按来源取出<b>原始值</b>（此步保留原生类型，数字仍是数字），
/// 再按 <see cref="FieldValueType"/> 做<b>类型转换</b>。
/// </para>
/// </remarks>
public static class ValueResolver
{
    /// <summary>
    /// 解析一个字段的最终取值。
    /// </summary>
    /// <param name="field">字段定义。</param>
    /// <param name="context">取值上下文。</param>
    /// <param name="error">转换失败等可提示问题的描述；正常时为 null。</param>
    /// <returns>可直接交给 JSON 序列化的值。</returns>
    public static object? Resolve(MessageField field, ResolutionContext context, out string? error)
    {
        var raw = ResolveRaw(field, context);
        return ConvertValue(raw, field.ValueType, out error);
    }

    /// <summary>
    /// 按来源取出原始值，不做类型转换。
    /// </summary>
    /// <remarks>
    /// <see cref="FieldSourceType.Runtime"/> 会<b>保留原生类型</b>——
    /// 运行时字典里的数字仍然是数字。这是刻意的：既有版本就是这样输出
    /// <c>LessonCount</c>、<c>Version</c> 等数字字段的，改成字符串会破坏
    /// 已经对接好的服务端。
    /// <para>
    /// <b>只有标识符会做 Trim</b>（变量名、配置键），字面量原样保留。
    /// 改造前课表项的取值是带 Trim 的、报文模板不带，两处行为不一致；
    /// 统一为「标识符忽略首尾空白」既消掉了这个分歧，
    /// 也不会动到用户在「固定文本」里精心保留的空格。
    /// </para>
    /// </remarks>
    public static object? ResolveRaw(MessageField field, ResolutionContext context)
    {
        var expression = field.Value ?? "";

        switch (field.Source)
        {
            case FieldSourceType.Fixed:
                // 字面量：一个字符都不改
                return expression;

            case FieldSourceType.Global:
                return context.GlobalValues.TryGetValue(expression.Trim(), out var global) ? global : "";

            case FieldSourceType.Runtime:
                return context.RuntimeValues.TryGetValue(expression.Trim(), out var runtime) ? runtime : null;

            case FieldSourceType.Template:
                // 插值结果必然是字符串
                return Interpolation.Expand(expression, key => LookupText(key, context)).Text;

            default:
                return null;
        }
    }

    /// <summary>
    /// 找出插值文本里未能解析的关键字，供模板校验提示。
    /// </summary>
    /// <remarks>
    /// 非模板来源没有占位符概念，返回空列表。
    /// </remarks>
    public static IReadOnlyList<string> FindUnresolvedKeys(MessageField field, ResolutionContext context)
    {
        if (field.Source != FieldSourceType.Template || string.IsNullOrEmpty(field.Value))
        {
            return Array.Empty<string>();
        }

        return Interpolation.Expand(field.Value, key => LookupText(key, context)).UnresolvedKeys;
    }

    /// <summary>
    /// 插值用的查表：运行时变量优先，其次全局配置。
    /// 两处都没有时返回 null，由插值器决定怎么呈现未命中。
    /// </summary>
    private static string? LookupText(string key, ResolutionContext context)
    {
        if (context.RuntimeValues.TryGetValue(key, out var runtime) && runtime != null)
        {
            return ToInvariantString(runtime);
        }

        if (context.GlobalValues.TryGetValue(key, out var global) && global != null)
        {
            return global;
        }

        return null;
    }

    /// <summary>
    /// 按键名直接取字符串值，供界面预览等需要「先看一眼」的场景使用。
    /// </summary>
    public static string? TryGetText(string key, ResolutionContext context)
        => LookupText(key, context);

    /// <summary>
    /// 把原始值转换成目标类型。
    /// </summary>
    /// <remarks>
    /// 转换失败时一律<b>回退为原字符串</b>，而不是给一个「安全」的默认值。
    /// 例如数字类型遇到非数字内容时返回 0 会静默篡改上报数据，
    /// 保留原串至少让服务端看到真实内容，同时校验会把问题报给用户。
    /// </remarks>
    private static object? ConvertValue(object? raw, FieldValueType type, out string? error)
    {
        error = null;

        switch (type)
        {
            case FieldValueType.Auto:
                // 原样输出：字符串来源本就是字符串，运行时变量保留数字/布尔形态。
                // 这是既有版本的默认行为，也是旧配置读入后的取值。
                return raw ?? "";

            case FieldValueType.Text:
                return ToInvariantString(raw);

            case FieldValueType.Number:
                return ConvertToNumber(raw, out error);

            case FieldValueType.Boolean:
                return ConvertToBoolean(raw, out error);

            case FieldValueType.RawJson:
                return ConvertToRawJson(raw, out error);

            default:
                return raw ?? "";
        }
    }

    /// <summary>转换为数字。已经是数字类型则原样保留。</summary>
    private static object ConvertToNumber(object? raw, out string? error)
    {
        error = null;

        switch (raw)
        {
            case null:
                return 0L;
            case long or int or short or byte or sbyte or uint or ushort or ulong:
                return raw;
            case double or float or decimal:
                return raw;
        }

        var text = ToInvariantString(raw).Trim();

        // 整数优先，避免把 8 变成 8.0
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return integer;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
        {
            return real;
        }

        error = $"值「{Truncate(text)}」不是合法数字，已按文本输出。";
        return text;
    }

    /// <summary>转换为布尔。接受 true/false 与 1/0。</summary>
    private static object ConvertToBoolean(object? raw, out string? error)
    {
        error = null;

        if (raw is bool already)
        {
            return already;
        }

        var text = ToInvariantString(raw).Trim();

        if (bool.TryParse(text, out var parsed))
        {
            return parsed;
        }

        // 校内数据里「1/0」比「true/false」更常见
        if (text == "1")
        {
            return true;
        }

        if (text == "0")
        {
            return false;
        }

        error = $"值「{Truncate(text)}」不是合法布尔值，已按文本输出。";
        return text;
    }

    /// <summary>把字符串当 JSON 原样嵌入。</summary>
    private static object ConvertToRawJson(object? raw, out string? error)
    {
        error = null;

        if (raw is JsonNode node)
        {
            return node;
        }

        var text = ToInvariantString(raw).Trim();

        // 空内容没有可嵌入的结构，退回空字符串比报错更合适
        if (text.Length == 0)
        {
            return "";
        }

        try
        {
            // JsonNode 可以直接交给 JsonSerializer，且不像 JsonDocument 需要释放
            return JsonNode.Parse(text) ?? "";
        }
        catch (JsonException ex)
        {
            error = $"内容不是合法 JSON（{ex.Message}），已按文本输出。";
            return text;
        }
    }

    /// <summary>
    /// 转成与区域设置无关的字符串。布尔值统一小写，
    /// 避免出现 <c>True</c> 这种 .NET 风格而服务端按 <c>true</c> 解析失败。
    /// </summary>
    private static string ToInvariantString(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    /// <summary>截断过长的值，避免校验提示被一大段内容撑爆界面。</summary>
    private static string Truncate(string text, int max = 32)
        => text.Length <= max ? text : text.Substring(0, max) + "…";
}
