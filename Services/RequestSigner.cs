using System;
using System.Security.Cryptography;
using System.Text;

namespace CICUser;

/// <summary>
/// 请求签名器（方案 A：共享密钥 + 时间戳 + 随机数）。
/// </summary>
/// <remarks>
/// <para>
/// 要解决的问题：设备ID 原本既是「身份标识」又是「认证凭据」，
/// 一旦被第三方看到（抓包、看服务端数据库、翻日志），就能被永久冒用，
/// 而服务端无从分辨。本类把这两件事拆开——
/// 身份仍用设备ID 声明，凭据改用「每次请求现算、且只在一小段时间内有效」的签名。
/// </para>
/// <para>
/// 签名覆盖四元组：设备ID、时间戳、随机数、请求体的 SHA-256。
/// 共享密钥<b>从不随报文发送</b>，只在两端各存一份，
/// 因此截获报文的人无法伪造新请求。
/// </para>
/// <para>
/// 与 HMAC 的对应关系：算法固定 HMAC-SHA256，
/// 与被签名内容拼接方式见 <see cref="BuildCanonical"/>。
/// 服务端必须按完全相同的规则重算，否则校验不通过。
/// </para>
/// </remarks>
internal static class RequestSigner
{
    /// <summary>协议版本，随签名规则一起发送，便于服务端日后平滑演进。</summary>
    public const string SchemeVersion = "1";

    /// <summary>请求头名称：签名协议版本。</summary>
    public const string HeaderVersion = "X-CIC-Scheme";

    /// <summary>请求头名称：设备ID，服务端据此取出对应密钥。</summary>
    public const string HeaderDevice = "X-CIC-Device";

    /// <summary>请求头名称：Unix 秒级时间戳。</summary>
    public const string HeaderTimestamp = "X-CIC-Timestamp";

    /// <summary>请求头名称：本次请求的随机数。</summary>
    public const string HeaderNonce = "X-CIC-Nonce";

    /// <summary>请求头名称：签名值。</summary>
    public const string HeaderSignature = "X-CIC-Signature";

    /// <summary>随机数字节数。16 字节即 128 位，碰撞概率可忽略。</summary>
    private const int NonceBytes = 16;

    /// <summary>一次签名所需的全部要素。</summary>
    /// <param name="Timestamp">Unix 秒级时间戳字符串。</param>
    /// <param name="Nonce">随机数（大写十六进制）。</param>
    /// <param name="Signature">签名值（大写十六进制）。</param>
    public readonly record struct SignedRequest(string Timestamp, string Nonce, string Signature);

    /// <summary>
    /// 为一次请求生成签名。
    /// </summary>
    /// <param name="secret">共享密钥。</param>
    /// <param name="deviceId">设备ID。</param>
    /// <param name="body">即将发送的请求体原文。</param>
    /// <returns>时间戳、随机数与签名。</returns>
    public static SignedRequest Sign(string secret, string deviceId, string body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(NonceBytes));
        var signature = Compute(secret, deviceId, timestamp, nonce, body);

        return new SignedRequest(timestamp, nonce, signature);
    }

    /// <summary>
    /// 按固定规则计算签名值。服务端用同一规则校验。
    /// </summary>
    /// <param name="secret">共享密钥。</param>
    /// <param name="deviceId">设备ID。</param>
    /// <param name="timestamp">Unix 秒级时间戳。</param>
    /// <param name="nonce">随机数。</param>
    /// <param name="body">请求体原文。</param>
    /// <returns>大写十六进制签名值。</returns>
    public static string Compute(
        string secret, string deviceId, string timestamp, string nonce, string body)
    {
        // 先对请求体取 SHA-256，再把摘要送进 HMAC。
        // 这样做有两个好处：一是被签名的串长度固定，不受报文大小影响；
        // 二是先摘要再签名可避免直接对超长内容做 HMAC 带来的额外开销。
        var bodyHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(body ?? "")));

        var canonical = BuildCanonical(deviceId, timestamp, nonce, bodyHash);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// 构造被签名的规范串。四个字段以换行分隔，顺序固定。
    /// </summary>
    /// <remarks>
    /// 换行符在设备ID、时间戳、随机数、摘要里都不可能自然出现
    /// （前三者是受限字符集，后者是十六进制），
    /// 因此不存在「字段边界被顶掉」的拼接歧义。
    /// </remarks>
    private static string BuildCanonical(
        string deviceId, string timestamp, string nonce, string bodyHash)
        => string.Concat(
            deviceId ?? "", "\n",
            timestamp ?? "", "\n",
            nonce ?? "", "\n",
            bodyHash);

    /// <summary>
    /// 生成一个新的共享密钥（128 位随机数的十六进制表示）。
    /// </summary>
    /// <returns>32 个字符的大写十六进制串。</returns>
    public static string GenerateSecret()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(NonceBytes));
}
