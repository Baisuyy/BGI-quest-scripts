using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.Runtime;
using BetterGenshinImpact.GameTask.SoloTask;
using BetterGenshinImpact.Pulonia.Models;
using BetterGenshinImpact.Pulonia.Services;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.GenshinAccountSwitch;

/// <summary>
/// 切换账号的参数。
/// </summary>
public class SwitchAccountParam
{
    /// <summary>
    /// 目标账户的名称或 UID。留空时只返回全部账户，不做切换
    /// </summary>
    public string Account { get; set; } = string.Empty;
}

/// <summary>
/// 切换到账户页里配置好的另一个账号。
/// <para>
/// 为什么要有这个任务：切号要退出登录、在登录界面找到目标账号、重新进游戏再核验 UID，
/// 让模型靠「点一下屏幕」是做不到的（登录界面全是图标和图片，OCR 一个名字也读不到）。
/// 这里复用 <see cref="IPuloniaGenshinAccountSwitcher"/> 已经调好的官方流程，
/// 于是「切到小号」就是一句话的事，不再依赖用户先写脚本。
/// </para>
/// <para>
/// 前提是账户已在「账号」页配好：完整 UID + 至少一种登录资料（图片 / 列表文字 / 密码）。
/// 缺哪样就明确报出来，不要让它变成「切了一半卡在登录界面」。
/// </para>
/// </summary>
[SoloTaskInfo(TaskId, "切换账号",
    Description = "切换到账户页里已配置的账号，切换后核验 UID。参数填账户名称或 UID。",
    Group = "工具", Order = 420, Scopes = SoloTaskScopes.All)]
public class SwitchAccountTask : ISoloTask<SwitchAccountParam, NoData>
{
    /// <summary>
    /// 稳定任务标识
    /// </summary>
    public const string TaskId = "switch_account";

    /// <summary>
    /// 找不到目标账户时的原因码
    /// </summary>
    public const string NotFoundReason = "account_not_found";

    /// <summary>
    /// 目标账户没配齐登录资料时的原因码
    /// </summary>
    public const string NotReadyReason = "account_not_ready";

    /// <summary>
    /// 切换过程失败时的原因码
    /// </summary>
    public const string SwitchFailedReason = "account_switch_failed";

    private static readonly ILogger Logger = App.GetLogger<SwitchAccountTask>();

    private readonly GameRuntime _runtime;
    private readonly AllConfig _config;
    private readonly IPuloniaAccountService _accounts;
    private readonly IPuloniaGenshinAccountSwitcher _switcher;

    /// <summary>
    /// 创建切换账号任务
    /// </summary>
    /// <param name="runtime">本次运行使用的运行环境</param>
    /// <param name="config">配置根</param>
    /// <param name="accounts">账户目录与当前账户</param>
    /// <param name="switcher">官方登录流程</param>
    public SwitchAccountTask(GameRuntime runtime, AllConfig config, IPuloniaAccountService accounts,
        IPuloniaGenshinAccountSwitcher switcher)
    {
        _runtime = runtime;
        _config = config;
        _accounts = accounts;
        _switcher = switcher;
    }

    /// <inheritdoc />
    public async Task<SoloTaskResult<NoData>> RunAsync(SwitchAccountParam param, CancellationToken ct)
    {
        var keyword = (param.Account ?? string.Empty).Trim();
        var settings = await _accounts.LoadAccountsAsync(ct).ConfigureAwait(false);
        var all = settings.Accounts;

        if (all.Count == 0)
        {
            return SoloTaskResult.Failed(NotFoundReason,
                "账户目录是空的。先到「账号」页添加账号，填好 UID 和登录资料。");
        }

        if (keyword.Length == 0)
        {
            return SoloTaskResult.Failed(NotFoundReason, "没说切到哪个账号。" + Describe(all));
        }

        var target = Match(all, keyword, out var candidates);
        if (target is null)
        {
            var reason = candidates.Count > 1
                ? $"「{keyword}」同时匹配到 {string.Join("、", candidates.Select(a => a.Name))}，请说完整名称或 UID。"
                : $"没有叫「{keyword}」的账号。";
            return SoloTaskResult.Failed(NotFoundReason, reason + Describe(all));
        }

        // 目标账户必须能自动登录：没有 UID 就无法核验，没有登录资料就走不到目标账号
        if (!Regex.IsMatch(target.Uid ?? string.Empty, "^[0-9]{9,10}$"))
        {
            return SoloTaskResult.Failed(NotReadyReason,
                $"账户「{target.Name}」没有填写完整 UID，不能自动切换。先到「账号」页补上 9–10 位 UID 再保存。");
        }

        if (!HasLoginSelection(target))
        {
            return SoloTaskResult.Failed(NotReadyReason,
                $"账户「{target.Name}」没有登录资料，不能自动切换。请在「账号」页为它采集或导入登录列表里的账号图片，或填写列表文字 / 密码。");
        }

        // 已经在目标账号上就不折腾：只截图读 UID，不按键，读不到也不影响继续判断
        if (await _switcher.TryReadUidAsync(_runtime, ct).ConfigureAwait(false) == target.Uid)
        {
            await _accounts.SetCurrentAccountAsync(_accounts.CurrentInstanceKey, target.Id, ct).ConfigureAwait(false);
            Logger.LogInformation("切换账号：已经在「{Name}」（UID {Uid}），无需切换", target.Name, target.Uid);
            return SoloTaskResult.Success($"已经在账户「{target.Name}」（UID {target.Uid}），没有做切换。");
        }

        // 同一账户同一时刻只能被一个运行占用，避免两个实例同时登录同一个号
        IDisposable? occupancy;
        PuloniaLockOwner? occupant;
        try
        {
            occupancy = _accounts.TryAcquireAccountOccupancy(target.Id, null, out occupant);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PuloniaTaskValidationException)
        {
            return SoloTaskResult.Failed(SwitchFailedReason, $"无法占用账户「{target.Name}」，未切换：{e.Message}");
        }

        if (occupancy is null)
        {
            return SoloTaskResult.Failed(SwitchFailedReason,
                $"账户「{target.Name}」正在{(occupant is null ? "另一个实例" : PuloniaAccountService.DescribeOccupant(occupant))}运行，未切换。先停掉那边再试。");
        }

        using var heldOccupancy = occupancy;
        Logger.LogInformation("切换账号：正在切换到「{Name}」（UID {Uid}）", target.Name, target.Uid);
        try
        {
            // 切换开始后游戏里登录的账号不再确定，失败时提示用户人工确认
            var uid = await _switcher.PrepareAsync(_runtime, target, static () => { }, ct).ConfigureAwait(false);
            await _accounts.SetCurrentAccountAsync(_accounts.CurrentInstanceKey, target.Id, ct).ConfigureAwait(false);
            Logger.LogInformation("切换账号：已切换到「{Name}」并核验 UID {Uid}", target.Name, uid);
            return SoloTaskResult.Success($"已切换到账户「{target.Name}」（UID {uid}），并核验通过。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Logger.LogError(e, "切换账号失败");
            return SoloTaskResult.Failed(SwitchFailedReason,
                $"切换到「{target.Name}」失败：{e.Message}。游戏里现在登录的账号可能已经变了，请先看一眼再决定下一步。");
        }
    }

    /// <summary>
    /// 在账户目录里按名称或 UID 找目标账户：先精确命中，再按唯一片段匹配；多个候选时不做猜测。
    /// </summary>
    /// <param name="all">账户目录</param>
    /// <param name="keyword">用户给的名称或 UID</param>
    /// <param name="candidates">片段匹配到的多个候选</param>
    /// <returns>唯一确定的目标账户；无法唯一确定时为 null</returns>
    private static PuloniaGenshinAccountProfile? Match(List<PuloniaGenshinAccountProfile> all, string keyword,
        out List<PuloniaGenshinAccountProfile> candidates)
    {
        candidates = [];
        var exact = all.FirstOrDefault(a => string.Equals(a.Name, keyword, StringComparison.OrdinalIgnoreCase))
                    ?? all.FirstOrDefault(a => string.Equals(a.Uid, keyword, StringComparison.Ordinal));
        if (exact is not null)
        {
            return exact;
        }

        candidates = all.Where(a =>
                a.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || (a.Uid ?? string.Empty).Contains(keyword, StringComparison.Ordinal))
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>
    /// 账户是否具备自动切换的条件：有完整 UID，且配置了至少一种登录资料。
    /// </summary>
    /// <param name="account">目标账户</param>
    public static bool IsReady(PuloniaGenshinAccountProfile account)
        => Regex.IsMatch(account.Uid ?? string.Empty, "^[0-9]{9,10}$") && HasLoginSelection(account);

    /// <summary>
    /// 账户是否配置了任意一种可用于自动登录的资料。
    /// </summary>
    /// <param name="account">目标账户</param>
    private static bool HasLoginSelection(PuloniaGenshinAccountProfile account) => account.LoginMode switch
    {
        PuloniaGenshinAccountLoginMode.Image => account.ImagePath is not null,
        PuloniaGenshinAccountLoginMode.ListText => !string.IsNullOrWhiteSpace(account.ListText),
        PuloniaGenshinAccountLoginMode.Password => account.CredentialReference is not null,
        PuloniaGenshinAccountLoginMode.Automatic => account.ImagePath is not null
                                                     || !string.IsNullOrWhiteSpace(account.ListText)
                                                     || account.CredentialReference is not null,
        _ => false
    };

    /// <summary>
    /// 把账户目录写成一句话并标出哪些账户还缺登录资料，方便模型直接挑。
    /// </summary>
    /// <param name="all">账户目录</param>
    private static string Describe(List<PuloniaGenshinAccountProfile> all)
    {
        var items = all.Select(a =>
        {
            var uid = string.IsNullOrWhiteSpace(a.Uid) ? "无 UID" : "UID " + a.Uid;
            var ready = IsReady(a) ? "" : "（未配齐登录资料）";
            return $"「{a.Name}」{uid}{ready}";
        });
        return "现有账号：" + string.Join("、", items) + "。";
    }
}
