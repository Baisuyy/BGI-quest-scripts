using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Mys;
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
    /// 上一次查询失败的原因，供上层原样转述给用户。
    /// <para>
    /// 米游社只回一个 retcode，5003 既可能是 Cookie 过期，也可能是复制时少了字段，
    /// 不说明白用户只能猜。
    /// </para>
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 实时便笺接口。
    /// </summary>
    private const string Endpoint = "https://api-takumi-record.mihoyo.com/game_record/app/genshin/api/dailyNote";

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
        LastError = null;
        var normalized = NormalizeCookie(cookie);
        var roleId = string.IsNullOrWhiteSpace(uid) ? GuessUid(normalized) : uid.Trim();
        roleId = StripToDigits(roleId);
        if (string.IsNullOrWhiteSpace(roleId))
        {
            LastError = "没配置原神 UID（设置 → 远程任务 → 米游社 UID），从 Cookie 里也推不出来。";
            logger.LogInformation("米游社查询跳过：未配置原神 UID");
            return null;
        }

        if (!normalized.Contains("ltoken", StringComparison.OrdinalIgnoreCase))
        {
            LastError = "Cookie 里没有 ltoken，粘的是不是不完整？需要带 ltoken_v2 与 ltuid_v2 的完整 Cookie。";
            logger.LogWarning("米游社查询跳过：Cookie 缺少 ltoken");
            return null;
        }

        var server = GuessServer(roleId);
        var query = $"role_id={roleId}&server={server}";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Endpoint}?{query}");
        request.Headers.TryAddWithoutValidation("Cookie", normalized);
        request.Headers.TryAddWithoutValidation("DS", MysSign.BuildDs(query));
        request.Headers.TryAddWithoutValidation("x-rpc-app_version", MysSign.AppVersion);
        request.Headers.TryAddWithoutValidation("x-rpc-client_type", "5");
        request.Headers.TryAddWithoutValidation("x-rpc-language", "zh-cn");
        request.Headers.TryAddWithoutValidation("Referer", "https://webstatic.mihoyo.com/");
        request.Headers.TryAddWithoutValidation("User-Agent", MysSign.AppUserAgent);

        try
        {
            using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                LastError = "Cookie 已失效，重新登录米游社再复制一次。";
                logger.LogWarning("米游社查询失败：Cookie 已失效");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                LastError = $"米游社返回 HTTP {(int)response.StatusCode}。";
                logger.LogWarning("米游社查询返回 {Status}", (int)response.StatusCode);
                return null;
            }

            var root = JsonNode.Parse(text) as JsonObject;
            var retcode = root?["retcode"]?.GetValue<int>() ?? -1;
            if (retcode != 0)
            {
                LastError = retcode switch
                {
                    // 米游社对「登录态不完整」和「登录态过期」都给 5003，区分不了就都提示到
                    5003 => "米游社判为未登录（retcode 5003）。Cookie 多半缺 cookie_token 或已过期："
                            + "重新登录米游社网页版，F12 → Network 里复制完整的 Cookie 请求头。",
                    10001 => "米游社参数不对（retcode 10001），检查 UID 是否填成了米游社账号 ID。",
                    _ => $"米游社拒绝请求（retcode {retcode}）。",
                };
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
            LastError = $"米游社请求异常：{e.Message}";
            logger.LogWarning(e, "米游社查询异常");
            return null;
        }
    }

    /// <summary>
    /// 把用户粘来的 Cookie 收拾成一行。
    /// <para>
    /// 从开发者工具复制的 Cookie 常带换行、行尾分号、缩进；还可能因为折行把
    /// <c>ltuid_v2=</c> 和值拆散，只剩一个裸数字。这些格式问题服务端一律回 5003，
    /// 看上去像「登录过期」，实际只是拼错了。
    /// </para>
    /// </summary>
    /// <param name="cookie">原始输入。</param>
    /// <returns>可直接放进请求头的 Cookie 串。</returns>
    private static string NormalizeCookie(string cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return string.Empty;
        }

        var parts = new List<string>();
        var hasLtuid = false;
        foreach (var rawLine in cookie.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var rawPart in rawLine.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var part = rawPart.Trim();
                if (part.Length == 0)
                {
                    continue;
                }

                // 裸数字：多半是 ltuid_v2 的值被换行拆了出来
                if (part.All(char.IsAsciiDigit))
                {
                    if (!hasLtuid)
                    {
                        parts.Add("ltuid_v2=" + part);
                        hasLtuid = true;
                    }

                    continue;
                }

                if (part.StartsWith("ltuid_v2=", StringComparison.OrdinalIgnoreCase)
                    || part.StartsWith("ltuid=", StringComparison.OrdinalIgnoreCase))
                {
                    hasLtuid = true;
                }

                parts.Add(part);
            }
        }

        // 只有 ltuid 没有 ltuid_v2 时补一份：新版接口只认后者
        if (parts.FirstOrDefault(p => p.StartsWith("ltuid=", StringComparison.OrdinalIgnoreCase)) is { } legacy
            && !hasLtuid)
        {
            parts.Add("ltuid_v2=" + legacy["ltuid=".Length..]);
        }

        return string.Join("; ", parts);
    }

    /// <summary>
    /// UID 里混进空格、引号、中文标点时只留数字。
    /// </summary>
    private static string StripToDigits(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsAsciiDigit(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
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
