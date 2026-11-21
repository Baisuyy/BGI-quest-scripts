using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace BetterGenshinImpact.Core.Mys;

/// <summary>
/// 把扫码登录返回的 <c>Set-Cookie</c> 与 <c>tokens</c> 拼成一条能直接用的 Cookie。
/// <para>
/// 米游社同一份登录态有新旧两套名字（<c>ltuid</c> / <c>ltuid_v2</c>、<c>mid</c> / <c>ltmid_v2</c>）。
/// v2 令牌不是自包含的：服务端要靠同名的 mid 找到签发它的那次会话，少了 mid，
/// <c>ltoken_v2</c> 再新也会被当成未登录（retcode 10001 / 1001 <c>Please login</c>）。
/// 所以这里**不再按白名单挑字段**——扫码给什么就留什么，只额外补齐缺失的别名。
/// </para>
/// <para>
/// 早期版本只保留十几个"看起来重要"的字段，把 <c>ltmid_v2</c>、<c>account_mid_v2</c>
/// 直接丢掉了，于是扫码绑定显示成功、查树脂却一直 Please login。这正是那次事故的根因。
/// </para>
/// </summary>
public static class MysCookieBuilder
{
    /// <summary>
    /// 拼装顺序：账号在前、令牌在后，方便用户对着开发者工具逐个核对。
    /// 不在此列但确实存在的字段会在末尾原样保留。
    /// </summary>
    private static readonly string[] PreferredOrder =
    [
        "ltuid_v2", "ltuid", "account_id_v2", "account_id",
        "ltmid_v2", "mid", "account_mid_v2",
        "ltoken_v2", "ltoken",
        "cookie_token_v2", "cookie_token",
        "stoken_v2", "stoken",
        "login_ticket", "login_uid"
    ];

    /// <summary>
    /// 明确没有价值的字段，拼进请求头只会让排查更糊。
    /// </summary>
    private static readonly string[] IgnoredNames =
    [
        "Path", "Expires", "Max-Age", "Domain", "Secure", "HttpOnly", "SameSite"
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
            if (name.Length == 0)
            {
                continue;
            }

            if (value.Length == 0 || value.Equals("deleted", StringComparison.OrdinalIgnoreCase))
            {
                // 服务端显式作废某个字段时不能留着旧值，否则下次还以为登录态在
                cookie.Remove(name);
                continue;
            }

            // 全收：v2 令牌靠同名的 mid 定位会话，漏一个字段就只剩 Please login
            if (!IgnoredNames.Contains(name, StringComparer.OrdinalIgnoreCase))
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

        // mid 同理：v2 令牌要它才能定位会话，扫码 body 里也会给
        var mid = FirstNonEmpty(cookie, "ltmid_v2", "account_mid_v2", "mid")
                  ?? data?["user_info"]?["mid"]?.ToString();
        if (!string.IsNullOrWhiteSpace(mid))
        {
            cookie.TryAdd("ltmid_v2", mid);
            cookie.TryAdd("account_mid_v2", mid);
        }

        Normalize(cookie);
        return cookie;
    }

    /// <summary>
    /// 从 Cookie 里取米游社账号 ID（<c>ltuid</c> 那一串），取不到返回 null。
    /// <para>
    /// 注意它和原神 UID 不是一回事：账号 ID 一般在 1 亿到 3 亿之间，原神 UID 是 9 位。
    /// </para>
    /// </summary>
    public static string? AccountId(string cookie)
        => string.IsNullOrWhiteSpace(cookie)
            ? null
            : FirstNonEmpty(cookie, "ltuid_v2", "ltuid", "account_id_v2", "account_id");

    /// <summary>
    /// 两套名字互相补齐，**但不删任何一边**。
    /// <para>
    /// 新版接口读 <c>_v2</c>，老接口（<c>binding/api/getUserGameRolesByCookie</c>、
    /// <c>dailyNote</c>）读的是 <c>ltuid</c> / <c>mid</c>。只留一边就一定有一条链路查不到，
    /// 而这点体积（几十字节）完全不值得省。
    /// </para>
    /// <para>
    /// 只补 ID 类字段。令牌类不能互抄：<c>ltoken_v2</c> 的值以 <c>v2_</c> 开头，
    /// 塞进 <c>ltoken</c> 会被服务端当成格式错误的 v1 令牌，比缺字段更糟。
    /// </para>
    /// </summary>
    private static void Normalize(Dictionary<string, string> cookie)
    {
        Alias(cookie, "ltuid_v2", "ltuid");
        Alias(cookie, "ltmid_v2", "mid");
        Alias(cookie, "account_id_v2", "account_id");
        Alias(cookie, "account_mid_v2", "ltmid_v2");
        // 上面可能刚从 account_mid_v2 补出 ltmid_v2，再同步一次 mid
        Alias(cookie, "ltmid_v2", "mid");
    }

    /// <summary>
    /// 一边有、另一边没有时复制一份过去；两边都有时以 <paramref name="current"/> 为准。
    /// </summary>
    private static void Alias(Dictionary<string, string> cookie, string current, string legacy)
    {
        if (cookie.TryGetValue(current, out var value) && value.Length > 0)
        {
            cookie[legacy] = value;
            return;
        }

        if (cookie.TryGetValue(legacy, out var fallback) && fallback.Length > 0)
        {
            cookie[current] = fallback;
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
