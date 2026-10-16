using System;
using System.Net.Http;
using System.Security.Cryptography;

namespace CICUser;

/// <summary>
/// 服务端证书的信任策略。
/// </summary>
/// <remarks>
/// <para>
/// 自动注册会把新密钥交给服务端，所以「对面到底是不是真的那台服务器」
/// 直接决定整个签名方案是否成立——证书校验被绕过，TLS 就只是心理安慰。
/// </para>
/// <para>
/// 默认交给<b>系统信任链</b>判断，适用于正式 CA 签发的证书，
/// 或已把自建 CA 根证书导入设备信任库的情形。
/// </para>
/// <para>
/// 自签证书且不便改动系统信任库时，可在配置里填证书指纹，改为<b>只认这一张</b>。
/// 这是证书绑定（pinning）：不依赖任何 CA，代价是证书轮换时必须同步更新指纹。
/// </para>
/// </remarks>
internal static class CertificateTrust
{
    /// <summary>
    /// 按配置构造 HTTP 处理器。
    /// </summary>
    /// <param name="settings">插件配置。</param>
    /// <returns>已应用证书校验策略的处理器。</returns>
    public static HttpMessageHandler CreateHandler(PluginSettings settings)
    {
        var handler = new SocketsHttpHandler();

        var pinned = PluginSettings.NormalizeFingerprint(settings.ServerCertificateFingerprint);
        if (pinned.Length == 0)
        {
            // 未绑定指纹：沿用系统信任链，不做任何干预
            return handler;
        }

        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
        {
            if (certificate is null)
            {
                return false;
            }

            // 刻意不看 sslPolicyErrors：绑定指纹的语义就是「只认这张证书」，
            // 而自签证书本来就带着 UntrustedRoot 之类的错误，
            // 若再要求 errors == None，自签场景将永远无法通过。
            var actual = Convert.ToHexString(
                certificate.GetCertHash(HashAlgorithmName.SHA256));

            return string.Equals(actual, pinned, StringComparison.OrdinalIgnoreCase);
        };

        return handler;
    }
}
