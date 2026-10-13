using System;
using System.Collections.Generic;
using System.Text;

namespace CICUser;

/// <summary>
/// 插值结果。除了展开后的文本，还带出未能解析的键名，
/// 供模板校验给出「引用了不存在的关键字」这类提示。
/// </summary>
/// <param name="Text">展开后的文本。</param>
/// <param name="UnresolvedKeys">未能解析的键名（去重、按首次出现顺序）。</param>
public readonly record struct InterpolationResult(
    string Text,
    IReadOnlyList<string> UnresolvedKeys);

/// <summary>
/// 文本插值器：把 <c>{Key}</c> 形式占位符替换为变量值，
/// 使一个字段可以混排固定文字与多个变量。
/// </summary>
/// <remarks>
/// <para>
/// 支持的语法：
/// <list type="bullet">
/// <item><c>{Key}</c>：取变量 Key 的值</item>
/// <item><c>{Key:Modifier}</c>：冒号后为修饰符，本轮<b>只切分不使用</b>，
/// 即命中 Key 就取原值、忽略修饰符。已预留扩展点。</item>
/// <item><c>{{</c> 与 <c>}}</c>：转义，分别输出字面量的 <c>{</c> 与 <c>}</c></item>
/// </list>
/// </para>
/// <para>
/// <b>未命中的变量保留原文</b>（<c>{Foo}</c> 原样输出），而非替换为空串。
/// 这与 <see cref="MessageBuilder"/> 里「取值失败给空串」的兜底策略不同，
/// 是刻意为之：空串会把拼写错误彻底藏起来，用户看到 <c>""</c> 完全无从下手；
/// 保留原文则能在预览区一眼看出哪里写错了，校验也会把它标出来。
/// 该行为由 <see cref="KeepUnresolvedLiteral"/> 统一控制。
/// </para>
/// <para>
/// 本实现是<b>单次线性扫描</b>，不涉及正则回溯，也不做递归展开
/// （变量值里再出现 <c>{...}</c> 不会被二次解释，避免无穷递归）。
/// </para>
/// </remarks>
public static class Interpolation
{
    /// <summary>未命中的占位符是否保留原文。改成 false 则替换为空串。</summary>
    public const bool KeepUnresolvedLiteral = true;

    /// <summary>占位符的起始与结束字符。</summary>
    private const char OpenBrace = '{';
    private const char CloseBrace = '}';

    /// <summary>修饰符分隔符。</summary>
    private const char ModifierSeparator = ':';

    /// <summary>
    /// 对模板文本做插值展开。
    /// </summary>
    /// <param name="template">模板文本，可为 null。</param>
    /// <param name="lookup">
    /// 按键名取值的委托。返回 null 表示该键不存在（走未命中分支）。
    /// </param>
    /// <returns>展开结果与未解析键名列表。</returns>
    public static InterpolationResult Expand(string? template, Func<string, string?> lookup)
    {
        if (string.IsNullOrEmpty(template))
        {
            return new InterpolationResult("", Array.Empty<string>());
        }

        var builder = new StringBuilder(template.Length + 16);
        List<string>? unresolved = null;
        var i = 0;

        while (i < template.Length)
        {
            var c = template[i];

            if (c == OpenBrace)
            {
                // 「{{」转义为字面量的「{」
                if (i + 1 < template.Length && template[i + 1] == OpenBrace)
                {
                    builder.Append(OpenBrace);
                    i += 2;
                    continue;
                }

                var close = template.IndexOf(CloseBrace, i + 1);

                // 没有闭合的右括号：剩余内容全部按字面量输出
                if (close < 0)
                {
                    builder.Append(template, i, template.Length - i);
                    break;
                }

                var token = template.Substring(i + 1, close - i - 1);

                // 空 token「{}」原样输出，不当成占位符
                if (token.Length == 0)
                {
                    builder.Append(OpenBrace).Append(CloseBrace);
                    i = close + 1;
                    continue;
                }

                var key = ExtractKey(token);
                var value = key.Length == 0 ? null : lookup(key);

                if (value != null)
                {
                    builder.Append(value);
                }
                else
                {
                    // 记下未解析的键名，供校验提示；空键名没有提示价值，跳过
                    if (key.Length > 0)
                    {
                        unresolved ??= new List<string>();
                        if (!unresolved.Contains(key))
                        {
                            unresolved.Add(key);
                        }
                    }

                    // 保留原文时把占位符原样写回，让拼写错误在预览区可见；
                    // 否则什么都不写，等价于替换为空串。
                    if (KeepUnresolvedLiteral)
                    {
                        builder.Append(OpenBrace).Append(token).Append(CloseBrace);
                    }
                }

                i = close + 1;
                continue;
            }

            if (c == CloseBrace)
            {
                // 「}}」转义为字面量的「}」；孤立的「}」本身就是字面量
                if (i + 1 < template.Length && template[i + 1] == CloseBrace)
                {
                    builder.Append(CloseBrace);
                    i += 2;
                    continue;
                }

                builder.Append(CloseBrace);
                i++;
                continue;
            }

            builder.Append(c);
            i++;
        }

        return new InterpolationResult(
            builder.ToString(),
            (IReadOnlyList<string>?)unresolved ?? Array.Empty<string>());
    }

    /// <summary>
    /// 从占位符内容里取出键名，剥掉修饰符与首尾空白。
    /// </summary>
    /// <remarks>
    /// <c>"SendTime:HH:mm"</c> → <c>"SendTime"</c>。
    /// 修饰符本轮不使用，但这里已把它切分出来，
    /// 将来接格式化器时不必改动扫描逻辑。
    /// </remarks>
    private static string ExtractKey(string token)
    {
        var separator = token.IndexOf(ModifierSeparator);
        var key = separator >= 0 ? token.Substring(0, separator) : token;
        return key.Trim();
    }

    /// <summary>
    /// 取出占位符里的修饰符部分，供将来做格式化使用。
    /// </summary>
    /// <param name="token">占位符内容（不含花括号）。</param>
    /// <returns>修饰符；没有修饰符时返回 null。</returns>
    public static string? ExtractModifier(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var separator = token.IndexOf(ModifierSeparator);
        return separator >= 0 && separator < token.Length - 1
            ? token.Substring(separator + 1)
            : null;
    }

    /// <summary>
    /// 判断文本里是否含有占位符，供界面决定是否提示用户。
    /// </summary>
    public static bool ContainsPlaceholder(string? text)
        => !string.IsNullOrEmpty(text) && text.IndexOf(OpenBrace) >= 0;
}
