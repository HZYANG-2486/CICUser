using System;
using System.Text;

namespace CICUser;

/// <summary>
/// 文本净化工具。集中处理那些「肉眼看不出、却会导致服务端校验失败」的字符，
/// 供上报逻辑与配置加载共同使用。
/// </summary>
/// <remarks>
/// 独立成类而不是塞在 <see cref="Plugin"/> 里，是为了避免
/// <see cref="PluginSettings"/> 这类底层模型反向依赖插件主类——
/// 那样配置层就会被界面与宿主程序集拖住，无法在轻量环境里单独验证。
/// </remarks>
internal static class TextSanitizer
{
    /// <summary>
    /// 剔除零宽与其它不可见格式字符。
    /// </summary>
    /// <remarks>
    /// 这些字符在界面上不可见，却会让字符串逃过 <see cref="string.IsNullOrWhiteSpace"/>
    /// 的判定（该方法对 U+200B 返回「非空白」），于是「看起来填了值、实际等于空」
    /// 的内容原样序列化出去，服务端 trim 后报「缺少必要参数」。
    /// <para>
    /// 覆盖：U+200B 零宽空格、U+200C 零宽非连接符、U+200D 零宽连接符、
    /// U+2060 词连接符、U+FEFF 字节序标记 / 零宽不换行空格。
    /// </para>
    /// </remarks>
    /// <param name="raw">原始文本，可为 null。</param>
    /// <returns>剔除后的新字符串；输入为 null 时返回空串。</returns>
    public static string StripInvisible(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return "";
        }

        var buffer = new char[raw.Length];
        var count = 0;
        foreach (var ch in raw)
        {
            if (IsInvisibleFormat(ch))
            {
                continue;
            }

            buffer[count++] = ch;
        }

        return new string(buffer, 0, count);
    }

    /// <summary>
    /// 判断字符是否属于需要剔除的不可见格式字符。
    /// </summary>
    /// <param name="ch">待判定字符。</param>
    public static bool IsInvisibleFormat(char ch)
        => ch is '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF';

    /// <summary>
    /// 净化「应当是一段可读文本」的输入：先剔除不可见字符，再裁剪首尾空白。
    /// </summary>
    /// <param name="raw">原始文本，可为 null。</param>
    /// <returns>净化后的文本；无有效内容时返回空串。</returns>
    public static string Clean(string? raw)
        => StripInvisible(raw).Trim();

    /// <summary>
    /// 净化并截断到指定最大长度，避免异常长的输入撑爆报文与界面。
    /// </summary>
    /// <param name="raw">原始文本，可为 null。</param>
    /// <param name="maxLength">允许保留的最大字符数。</param>
    /// <returns>净化后的文本。</returns>
    public static string CleanAndTruncate(string? raw, int maxLength)
    {
        var cleaned = Clean(raw);
        if (maxLength <= 0 || cleaned.Length <= maxLength)
        {
            return cleaned;
        }

        return cleaned[..maxLength];
    }

    /// <summary>
    /// 去除全部控制字符与不可见格式字符，保留正常字符与其间的普通空格。
    /// </summary>
    /// <remarks>
    /// 用于那些不允许出现空白的位置，例如 URL 中的主机名与 JSON 的键名。
    /// </remarks>
    /// <param name="raw">原始文本，可为 null。</param>
    /// <param name="alsoRemoveWhitespace">是否连同普通空白一起去掉。</param>
    public static string CleanStrict(string? raw, bool alsoRemoveWhitespace)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return "";
        }

        var builder = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (IsInvisibleFormat(ch) || char.IsControl(ch))
            {
                continue;
            }

            if (alsoRemoveWhitespace && char.IsWhiteSpace(ch))
            {
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}
