using System;
using System.Collections.Generic;

namespace CICUser;

/// <summary>
/// 嵌套对象构建器：把 <c>a.b.c</c> 形式的键名展开成
/// <c>{"a":{"b":{"c":…}}}</c>，使报文能表达真实服务端常见的层级结构。
/// </summary>
/// <remarks>
/// <para>
/// 之所以靠解析键名（而不是新增一个「是否路径」开关字段），是因为
/// 少一个字段就少一处 <c>Clone()</c>、配置迁移、界面列需要同步，
/// 而且用户的直觉就是「在键名里打点号」。旧的无点号键名解析结果与原键完全一致。
/// </para>
/// <para>
/// 需要字面量点号时用反斜杠转义：<c>a\.b</c> 得到键名 <c>"a.b"</c>。
/// </para>
/// </remarks>
public static class NestedObjectBuilder
{
    /// <summary>路径分隔符。</summary>
    private const char Separator = '.';

    /// <summary>转义符。</summary>
    private const char Escape = '\\';

    /// <summary>单个路径段的长度上限。</summary>
    public const int MaxSegmentLength = 64;

    /// <summary>路径的最大层数。过深的嵌套只会让报文难以维护。</summary>
    public const int MaxDepth = 8;

    /// <summary>
    /// 把键名切分为路径段。
    /// </summary>
    /// <remarks>
    /// 规则：
    /// <list type="bullet">
    /// <item>按<b>未转义</b>的点号切分；<c>\.</c> 输出字面量点号</item>
    /// <item>空段被忽略：<c>a..b</c> 等价于 <c>a.b</c></item>
    /// <item>每段截断到 <see cref="MaxSegmentLength"/>，段数上限 <see cref="MaxDepth"/></item>
    /// <item>全部为空时返回空列表，由调用方决定回落到什么键名</item>
    /// </list>
    /// </remarks>
    /// <param name="keyPath">原始键名。</param>
    /// <param name="error">首个被修正的问题描述；无问题时为 null。</param>
    /// <returns>路径段列表。</returns>
    public static IReadOnlyList<string> SplitPath(string? keyPath, out string? error)
    {
        error = null;
        var segments = new List<string>();

        if (string.IsNullOrEmpty(keyPath))
        {
            return segments;
        }

        var current = new System.Text.StringBuilder();

        for (var i = 0; i < keyPath.Length; i++)
        {
            var c = keyPath[i];

            if (c == Escape && i + 1 < keyPath.Length && keyPath[i + 1] == Separator)
            {
                // 「\.」→ 字面量点号，不参与切分
                current.Append(Separator);
                i++;
                continue;
            }

            if (c == Separator)
            {
                AppendSegment(segments, current, ref error);
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        AppendSegment(segments, current, ref error);

        if (segments.Count > MaxDepth)
        {
            error ??= $"键名层级超过 {MaxDepth} 层，超出部分已被忽略。";
            segments.RemoveRange(MaxDepth, segments.Count - MaxDepth);
        }

        return segments;
    }

    /// <summary>把累积的一段收进结果，顺带做截断与空段过滤。</summary>
    private static void AppendSegment(List<string> segments, System.Text.StringBuilder current, ref string? error)
    {
        var segment = current.ToString().Trim();

        // 空段直接跳过：a..b 与 .a. 这类写法按 a.b、a 处理
        if (segment.Length == 0)
        {
            return;
        }

        if (segment.Length > MaxSegmentLength)
        {
            error ??= $"键名分段超过 {MaxSegmentLength} 字符，已截断。";
            segment = segment.Substring(0, MaxSegmentLength);
        }

        segments.Add(segment);
    }

    /// <summary>
    /// 把路径段重新拼回键名字符串，对含分隔符或转义符的段做转义。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="SplitPath"/> 互为逆运算。归一化流程会先切分再拼回，
    /// 若这里不转义，<c>a\.b</c>（字面键 <c>a.b</c>）会被拼成 <c>a.b</c>，
    /// 下一次解析就变成了嵌套结构——静默改变用户配置。
    /// </remarks>
    /// <param name="segments">路径段。</param>
    /// <returns>可再次交给 <see cref="SplitPath"/> 的键名。</returns>
    public static string JoinPath(IReadOnlyList<string> segments)
    {
        if (segments == null || segments.Count == 0)
        {
            return "";
        }

        var parts = new string[segments.Count];
        for (var i = 0; i < segments.Count; i++)
        {
            parts[i] = segments[i]
                .Replace("\\", "\\\\")
                .Replace(".", "\\.");
        }

        return string.Join(Separator.ToString(), parts);
    }

    /// <summary>
    /// 把一组字段写进目标字典，按路径自动构建中间层级。
    /// </summary>
    /// <param name="root">目标字典。</param>
    /// <param name="keyPath">键名，可含点号路径。</param>
    /// <param name="value">要写入的值。</param>
    /// <param name="error">发生结构冲突时的描述；正常写入时为 null。</param>
    /// <returns>是否实际写入。</returns>
    public static bool Set(
        IDictionary<string, object?> root,
        string? keyPath,
        object? value,
        out string? error)
    {
        error = null;

        if (root == null)
        {
            return false;
        }

        var pathError = (string?)null;
        var segments = SplitPath(keyPath, out pathError);
        error = pathError;

        if (segments.Count == 0)
        {
            return false;
        }

        var current = root;
        var prefix = "";

        // 逐层下钻，中间的每一层要么已存在且是对象，要么新建
        for (var i = 0; i < segments.Count - 1; i++)
        {
            var segment = segments[i];
            prefix = prefix.Length == 0 ? segment : $"{prefix}.{segment}";

            if (current.TryGetValue(segment, out var existing))
            {
                // 已经是一个对象：继续下钻
                if (existing is IDictionary<string, object?> nested)
                {
                    current = nested;
                    continue;
                }

                // 已存在但是标量：再往里写就构成结构冲突。
                // 先到者胜——保留先写入的值，跳过本次，并把冲突报告出去。
                error = $"键名冲突：「{prefix}」已作为普通字段使用，无法再作为对象容纳「{prefix}.…」。";
                return false;
            }

            var created = new Dictionary<string, object?>(StringComparer.Ordinal);
            current[segment] = created;
            current = created;
        }

        var leaf = segments[segments.Count - 1];

        // 目标位置已有对象，却要写入标量：同样是结构冲突，先到者胜
        if (current.TryGetValue(leaf, out var leafExisting)
            && leafExisting is IDictionary<string, object?>)
        {
            var fullPath = prefix.Length == 0 ? leaf : $"{prefix}.{leaf}";
            error = $"键名冲突：「{fullPath}」已作为对象使用，无法再作为普通字段写入。";
            return false;
        }

        // 同一路径重复出现时后写覆盖，与既有的字典赋值语义保持一致
        current[leaf] = value;
        return true;
    }
}
