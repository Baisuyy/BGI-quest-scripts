using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Mys;
using BetterGenshinImpact.Service.Interface;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service.RemoteTask;

/// <summary>
/// 开始绑定的结果：二维码图片 + 一句发给用户的说明。
/// </summary>
/// <param name="QrImage">PNG 二维码。</param>
/// <param name="Text">附带说明。</param>
public sealed record MysBindStart(byte[] QrImage, string Text);

/// <summary>
/// 一次轮询的结果。
/// </summary>
/// <param name="Text">发给用户的话。</param>
/// <param name="Confirmed">是否已经绑定成功。</param>
public sealed record MysBindOutcome(string Text, bool Confirmed = false);

/// <summary>
/// 米游社扫码绑定：管住「一个会话一次扫码」的状态，把登录态写进配置。
/// <para>
/// 状态放在内存里而不是配置文件：二维码三分钟就过期，重启后本来就该重扫，
/// 落盘反而会留下一堆用不了的票据。
/// </para>
/// <para>
/// 绑定分两步走：先回二维码图，用户扫完再发一次「绑定米游社」触发轮询。这样不需要
/// 机器人有主动推送通道，也不占用一个长连接等三分钟。
/// </para>
/// </summary>
public sealed class MysBindingService(
    MysPassportClient passport,
    MysDailyNoteClient dailyNote,
    IConfigService configService,
    ILogger<MysBindingService> logger)
{
    /// <summary>
    /// 选角色的时间窗口：扫完码不选就过期，避免几天后一句「绑定米游社 2」突然改掉 UID。
    /// </summary>
    private static readonly TimeSpan RoleChoiceLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 按会话隔离：私聊用发送者 OpenID 当键，多个人同时绑不会互相顶掉。
    /// </summary>
    private readonly ConcurrentDictionary<string, PendingBind> _binds = new(StringComparer.Ordinal);

    /// <summary>
    /// 这个会话是否有还能用的二维码或待选角色。
    /// </summary>
    public bool HasPending(string key) => TryGetLive(key, out _);

    /// <summary>
    /// 取等待选择的角色列表；桌面端用它填下拉框。没有待选角色时返回 null。
    /// </summary>
    public IReadOnlyList<MysGameRole>? PeekRoles(string key)
        => TryGetLive(key, out var pending) ? pending.Roles : null;

    /// <summary>
    /// 生成二维码。
    /// </summary>
    /// <param name="key">会话键。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<MysBindStart> StartAsync(string key, CancellationToken ct = default)
    {
        var session = await passport.CreateQrAsync(ct).ConfigureAwait(false);
        var png = MysQrCode.Render(session.Url);

        _binds[key] = new PendingBind { Qr = session, ExpiresAt = session.ExpiresAt };
        logger.LogInformation("米游社扫码绑定：已生成二维码，会话 {Key}，票据尾号 {Tail}",
            ShortKey(key), session.Ticket[^6..]);

        return new MysBindStart(png,
            "用米游社 App 扫上面的二维码。\n"
            + "扫完先在手机上点「确认登录」，再回来发一句「绑定米游社」，我就把账号记下来。\n"
            + $"二维码 {MysCookieBuilder.RemainText(session.ExpiresAt, DateTimeOffset.UtcNow)} 内有效。");
    }

    /// <summary>
    /// 轮询一次扫码状态；确认后写配置。
    /// </summary>
    /// <param name="key">会话键。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<MysBindOutcome> PollAsync(string key, CancellationToken ct = default)
    {
        if (!TryGetLive(key, out var pending))
        {
            return new MysBindOutcome("还没有进行中的扫码。先发「绑定米游社」，我把二维码发给你。");
        }

        // 已经扫完、在等用户选角色：再扫一次没有意义，先把选择做完
        if (pending.Roles is { Count: > 1 })
        {
            return new MysBindOutcome(pending.BuildRoleChoiceText());
        }

        MysScanResult result;
        try
        {
            result = await passport.QueryAsync(pending.Qr, ct).ConfigureAwait(false);
        }
        catch (MysLoginException e)
        {
            _binds.TryRemove(key, out _);
            return new MysBindOutcome(e.Message + "\n重新发「绑定米游社」再来一次。");
        }

        switch (result.State)
        {
            case MysQrState.WaitScan:
                return new MysBindOutcome(
                    "还没扫到。二维码"
                    + MysCookieBuilder.RemainText(pending.ExpiresAt, DateTimeOffset.UtcNow)
                    + "内有效，过期就再发「绑定米游社」。");

            case MysQrState.Scanned:
                return new MysBindOutcome("扫到了，在手机上点一下「确认登录」，然后再发「绑定米游社」。");

            case MysQrState.Expired:
                _binds.TryRemove(key, out _);
                return new MysBindOutcome("二维码过期了。再发「绑定米游社」，我给你换一张。");

            case MysQrState.Failed:
                _binds.TryRemove(key, out _);
                return new MysBindOutcome((result.Message ?? "扫码失败。") + "\n重新发「绑定米游社」再来一次。");

            case MysQrState.Confirmed:
                break;

            default:
                return new MysBindOutcome("扫码状态异常，重新发「绑定米游社」再来一次。");
        }

        var cookie = result.Cookie!;
        var roles = result.Roles ?? [];
        var config = configService.Get().RemoteTaskConfig;
        config.MysCookie = cookie;
        logger.LogInformation("米游社扫码绑定：登录态已写入配置，{Describe}（角色 {Count} 个）",
            MysCookieBuilder.Describe(cookie), roles.Count);

        if (roles.Count == 0)
        {
            config.MysUid = string.Empty;
            configService.Save();
            _binds.TryRemove(key, out _);
            return new MysBindOutcome(
                "登录态已经存好了，但米游社没返回这个账号下的原神角色。\n"
                + "在「设置 → 通用 → 远程任务」里把原神 UID 填上就能查树脂；也可以用「切号」回游戏主界面，我从画面上读。\n"
                + "如果填完 UID 仍提示未登录，说明这份登录态不完整，重发一次「绑定米游社」再扫。",
                Confirmed: true);
        }

        if (roles.Count == 1)
        {
            var role = roles[0];
            config.MysUid = role.Uid;
            configService.Save();
            _binds.TryRemove(key, out _);
            logger.LogInformation("米游社扫码绑定完成：UID {Uid}", role.Uid);

            // 立刻查一次便笺：绑定这步只证明「拿到了 Cookie」，能不能查树脂要看服务端认不认。
            // 不查的话用户要等到下次问体力才发现登录态是废的，中间那句「绑定好了」就是误导。
            var (note, error) = await VerifyAsync(cookie, role.Uid, ct).ConfigureAwait(false);
            return new MysBindOutcome(BuildSuccessText(role, note, error), Confirmed: true);
        }

        // 多个角色：先记住登录态，让用户回一个序号。
        // 这里必须把旧 UID 清掉：换了账号还留着上一个人的 UID，查出来的是别人的便笺。
        pending.Roles = roles;
        pending.RoleChoiceExpiresAt = DateTimeOffset.UtcNow + RoleChoiceLifetime;
        config.MysUid = string.Empty;
        configService.Save();
        return new MysBindOutcome(pending.BuildRoleChoiceText());
    }

    /// <summary>
    /// 多个角色时按序号选定一个，并立刻查一次便笺验证登录态。
    /// </summary>
    /// <param name="key">会话键。</param>
    /// <param name="index">从 1 开始的序号。</param>
    /// <param name="text">回复给用户的话。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<(bool Ok, string Text)> TrySelectRoleAsync(
        string key,
        int index,
        CancellationToken ct = default)
    {
        if (!TryGetLive(key, out var pending) || pending.Roles is not { Count: > 0 } roles)
        {
            return (false, string.Empty);
        }

        if (index < 1 || index > roles.Count)
        {
            return (true, $"序号超出范围，1 到 {roles.Count} 之间选一个。");
        }

        var role = roles[index - 1];
        var config = configService.Get().RemoteTaskConfig;
        config.MysUid = role.Uid;
        configService.Save();
        _binds.TryRemove(key, out _);
        logger.LogInformation("米游社扫码绑定完成：UID {Uid}", role.Uid);

        var (note, error) = await VerifyAsync(config.MysCookie, role.Uid, ct).ConfigureAwait(false);
        return (true, BuildSuccessText(role, note, error));
    }

    /// <summary>
    /// 取一个还没过期的会话，顺手清掉过期的。
    /// </summary>
    private bool TryGetLive(string key, out PendingBind pending)
    {
        pending = null!;
        if (!_binds.TryGetValue(key, out var found))
        {
            return false;
        }

        var deadline = found.Roles is { Count: > 0 } ? found.RoleChoiceExpiresAt : found.ExpiresAt;
        if (deadline <= DateTimeOffset.UtcNow)
        {
            _binds.TryRemove(key, out _);
            return false;
        }

        pending = found;
        return true;
    }

    /// <summary>
    /// 绑定成功后立刻查一次便笺，确认这份登录态在服务端真的能用。
    /// </summary>
    /// <param name="cookie">刚拿到的登录态。</param>
    /// <param name="uid">选中角色的 UID。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>读数；失败时同时给出原因。</returns>
    private async Task<(MysDailyNote? Note, string? Error)> VerifyAsync(
        string cookie,
        string uid,
        CancellationToken ct)
    {
        try
        {
            var note = await dailyNote.QueryAsync(cookie, uid, ct).ConfigureAwait(false);
            if (note != null)
            {
                logger.LogInformation("米游社绑定自检通过：树脂 {Current}/{Max}", note.CurrentResin, note.MaxResin);
                return (note, null);
            }

            var reason = dailyNote.LastError;
            logger.LogWarning("米游社绑定自检未通过：{Reason}", reason);
            return (null, reason);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "米游社绑定自检异常");
            return (null, "查询时出了点异常：" + e.Message);
        }
    }

    /// <summary>
    /// 绑定成功的回复：带上角色名和 UID，方便用户确认绑的是不是想绑的那个号。
    /// </summary>
    /// <param name="role">选中的角色。</param>
    /// <param name="note">自检读数；没查通时为 null。</param>
    /// <param name="error">自检失败的原因。</param>
    private static string BuildSuccessText(MysGameRole role, MysDailyNote? note, string? error)
    {
        var builder = new StringBuilder();
        builder.Append("绑定好了：").Append(role.Nickname)
            .Append("（UID ").Append(role.Uid)
            .Append('·').Append(role.ServerName);
        if (role.Level > 0)
        {
            builder.Append("·冒险等阶 ").Append(role.Level);
        }

        builder.Append("）\n");
        if (note != null)
        {
            // 真查通了才敢说「以后不开游戏也能问」；只拿到 Cookie 不等于能用
            builder.Append("刚试查了一次：现在树脂 ")
                .Append(note.CurrentResin).Append('/').Append(note.MaxResin)
                .Append("，浓缩 ").Append(note.CondensedResin).Append(" 个。\n")
                .Append("以后不开游戏也能问「还有多少树脂」。换号或登录过期，重新发「绑定米游社」覆盖就行。");
        }
        else
        {
            builder.Append("但登录态没能通过实测：").Append(error ?? "米游社没有返回数据").Append('\n')
                .Append("先重新发一次「绑定米游社」再扫；如果两次都一样，把上面这句话原样发我，我按 retcode 查。");
        }

        return builder.ToString();
    }

    /// <summary>
    /// 日志里只留会话键的尾部，避免整串 OpenID 进日志。
    /// </summary>
    private static string ShortKey(string key)
        => key.Length <= 6 ? key : "…" + key[^6..];

    /// <summary>
    /// 一次进行中的绑定。
    /// </summary>
    private sealed class PendingBind
    {
        /// <summary>扫码会话。</summary>
        public required MysQrSession Qr { get; init; }

        /// <summary>二维码过期时间。</summary>
        public required DateTimeOffset ExpiresAt { get; init; }

        /// <summary>扫码确认后拿到的角色列表，等用户选。</summary>
        public IReadOnlyList<MysGameRole>? Roles { get; set; }

        /// <summary>选角色的截止时间。</summary>
        public DateTimeOffset RoleChoiceExpiresAt { get; set; }

        /// <summary>
        /// 多角色时的选择提示。
        /// </summary>
        public string BuildRoleChoiceText()
        {
            var builder = new StringBuilder();
            builder.Append("这个米游社账号下有多个原神角色，回一个序号（例如「绑定米游社 2」）：\n");
            for (var i = 0; i < (Roles?.Count ?? 0); i++)
            {
                var role = Roles![i];
                builder.Append(i + 1).Append(". ").Append(role.Nickname)
                    .Append("（").Append(role.Uid).Append('·').Append(role.ServerName);
                if (role.Level > 0)
                {
                    builder.Append("·").Append(role.Level).Append(" 级");
                }

                builder.Append("）\n");
            }

            builder.Append("登录态已经存好了，选完就能查树脂。");
            return builder.ToString();
        }
    }
}
