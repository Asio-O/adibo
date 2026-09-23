// SPDX-License-Identifier: MIT
using System.Security.Cryptography;
using System.Text;

namespace AdiboProxy.Rcouyi;

/// <summary>
/// 复刻网页端的 <c>xx-cf-source</c> 请求头。
///
/// 前端实现（去混淆后）：
/// <code>
/// key  = "b1d8c88f9dfc9316f4b37f462d77ae5d";   // 32 字节 -> AES-256
/// host = BASE_URL.replace(/^https?:\/\//i, "");
/// value = Base64(AES_ECB_PKCS7(host, key));
/// </code>
/// </summary>
public static class RcouyiSignature
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("b1d8c88f9dfc9316f4b37f462d77ae5d");

    /// <summary>计算签名值。ECB 模式不使用 IV，前端代码里的 iv 参数实际不参与运算。</summary>
    public static string Compute(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        var host = StripScheme(baseUrl);
        var plain = Encoding.UTF8.GetBytes(host);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = Key;

        using var encryptor = aes.CreateEncryptor();
        var cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);
        return Convert.ToBase64String(cipher);
    }

    private static string StripScheme(string url)
    {
        var index = url.IndexOf("://", StringComparison.Ordinal);
        var host = index >= 0 ? url[(index + 3)..] : url;
        return host.TrimEnd('/');
    }
}
