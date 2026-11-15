using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service.RemoteTask;

/// <summary>
/// 米游社实时便笺读数。
/// </summary>
/// <param name="CurrentResin">当前原粹树脂。</param>
/// <param name="MaxResin">原粹树脂上限。</param>
/// <param name="CondensedResin">浓缩树脂数量。</param>
/// <param name="RecoverySeconds">距离下一点树脂的秒数。</param>
/// <param name="FinishedTasks">已完成每日委托数。</param>
public sealed record MysDailyNote(
    int CurrentResin,
    int MaxResin,
    int CondensedResin,
    int RecoverySeconds,
    int FinishedTasks);

/// <summary>
/// 米游社「实时便笺」客户端：不用开游戏就能知道树脂、委托、洞天宝钱的状态。
/// <para>
/// 走的是米游社网页端同一个接口，需要用户自己贴 Cookie（含 ltoken 与 ltuid），
/// 以及原神 UID。接口失败时返回 null，由调用方退回截图识别，不会因为 Cookie 过期就让整条指令失败。
/// </para>
/// </summary>
public sealed class MysDailyNoteClient(HttpClient httpClient, ILogger<MysDailyNoteClient> logger)
{
    /// <summary>
    /// 实时便笺接口。
    /// </summary>
    private const string Endpoint = "https://api-takumi-record.mihoyo.com/game_record/app/genshin/api/dailyNote";

    /// <summary>
    /// 米游社网页端 DS 签名固定盐值（v2）。
    /// </summary>
    private const string DsSalt = "xV8v4Qu54lUKrEYFZkJhB8cuOh9Asafs";

    /// <summary>
    /// 国服官服的服务器标识。
    /// </summary>
    private const string ServerOfficial = "cn_gf01";

    /// <summary>
    /// 国服 B 服的服务器标识。
    /// </summary>
    private const string ServerBilibili = "cn_qd01";

    /// <summary>
    /// 查询实时便笺。
    /// </summary>
    /// <param name="cookie">米游社 Cookie 原文。</param>
    /// <param name="uid">原神 UID；留空时尝试从 Cookie 里推断（多数情况推不出来，需要显式配置）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>读数；未配置 UID、网络失败或接口报错时返回 null。</returns>
    public async Task<MysDailyNote?> QueryAsync(string cookie, string uid, CancellationToken ct = default)
    {
        var roleId = string.IsNullOrWhiteSpace(uid) ? GuessUid(cookie) : uid.Trim();
        if (string.IsNullOrWhiteSpace(roleId))
        {
            logger.LogInformation("米游社查询跳过：未配置原神 UID");
            return null;
        }

        var server = GuessServer(roleId);
        var query = $"role_id={roleId}&server={server}";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Endpoint}?{query}");
        request.Headers.TryAddWithoutValidation("Cookie", cookie.Trim());
        request.Headers.TryAddWithoutValidation("DS", BuildDs(query));
        request.Headers.TryAddWithoutValidation("x-rpc-app_version", "2.11.1");
        request.Headers.TryAddWithoutValidation("x-rpc-client_type", "5");
        request.Headers.TryAddWithoutValidation("x-rpc-language", "zh-cn");
        request.Headers.TryAddWithoutValidation("Referer", "https://webstatic.mihoyo.com/");
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 miHoYoBBS/2.11.1");

        try
        {
            using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                logger.LogWarning("米游社查询失败：Cookie 已失效");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("米游社查询返回 {Status}", (int)response.StatusCode);
                return null;
            }

            var root = JsonNode.Parse(text) as JsonObject;
            var retcode = root?["retcode"]?.GetValue<int>() ?? -1;
            if (retcode != 0)
            {
                logger.LogWarning("米游社查询被拒：retcode={Retcode}，message={Message}",
                    retcode, root?["message"]?.GetValue<string>());
                return null;
            }

            var data = root?["data"] as JsonObject;
            if (data == null)
            {
                return null;
            }

            return new MysDailyNote(
                data["current_resin"]?.GetValue<int>() ?? 0,
                data["max_resin"]?.GetValue<int>() ?? 200,
                data["current_condensed_resin"]?.GetValue<int>() ?? 0,
                ParseInt(data["resin_recovery_time"]),
                data["finished_task_num"]?.GetValue<int>() ?? 0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "米游社查询异常");
            return null;
        }
    }

    /// <summary>
    /// 生成 DS 签名：<c>时间戳,随机串,md5(salt&amp;t&amp;r&amp;b&amp;q)</c>。
    /// </summary>
    /// <param name="query">不含问号的查询串。</param>
    private static string BuildDs(string query)
    {
        var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var r = Random.Shared.Next(100000, 1000000).ToString(CultureInfo.InvariantCulture);
        var raw = $"salt={DsSalt}&t={t}&r={r}&b=&q={query}";
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        return $"{t},{r},{hash}";
    }

    /// <summary>
    /// 按 UID 首位判断服务器：1/2 官服，5 B 服，其余按官服处理。
    /// </summary>
    /// <param name="uid">原神 UID。</param>
    private static string GuessServer(string uid)
        => uid.StartsWith('5') ? ServerBilibili : ServerOfficial;

    /// <summary>
    /// 从 Cookie 里找形如 9 位数字的 UID。找不到就返回空，交给用户显式配置。
    /// </summary>
    /// <param name="cookie">Cookie 原文。</param>
    private static string GuessUid(string cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return string.Empty;
        }

        // 常见写法是 ltuid=123456789，那是米游社账号而非原神 UID，不能直接当 role_id，
        // 所以只在显式带 uid 键时采用，避免用错 ID 打出别人的便笺。
        foreach (var part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
            {
                continue;
            }

            var key = pair[0].Trim();
            if (key.Equals("uid", StringComparison.OrdinalIgnoreCase)
                || key.Equals("game_uid", StringComparison.OrdinalIgnoreCase))
            {
                return pair[1].Trim();
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 接口里秒数有时是数字有时是字符串，统一转成 int。
    /// </summary>
    private static int ParseInt(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed) ? parsed : 0;
    }
}
