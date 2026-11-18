using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace BetterGenshinImpact.Core.Mys;

/// <summary>
/// 把扫码登录返回的 <c>Set-Cookie</c> 与 <c>tokens</c> 拼成一条能直接用的 Cookie。
/// <para>
/// 米游社同一份登录态有新旧两套名字（<c>ltuid</c> / <c>ltuid_v2</c>），而网页端接口只认
/// 带 <c>_v2</c> 的那套。扫码接口不一定把两套都给全，所以这里做一次补齐：
/// 缺 <c>ltuid_v2</c> 就用 <c>account_id</c>，缺 <c>cookie_token_v2</c> 就从 tokens 里按类型取。
/// </para>
/// </summary>
public static class MysCookieBuilder
{
    /// <summary>
    /// 拼装顺序：账号在前、令牌在后，方便用户对着开发者工具逐个核对。
    /// </summary>
    private static readonly string[] PreferredOrder =
    [
        "ltuid_v2", "ltuid", "account_id_v2", "account_id",
        "ltoken_v2", "ltoken",
        "cookie_token_v2", "cookie_token",
        "stoken_v2", "stoken",
        "mid", "login_ticket"
    ];

    /// <summary>
    /// 从响应头与响应体的 tokens 里收集登录态字段。
    /// </summary>
    /// <param name="setCookies"><c>Set-Cookie</c> 头的原始值集合。</param>
    /// <param name="data">扫码状态接口返回的 <c>data</c> 对象。</param>
    public static Dictionary<string, string> Collect(IEnumerable<string>? setCookies, JsonNode? data)
    {
        var cookie = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in setCookies ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            // Set-Cookie 只取第一段 name=value，后面是 Path/Expires 等属性
            var pair = raw.Split(';', 2)[0];
            var split = pair.IndexOf('=');
            if (split <= 0)
            {
                continue;
            }

            var name = pair[..split].Trim();
            var value = pair[(split + 1)..].Trim().Trim('"');
            if (value.Length == 0 || value.Equals("deleted", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (PreferredOrder.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                cookie[name] = value;
            }
        }

        // tokens 是扫码接口在 body 里返回的登录态，类型：1 stoken / 2 ltoken / 4 cookie_token
        if (data?["tokens"] is JsonArray tokens)
        {
            foreach (var item in tokens.OfType<JsonObject>())
            {
                var token = item["token"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                var type = item["token_type"]?.GetValue<int>() ?? 0;
                var name = type switch
                {
                    1 => "stoken_v2",
                    2 => "ltoken_v2",
                    4 => "cookie_token_v2",
                    _ => null
                };
                if (name is not null)
                {
                    cookie.TryAdd(name, token);
                }
            }
        }

        // 账号 ID：新版接口给 account_id，旧版给 ltuid，两边都能推出 v2 的名字
        var accountId = FirstNonEmpty(cookie, "account_id_v2", "account_id", "ltuid_v2", "ltuid")
                        ?? data?["user_info"]?["aid"]?.ToString();
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            cookie.TryAdd("ltuid_v2", accountId);
            cookie.TryAdd("account_id_v2", accountId);
        }

        Normalize(cookie);
        return cookie;
    }

    /// <summary>
    /// 把旧名字升级成 <c>_v2</c>，升级完就把旧字段去掉：
    /// 两套名字同时出现会让 Cookie 变长几十个字符，而接口只读新名字。
    /// </summary>
    private static void Normalize(Dictionary<string, string> cookie)
    {
        Promote(cookie, "ltuid_v2", "ltuid");
        Promote(cookie, "ltoken_v2", "ltoken");
        Promote(cookie, "cookie_token_v2", "cookie_token");
        Promote(cookie, "stoken_v2", "stoken");
    }

    private static void Promote(Dictionary<string, string> cookie, string current, string legacy)
    {
        if (!cookie.ContainsKey(current) && cookie.TryGetValue(legacy, out var value))
        {
            cookie[current] = value;
        }

        if (cookie.ContainsKey(current))
        {
            cookie.Remove(legacy);
        }
    }

    /// <summary>
    /// 把收集到的字段拼成请求头里的一条 Cookie。
    /// </summary>
    public static string Compose(IReadOnlyDictionary<string, string> cookie)
    {
        var parts = new List<string>(cookie.Count);
        foreach (var name in PreferredOrder)
        {
            if (cookie.TryGetValue(name, out var value) && value.Length > 0)
            {
                parts.Add($"{name}={value}");
            }
        }

        // 兜底：名字不在白名单里但确实有用的字段也别丢
        foreach (var (name, value) in cookie)
        {
            if (!PreferredOrder.Contains(name, StringComparer.OrdinalIgnoreCase) && value.Length > 0)
            {
                parts.Add($"{name}={value}");
            }
        }

        return string.Join("; ", parts);
    }

    /// <summary>
    /// 检查这条 Cookie 能不能查便笺；能用返回 null，否则返回给用户看的原因。
    /// </summary>
    public static string? Validate(string cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return "没有拿到登录态，请重新扫码。";
        }

        if (FirstNonEmpty(cookie, "ltuid_v2", "ltuid", "account_id_v2", "account_id") is null)
        {
            return "登录态里没有账号 ID，请重新扫码。";
        }

        if (FirstNonEmpty(cookie, "ltoken_v2", "ltoken") is null)
        {
            return "登录态里没有 ltoken，请重新扫码。";
        }

        return null;
    }

    /// <summary>
    /// 给用户看的脱敏描述：只保留账号 ID，令牌一律打码。
    /// </summary>
    public static string Describe(string cookie)
    {
        var uid = FirstNonEmpty(cookie, "ltuid_v2", "ltuid", "account_id_v2", "account_id") ?? "未知";
        var fields = cookie.Split(';', StringSplitOptions.RemoveEmptyEntries).Length;
        return $"米游社账号 {uid}（{fields} 个字段，令牌已隐藏）";
    }

    /// <summary>
    /// 按顺序找第一个非空值；<paramref name="source"/> 既可以是 Cookie 串也可以是键值字典。
    /// </summary>
    private static string? FirstNonEmpty(IReadOnlyDictionary<string, string> cookie, params string[] names)
    {
        foreach (var name in names)
        {
            if (cookie.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? FirstNonEmpty(string cookie, params string[] names)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = part.IndexOf('=');
            if (split > 0)
            {
                map[part[..split].Trim()] = part[(split + 1)..].Trim();
            }
        }

        return FirstNonEmpty(map, names);
    }

    /// <summary>
    /// 米游社账号 ID 一定是数字，用于把 UID 里的空格、引号等杂字符剔掉。
    /// </summary>
    public static string DigitsOnly(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value.Where(char.IsAsciiDigit).ToArray());
    }

    /// <summary>
    /// 按账号 ID 推断服务器：1/2 官服，5 B 服。
    /// </summary>
    public static string GuessServer(string accountOrUid)
        => accountOrUid.StartsWith('5') ? "cn_qd01" : "cn_gf01";

    /// <summary>
    /// 把秒级时间戳格式化成「还剩几分几秒」，用于二维码过期提示。
    /// </summary>
    public static string RemainText(DateTimeOffset expireAt, DateTimeOffset now)
    {
        var seconds = (int)Math.Max(0, (expireAt - now).TotalSeconds);
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60} 分 {seconds % 60} 秒");
    }
}
