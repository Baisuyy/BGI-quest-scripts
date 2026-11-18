using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BetterGenshinImpact.Core.Mys;

/// <summary>
/// 米游社网页端接口的公共常量与 DS 签名。
/// <para>
/// 米游社所有 <c>api-takumi-record</c> 接口都要 <c>DS</c> 头，算法固定：
/// <c>md5(salt&amp;t&amp;r&amp;b&amp;q)</c>，拼成 <c>t,r,hash</c> 三截。
/// 把它单独放一处，避免每个客户端各抄一份、盐值改一次要改好几处。
/// </para>
/// </summary>
public static class MysSign
{
    /// <summary>
    /// 网页端 DS 固定盐值（v2）。
    /// </summary>
    public const string Salt = "xV8v4Qu54lUKrEYFZkJhB8cuOh9Asafs";

    /// <summary>
    /// 请求头里的米游社 App 版本，和 UA 里的版本号保持一致。
    /// </summary>
    public const string AppVersion = "2.11.1";

    /// <summary>
    /// 米游社 App 的 UA。接口会按 UA 判断来源渠道，用浏览器 UA 会被判成非法请求。
    /// </summary>
    public const string AppUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
        + "Chrome/120.0.0.0 Safari/537.36 miHoYoBBS/" + AppVersion;

    /// <summary>
    /// 生成 <c>DS</c> 请求头。
    /// </summary>
    /// <param name="query">不含问号的查询串，参数顺序必须和实际请求一致。</param>
    /// <param name="timestamp">秒级时间戳，测试时传固定值。</param>
    /// <param name="random">6 位随机串，测试时传固定值。</param>
    public static string BuildDs(string query, long? timestamp = null, int? random = null)
    {
        var t = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var r = random ?? Random.Shared.Next(100000, 1000000);
        var raw = $"salt={Salt}&t={t}&r={r}&b=&q={query}";
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        return string.Create(CultureInfo.InvariantCulture, $"{t},{r},{hash}");
    }
}
