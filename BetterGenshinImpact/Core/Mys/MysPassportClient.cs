using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Core.Mys;

/// <summary>
/// 扫码登录的四个状态。
/// </summary>
public enum MysQrState
{
    /// <summary>二维码已生成，还没被扫。</summary>
    WaitScan,

    /// <summary>扫到了，手机上还差一次「确认登录」。</summary>
    Scanned,

    /// <summary>确认完成，登录态已拿到。</summary>
    Confirmed,

    /// <summary>二维码过期或票据作废，需要重新生成。</summary>
    Expired,

    /// <summary>接口报错。</summary>
    Failed
}

/// <summary>
/// 一次扫码会话：二维码内容、轮询用的票据、生成时绑定的设备 ID。
/// </summary>
/// <param name="Url">二维码里要放的内容，用米游社 App 扫。</param>
/// <param name="Ticket">轮询扫码状态的票据。</param>
/// <param name="DeviceId">生成二维码时用的设备 ID，轮询必须用同一个。</param>
/// <param name="ExpiresAt">二维码过期时间。</param>
public sealed record MysQrSession(string Url, string Ticket, string DeviceId, DateTimeOffset ExpiresAt);

/// <summary>
/// 米游社账号下的一个原神角色。
/// </summary>
/// <param name="Uid">游戏 UID。</param>
/// <param name="Nickname">角色名。</param>
/// <param name="Level">冒险等阶。</param>
/// <param name="Region">服务器标识，如 cn_gf01。</param>
public sealed record MysGameRole(string Uid, string Nickname, int Level, string Region)
{
    /// <summary>
    /// 给用户看的服务器名。
    /// </summary>
    public string ServerName => Region == "cn_qd01" ? "B 服" : "官服";
}

/// <summary>
/// 一次轮询的结果。
/// </summary>
/// <param name="State">扫码状态。</param>
/// <param name="Cookie">确认后才有的登录态。</param>
/// <param name="Roles">确认后才有的角色列表；查询失败时为空。</param>
/// <param name="Message">失败原因，直接转述给用户。</param>
public sealed record MysScanResult(
    MysQrState State,
    string? Cookie = null,
    IReadOnlyList<MysGameRole>? Roles = null,
    string? Message = null);

/// <summary>
/// 米游社扫码登录客户端。
/// <para>
/// 走的是网页端自己的扫码通道：生成二维码 → 手机米游社 App 扫 → 轮询到 Confirmed 时，
/// 登录态以 <c>Set-Cookie</c> 的形式返回。全程不需要用户碰开发者工具，也不需要密码。
/// </para>
/// <para>
/// 注意 <c>createQRLogin</c> 要求 <c>x-rpc-app_id</c> 与 <c>x-rpc-device_id</c> 两个头，
/// 缺一个就回 -3005「参数不合法」；轮询必须复用同一个 device_id，换一个票据就作废。
/// </para>
/// </summary>
public sealed class MysPassportClient(HttpClient httpClient, ILogger<MysPassportClient> logger)
{
    /// <summary>
    /// 米游社网页端的应用 ID。
    /// </summary>
    private const string AppId = "bll8iq97cem8";

    /// <summary>
    /// 国服扫码接口的域名。国际服是 passport-api.hoyolab.com，这里只处理国服。
    /// </summary>
    private const string PassportHost = "https://passport-api.miyoushe.com";

    /// <summary>
    /// 生成二维码。
    /// </summary>
    private const string CreateQrUrl = PassportHost + "/account/ma-cn-passport/web/createQRLogin";

    /// <summary>
    /// 轮询扫码状态。
    /// </summary>
    private const string QueryQrUrl = PassportHost + "/account/ma-cn-passport/web/queryQRLoginStatus";

    /// <summary>
    /// 按 Cookie 查账号下的原神角色。
    /// </summary>
    private const string RolesByCookieUrl =
        "https://api-takumi.mihoyo.com/binding/api/getUserGameRolesByCookie?game_biz=hk4e_cn";

    /// <summary>
    /// 米游社「我的角色」卡片接口，与实时便笺同源，用同一套 v2 登录态。
    /// </summary>
    private const string RecordCardUrl =
        "https://api-takumi-record.mihoyo.com/game_record/card/wapi/getGameRecordCard";

    /// <summary>
    /// 二维码有效期：接口给 5 分钟，这里按 3 分钟算，留出扫码和确认的余量。
    /// </summary>
    private static readonly TimeSpan QrLifetime = TimeSpan.FromMinutes(3);

    /// <summary>
    /// 生成二维码。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="MysLoginException">接口拒绝或返回结构不对。</exception>
    public async Task<MysQrSession> CreateQrAsync(CancellationToken ct = default)
    {
        var deviceId = Guid.NewGuid().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Post, CreateQrUrl)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        AddPassportHeaders(request, deviceId);

        using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var root = Parse(text, "生成二维码");

        var data = root["data"] as JsonObject;
        var url = data?["url"]?.GetValue<string>();
        var ticket = data?["ticket"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(ticket))
        {
            throw new MysLoginException("米游社没有返回二维码，稍后再试。");
        }

        return new MysQrSession(url, ticket, deviceId, DateTimeOffset.UtcNow + QrLifetime);
    }

    /// <summary>
    /// 查询一次扫码状态；确认后同时把角色列表一起取回来。
    /// </summary>
    /// <param name="session">生成二维码时的会话。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<MysScanResult> QueryAsync(MysQrSession session, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, QueryQrUrl)
        {
            Content = new StringContent(
                new JsonObject { ["ticket"] = session.Ticket }.ToJsonString(),
                Encoding.UTF8,
                "application/json")
        };
        AddPassportHeaders(request, session.DeviceId);
        request.Headers.TryAddWithoutValidation("Origin", "https://user.miyoushe.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://user.miyoushe.com/");

        using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        JsonObject root;
        try
        {
            root = Parse(text, "查询扫码状态");
        }
        catch (MysLoginException e)
        {
            return new MysScanResult(MysQrState.Failed, Message: e.Message);
        }

        var data = root["data"] as JsonObject;
        var status = data?["status"]?.GetValue<string>() ?? string.Empty;
        switch (status)
        {
            case "Created":
                return new MysScanResult(MysQrState.WaitScan);
            case "Scanned":
                return new MysScanResult(MysQrState.Scanned);
            case "Confirmed":
                break;
            case "Expired":
            case "Canceled":
                return new MysScanResult(MysQrState.Expired);
            default:
                logger.LogWarning("米游社扫码返回了没见过的状态：{Status}", status);
                return new MysScanResult(MysQrState.Failed, Message: $"米游社返回了未知状态 {status}。");
        }

        var cookie = MysCookieBuilder.Compose(MysCookieBuilder.Collect(ReadSetCookies(response), data));
        var invalid = MysCookieBuilder.Validate(cookie);
        if (invalid is not null)
        {
            return new MysScanResult(MysQrState.Failed, Message: invalid);
        }

        var roles = await GetRolesAsync(cookie, ct).ConfigureAwait(false);
        return new MysScanResult(MysQrState.Confirmed, cookie, roles);
    }

    /// <summary>
    /// 按 Cookie 查账号下的原神角色。
    /// <para>
    /// 先走米游社「我的角色」卡片接口（<c>getGameRecordCard</c>）：它和实时便笺同源，
    /// 用的是同一套 v2 登录态，绑定时能顺手验证这份 Cookie 到底能不能查数据。
    /// 卡片接口拿不到时再退回老的 binding 接口——后者只认 v1 的 <c>cookie_token</c>，
    /// 扫码得到的新登录态基本都查不出来，所以只能当兜底。
    /// </para>
    /// <para>
    /// 查不到不算失败：有些账号只在米游社绑过通行证、没同步角色，这时候让用户手填 UID
    /// 比让整次绑定失败更合理。
    /// </para>
    /// </summary>
    /// <param name="cookie">扫码得到的登录态。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<IReadOnlyList<MysGameRole>> GetRolesAsync(string cookie, CancellationToken ct = default)
    {
        var fromCard = await GetRolesFromCardAsync(cookie, ct).ConfigureAwait(false);
        if (fromCard.Count > 0)
        {
            return fromCard;
        }

        return await GetRolesFromBindingAsync(cookie, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 用「我的角色」卡片接口列角色：与实时便笺同源，能顺带验证登录态是否真的可用。
    /// </summary>
    private async Task<IReadOnlyList<MysGameRole>> GetRolesFromCardAsync(string cookie, CancellationToken ct)
    {
        var accountId = MysCookieBuilder.AccountId(cookie);
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return [];
        }

        var query = "uid=" + accountId;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{RecordCardUrl}?{query}");
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        request.Headers.TryAddWithoutValidation("DS", MysSign.BuildDs(query));
        request.Headers.TryAddWithoutValidation("x-rpc-app_version", MysSign.AppVersion);
        request.Headers.TryAddWithoutValidation("x-rpc-client_type", "5");
        request.Headers.TryAddWithoutValidation("x-rpc-language", "zh-cn");
        request.Headers.TryAddWithoutValidation("User-Agent", MysSign.AppUserAgent);
        request.Headers.TryAddWithoutValidation("Referer", "https://webstatic.mihoyo.com/");

        try
        {
            using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("米游社角色卡片 HTTP {Status}", (int)response.StatusCode);
                return [];
            }

            var root = JsonNode.Parse(text) as JsonObject;
            var retcode = root?["retcode"]?.GetValue<int>() ?? -1;
            if (retcode != 0)
            {
                logger.LogInformation("米游社角色卡片被拒：retcode={Retcode}，message={Message}",
                    retcode, root?["message"]?.ToString());
                return [];
            }

            if (root!["data"]?["list"] is not JsonArray list)
            {
                return [];
            }

            var roles = new List<MysGameRole>(list.Count);
            foreach (var item in list.OfType<JsonObject>())
            {
                // 一个账号可能同时玩崩铁/绝区零，这里只要原神
                var biz = item["game_biz"]?.ToString();
                if (!string.IsNullOrEmpty(biz) && !biz.Equals("hk4e_cn", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var uid = item["game_uid"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(uid))
                {
                    continue;
                }

                roles.Add(new MysGameRole(
                    uid,
                    item["nickname"]?.ToString() ?? "旅行者",
                    ParseLevel(item["level"]),
                    item["region"]?.ToString() ?? "cn_gf01"));
            }

            if (roles.Count > 0)
            {
                logger.LogInformation("米游社角色卡片：取到 {Count} 个原神角色", roles.Count);
            }

            return roles;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "米游社角色卡片查询异常");
            return [];
        }
    }

    /// <summary>
    /// 老接口兜底：只认 <c>cookie_token</c>，手填 Cookie 的老用户仍可能走到这里。
    /// </summary>
    private async Task<IReadOnlyList<MysGameRole>> GetRolesFromBindingAsync(string cookie, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RolesByCookieUrl);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        request.Headers.TryAddWithoutValidation("User-Agent", MysSign.AppUserAgent);
        request.Headers.TryAddWithoutValidation("Referer", "https://webstatic.mihoyo.com/");
        request.Headers.TryAddWithoutValidation("x-rpc-app_version", MysSign.AppVersion);
        request.Headers.TryAddWithoutValidation("x-rpc-client_type", "5");

        try
        {
            using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("米游社角色列表 HTTP {Status}", (int)response.StatusCode);
                return [];
            }

            var root = JsonNode.Parse(text) as JsonObject;
            if (root?["retcode"]?.GetValue<int>() is not 0)
            {
                logger.LogInformation("米游社角色列表被拒：retcode={Retcode}，message={Message}",
                    root?["retcode"]?.ToString(), root?["message"]?.ToString());
                return [];
            }

            if (root["data"]?["list"] is not JsonArray list)
            {
                return [];
            }

            var roles = new List<MysGameRole>(list.Count);
            foreach (var item in list.OfType<JsonObject>())
            {
                var uid = item["game_uid"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(uid))
                {
                    continue;
                }

                roles.Add(new MysGameRole(
                    uid,
                    item["nickname"]?.ToString() ?? "旅行者",
                    ParseLevel(item["level"]),
                    item["region"]?.ToString() ?? "cn_gf01"));
            }

            return roles;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "米游社角色列表查询异常");
            return [];
        }
    }

    /// <summary>
    /// 扫码接口要求的两个头，生成与轮询都得带。
    /// </summary>
    private static void AddPassportHeaders(HttpRequestMessage request, string deviceId)
    {
        request.Headers.TryAddWithoutValidation("x-rpc-app_id", AppId);
        request.Headers.TryAddWithoutValidation("x-rpc-device_id", deviceId);
        request.Headers.TryAddWithoutValidation("User-Agent", MysSign.AppUserAgent);
    }

    /// <summary>
    /// 从响应里取全部 Set-Cookie 原文。
    /// </summary>
    private static IEnumerable<string> ReadSetCookies(HttpResponseMessage response)
    {
        return response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values
            : Enumerable.Empty<string>();
    }

    /// <summary>
    /// 解析返回的 JSON，并检查 retcode。
    /// </summary>
    private static JsonObject Parse(string text, string action)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception)
        {
            root = null;
        }

        if (root is null)
        {
            throw new MysLoginException($"{action}失败：米游社返回的不是 JSON。");
        }

        var retcode = root["retcode"]?.GetValue<int>() ?? -1;
        if (retcode != 0)
        {
            var message = root["message"]?.ToString() ?? "未知错误";
            throw new MysLoginException($"{action}失败（retcode {retcode}）：{message}");
        }

        return root;
    }

    /// <summary>
    /// 等级在接口里有时是数字有时是字符串。
    /// </summary>
    private static int ParseLevel(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var number))
            {
                return number;
            }

            if (value.TryGetValue<string>(out var text)
                && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }
}

/// <summary>
/// 扫码登录过程中的可展示错误。
/// </summary>
public sealed class MysLoginException(string message) : Exception(message);
