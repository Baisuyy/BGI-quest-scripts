using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Infrastructure.Network;
using BetterGenshinImpact.Infrastructure.Network.Download;
using BetterGenshinImpact.Model;
using BetterGenshinImpact.Service.Interface;
using BetterGenshinImpact.View.Windows;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace BetterGenshinImpact.Core.Special;

/// <summary>
///     特别版的更新检查。
///     <para>
///         官方的三条更新路径都指向官方包：稳定版读官方 OSS 的 notice.json、Alpha 读 Mirror 酱、
///         点「更新」拉起的是 Kachina 生成的 BetterGI.update.exe。特别版一旦走这些路径，
///         就会被整体覆盖回官方版，内置 AI 也随之消失。所以这里只读自己的 Release。
///     </para>
///     <para>
///         升级有两条路：有「上一版 → 这一版」的增量包时走热更新（只下变化的文件，退出后替换并重启），
///         没有站得住的增量包时才退回完整安装包覆盖。两条路都不会碰 <c>User</c> 目录。
///     </para>
///     <para>
///         GitHub 在国内经常连不上，所以 API 走「直连 → 代理镜像 → releases.atom」三级降级，
///         下载链接同时给两个镜像；全部失败时把可复制的地址给出来，而不是只说一句「检查失败」。
///     </para>
/// </summary>
public static class SpecialUpdater
{
    /// <summary>特别版发布所在的仓库。</summary>
    public const string Repo = "Baisuyy/BGI-quest-scripts";

    private const string ReleasesPage = "https://github.com/" + Repo + "/releases";
    private const string TagPrefix = "special-v";

    /// <summary>
    ///     JSON API。直连优先，其次 gh-proxy。
    ///     <para>
    ///         gh-proxy 对 api.github.com 有频率限制，连续查会 403，所以它只算第二道；
    ///         实测 api.kkgithub.com 已经连不上，不要再加回来。
    ///     </para>
    /// </summary>
    private static readonly string[] ApiEndpoints =
    [
        "https://api.github.com/repos/" + Repo + "/releases?per_page=20",
        "https://gh-proxy.com/https://api.github.com/repos/" + Repo + "/releases?per_page=20",
    ];

    /// <summary>
    ///     releases.atom 只有 18 KB，只含标题与 tag，不含资产名——但它是最后一个能出 tag 的渠道，
    ///     拿到 tag 后按发布流水线的命名规则拼出安装包地址，仍然能下载。
    /// </summary>
    private static readonly string[] AtomEndpoints =
    [
        "https://gh-proxy.com/https://github.com/" + Repo + "/releases.atom",
        "https://github.com/" + Repo + "/releases.atom",
    ];

    /// <summary>
    ///     下载镜像。实测这两个都能对 release 资产返回 206；
    ///     注意 ghproxy.net 代理 API 会 403，只能拿来下文件，不能拿它查列表。
    /// </summary>
    private static readonly string[] DownloadMirrors =
    [
        "https://gh-proxy.com/",
        "https://ghproxy.net/",
    ];

    private static readonly string[] DownloadPageMirrors =
    [
        ReleasesPage,
        "https://gh-proxy.com/" + ReleasesPage,
    ];

    /// <summary>
    ///     上游（官方 BetterGI）的版本清单。
    ///     <para>
    ///         与特别版自己的 Release 是两条独立渠道：它只说明「上游走到哪了」，
    ///         不代表特别版可以跟着升级——特别版的改动要重新合并到上游代码上。
    ///         这个 OSS 地址国内直连可用，正好补上 GitHub 连不上时的信息缺口。
    ///     </para>
    /// </summary>
    private const string UpstreamNoticeUrl = "https://hui-config.oss-cn-hangzhou.aliyuncs.com/bgi/notice.json";

    /// <summary>
    ///     记住「上游这个版本已经提醒过」，避免每次启动都弹。
    ///     存文件而不是存配置：配置文件里的字段会被上游同步脚本整段覆盖。
    /// </summary>
    private static string UpstreamStatePath => Global.Absolute(@"User\config\special-upstream.json");

    /// <summary>
    ///     检查上游是否已经走在特别版前面。是则提示（自动检查只提示一次，手动检查每次都说明）。
    ///     <para>
    ///         这里只提示不下载：上游的新版本是官方包，装上去特别版的能力（内置 AI、远程任务、
    ///         日志上报）会全部消失，和「特别版更新」是两件完全不同的事。
    ///     </para>
    /// </summary>
    internal static async Task NoticeUpstreamAsync(UpdateOption option, IHttpClientFactory httpClientFactory, ILogger logger)
    {
        var upstream = await FetchUpstreamVersionAsync(httpClientFactory, logger).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(upstream))
        {
            return;
        }

        string baseline = BaseVersion();
        if (CompareVersion(upstream, baseline) <= 0)
        {
            return;
        }

        bool manual = option.Trigger == UpdateTrigger.Manual;
        if (!manual && string.Equals(ReadNotifiedUpstream(), upstream, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await ThemedMessageBox.InformationAsync(
            $"上游 BetterGI 已经发布 {upstream}，特别版目前基于 {baseline}。\n\n" +
            "特别版不是改个版本号就能跟上的：上游这批改动要重新合并、重新回归，" +
            "适配完成前请以当前版本为准。\n\n" +
            "这不是「你的版本旧了」的提示，不用去下载官方包——装上去会丢掉特别版的功能。",
            "上游已有新版本");

        if (!manual)
        {
            WriteNotifiedUpstream(upstream);
        }
    }

    /// <summary>
    ///     读官方 notice.json 里的 version 字段。读不到返回 null，调用方按「不知道」处理。
    /// </summary>
    private static async Task<string?> FetchUpstreamVersionAsync(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var text = await client.GetStringAsync(UpstreamNoticeUrl, cts.Token).ConfigureAwait(true);

            using var document = JsonDocument.Parse(text);
            var upstream = document.RootElement.TryGetProperty("version", out var node) ? node.GetString() : null;
            return string.IsNullOrWhiteSpace(upstream) ? null : upstream;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "读取上游版本失败");
            return null;
        }
    }

    /// <summary>
    ///     去掉特别版后缀的主版本，例如 <c>0.67.0-special.7.7.2</c> → <c>0.67.0</c>。
    /// </summary>
    private static string BaseVersion()
    {
        var match = Regex.Match(CurrentVersion(), @"^\d+\.\d+\.\d+");
        return match.Success ? match.Value : CurrentVersion();
    }

    private static string? ReadNotifiedUpstream()
    {
        try
        {
            var path = UpstreamStatePath;
            if (!File.Exists(path))
            {
                return null;
            }

            var node = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
            return node.Value<string>("notifiedUpstream");
        }
        catch
        {
            return null;
        }
    }

    private static void WriteNotifiedUpstream(string version)
    {
        try
        {
            var path = UpstreamStatePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, new Newtonsoft.Json.Linq.JObject
            {
                ["notifiedUpstream"] = version,
                ["notifiedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            }.ToString());
        }
        catch
        {
            // 写不了状态文件最多是下次再弹一次，不值得打断流程
        }
    }

    /// <summary>
    ///     处理一次更新检查。特别版总是自行处理，返回 true 表示调用方不应再走官方逻辑。
    /// </summary>
    public static async Task<bool> TryHandleAsync(UpdateOption option, IHttpClientFactory httpClientFactory, ILogger logger)
    {
        // 启动时的自动检查也走这里，但只在新版本出现时弹一次窗：
        // 「连不上」「已是最新」这类结果对自动检查没有意义，静默跳过。
        bool manual = option.Trigger == UpdateTrigger.Manual;
        string current = CurrentVersion();

        var (releases, error) = await FetchReleasesAsync(httpClientFactory, logger).ConfigureAwait(false);
        if (releases == null)
        {
            if (!manual)
            {
                logger.LogInformation("启动检查特别版更新失败，本次跳过：{Error}", error);
                return true;
            }

            logger.LogWarning("获取特别版发布信息失败：{Error}", error);
            await ThemedMessageBox.InformationAsync(
                "检查更新失败：连不上 GitHub，镜像也没通。\n\n" +
                "可以稍后再试，或手动打开下面任一地址下载最新包：\n" +
                string.Join("\n", DownloadPageMirrors) +
                $"\n\n当前版本：{current}",
                "检查更新");
            return true;
        }

        var latest = releases
            .Where(r => !string.IsNullOrWhiteSpace(r.TagName)
                        && r.TagName.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => ParseVersion(r.TagName), StringComparer.Ordinal)
            .FirstOrDefault();

        if (latest == null)
        {
            if (!manual)
            {
                return true;
            }

            await ThemedMessageBox.InformationAsync(
                $"当前版本：{current}\n\nGitHub 上没有找到特别版发布，请手动打开发布页查看。\n" +
                string.Join("\n", DownloadPageMirrors), "检查更新");
            return true;
        }

        string latestVersion = latest.TagName[TagPrefix.Length..];

        // 只比“相等”会把降级也当成新版本：远端只剩更旧的包时，仍然弹「发现新版本」。
        // 这里按 (主.次.修订, special 尾号) 比较，远端不更新就直说已是最新。
        if (CompareVersion(latestVersion, current) <= 0)
        {
            if (manual)
            {
                // 手动检查是用户主动问「有没有新版」，把上游状态一起说清楚，
                // 省得他再去官网对一遍「官方更新了没」。
                var upstream = await FetchUpstreamVersionAsync(httpClientFactory, logger).ConfigureAwait(true);
                var upstreamHint = string.Empty;
                if (!string.IsNullOrWhiteSpace(upstream))
                {
                    upstreamHint = CompareVersion(upstream, BaseVersion()) > 0
                        ? $"\n\n上游 BetterGI 已到 {upstream}，特别版还在 {BaseVersion()} 上做适配。"
                        : $"\n\n上游 BetterGI 当前 {upstream}，两边是同步的。";
                }

                await ThemedMessageBox.InformationAsync(
                    $"当前已是最新的特别版（{current}）。{upstreamHint}", "检查更新");
            }

            return true;
        }

        // 自动检查时同一个版本只提醒一次：用户上次点了「稍后」或「关闭」，
        // 下次启动就不该再被同一个版本拦住，否则每次开机都弹。
        if (!manual)
        {
            string? notified = App.GetService<IConfigService>()?.Get()?.CommonConfig.LastNotifiedSpecialVersion;
            if (string.Equals(notified, latestVersion, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("特别版 {Version} 已提醒过，跳过本次弹窗", latestVersion);
                return true;
            }

            RememberNotified(latestVersion, logger);
            logger.LogInformation("发现特别版新版本 {Version}（当前 {Current}），弹出更新提示", latestVersion, current);
        }

        // 直接给安装包直链：发布页在大陆常常打不开，能让用户少走一步就少走一步
        string? assetUrl = PickAsset(latest);
        var urls = assetUrl is null
            ? Array.Empty<string>()
            : DownloadMirrors.Select(m => m + assetUrl).ToArray();

        // 自动检查只提醒一次，弹窗里写明，免得用户以为每次开机都会被拦
        string onceHint = manual ? "" : "\n\n本次只提醒一次，选「否」后不再提示这个版本。";

        // 增量包优先：完整包 546 MB，走镜像常只有几百 KB/s，一次升级要等十几分钟；
        // 而一次发版真正变的通常只有主程序 DLL 与前端产物，增量包往往只有几十 MB。
        // 这一段就是用户口中的「热更新」：下载完由脚本在进程退出后替换文件再拉起新版。
        bool offeredPatch = false;
        if (FindPatch(latest, current) is { } patch)
        {
            offeredPatch = true;
            string sizeText = patch.Size > 0 ? $"（约 {patch.Size / 1024d / 1024d:0.0} MB）" : "";
            var go = await ThemedMessageBox.QuestionAsync(
                $"特别版最新发布：{latestVersion}\n当前版本：{current}\n\n" +
                $"可以热更新：只下载变化的文件{sizeText}，退出后自动替换并重启，不用重新下 546 MB 完整包。\n" +
                "替换的是程序自己的文件，User 目录（配置、脚本、截图）原样保留。\n\n" +
                "现在更新？（选「否」则给出完整包链接，自行覆盖安装）" + onceHint,
                "发现新版本", MessageBoxButton.YesNo);

            if (go == MessageBoxResult.Yes)
            {
                bool done = await ApplyPatchAsync(patch, latestVersion, httpClientFactory, logger).ConfigureAwait(true);
                if (done)
                {
                    return true;
                }
            }
        }

        string hint = urls.Length > 0
            ? "安装包地址（任选一条，打不开就换下一条）：\n" + string.Join("\n", urls)
            : "现在打开下载页？\n\n" + string.Join("\n", DownloadPageMirrors);

        // 走到这里有两种情况：没有对应增量包，或者用户不想热更新。
        // 原文案写「特别版没有在线自动更新」，在支持增量以后已经不准确，
        // 也容易让人以为每次升级都得重新下 546 MB。
        string reason = offeredPatch
            ? "也可以按老办法，下载完整安装包覆盖安装。"
            : $"这一版没有放出「{current} → {latestVersion}」的增量包（增量包只做相邻版本），只能下载完整安装包覆盖安装。";

        var open = await ThemedMessageBox.QuestionAsync(
            $"特别版最新发布：{latestVersion}\n当前版本：{current}\n\n" +
            reason + "\n覆盖安装不会动 User 目录，配置、脚本、截图都保留。\n\n" + hint + onceHint,
            "发现新版本", MessageBoxButton.YesNo);

        if (open == MessageBoxResult.Yes)
        {
            Open(urls.Length > 0 ? urls[0] : DownloadPageMirrors[^1]);
        }

        return true;
    }

    /// <summary>
    ///     一次不带界面的更新检查结果，给远程指令用。
    /// </summary>
    /// <param name="Reachable">是否成功读到发布列表；false 时 <paramref name="Message"/> 就是失败原因。</param>
    /// <param name="Message">可以直接发给用户的一句话。</param>
    /// <param name="CurrentVersion">本机当前版本。</param>
    /// <param name="LatestVersion">远端最新版本；没有比本机更新的版本时为 null。</param>
    /// <param name="Patch">可套用的增量包；这一版没放出时为 null。</param>
    internal sealed record SpecialUpdateCheck(
        bool Reachable,
        string Message,
        string CurrentVersion,
        string? LatestVersion,
        SpecialAsset? Patch);

    /// <summary>
    ///     不带界面的更新检查，供 QQ 那边复用。
    ///     <para>
    ///         菜单里的「检查更新」会弹窗、会开进度窗口；远程指令没有人守着桌面，
    ///         所以另走一条只吐文本的路径：这里只判断，不下载、不弹窗、不写已提醒版本。
    ///     </para>
    /// </summary>
    internal static async Task<SpecialUpdateCheck> CheckAsync(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        string current = CurrentVersion();
        var (releases, error) = await FetchReleasesAsync(httpClientFactory, logger).ConfigureAwait(false);
        if (releases == null)
        {
            return new SpecialUpdateCheck(false, $"连不上 GitHub，也没通镜像：{error}", current, null, null);
        }

        var latest = releases
            .Where(r => !string.IsNullOrWhiteSpace(r.TagName)
                        && r.TagName.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => ParseVersion(r.TagName), StringComparer.Ordinal)
            .FirstOrDefault();
        if (latest == null)
        {
            return new SpecialUpdateCheck(false, "GitHub 上没有找到特别版发布。", current, null, null);
        }

        string latestVersion = latest.TagName[TagPrefix.Length..];
        if (CompareVersion(latestVersion, current) <= 0)
        {
            return new SpecialUpdateCheck(true, $"已经是最新的特别版（{current}）。", current, null, null);
        }

        var patch = FindPatch(latest, current);
        string size = patch is { Size: > 0 } asset ? $"（{asset.Size / 1024d / 1024d:0.0} MB）" : string.Empty;
        string message = patch == null
            ? $"有新版 {latestVersion}（当前 {current}），但这一版没有放出 {current} → {latestVersion} 的增量包。"
            : $"有新版 {latestVersion}（当前 {current}），可以热更新{size}。";

        return new SpecialUpdateCheck(true, message, current, latestVersion, patch);
    }

    /// <summary>
    ///     下载增量包并解压校验，全程不弹窗——远程指令的调用方自己把结果说给用户听。
    ///     <para>成功后由 <see cref="SpecialPatch.StartApplyAndRestart"/> 在进程退出时替换文件。</para>
    /// </summary>
    internal static async Task<(bool Ok, string Message)> DownloadPatchAsync(
        SpecialAsset patch, string version, ILogger logger)
    {
        var download = App.GetService<IDownloadService>();
        if (download is null)
        {
            return (false, "下载服务不可用，没法热更新。");
        }

        string target = Path.Combine(SpecialPatch.Root, "patch.zip");
        try
        {
            Directory.CreateDirectory(SpecialPatch.Root);

            // 上一次同样大小的包还在，就不重复下载：断线重连后重发指令很常见
            if (File.Exists(target) && patch.Size > 0 && new FileInfo(target).Length == patch.Size)
            {
                logger.LogInformation("复用已下载的增量包");
            }
            else
            {
                var mirrors = DownloadMirrors
                    .Select(m => new Uri(m + patch.Url))
                    .Where(u => u.IsAbsoluteUri)
                    .ToList();

                var task = download.Start(new DownloadRequest(
                    new Uri(patch.Url), $"特别版 {version} 增量包", DownloadCategories.SpecialUpdate)
                {
                    DestinationPath = target,
                    ExpectedSize = patch.Size > 0 ? patch.Size : null,
                    MaxBytes = 2L * 1024 * 1024 * 1024,
                    Mirrors = mirrors,
                    Origin = "远程更新",
                });

                target = await task.Completion.ConfigureAwait(false);
            }

            var (ok, message) = await SpecialPatch.StageAsync(target, logger).ConfigureAwait(false);
            if (!ok)
            {
                logger.LogWarning("远程增量更新准备失败：{Message}", message);
                return (false, message);
            }

            return (true, message);
        }
        catch (Exception e)
        {
            logger.LogError(e, "下载特别版增量包失败");
            return (false, $"下载失败：{e.Message}");
        }
    }

    /// <summary>
    ///     记下已经提醒过的版本，避免每次启动都弹同一个版本。
    ///     <para>弹窗前就写盘：即使用户直接关掉对话框，下次也不再重复打扰。</para>
    /// </summary>
    private static void RememberNotified(string version, ILogger logger)
    {
        try
        {
            var configService = App.GetService<IConfigService>();
            if (configService?.Get() is not { } config)
            {
                return;
            }

            config.CommonConfig.LastNotifiedSpecialVersion = version;
            configService.Save();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "记录已提醒的特别版版本失败");
        }
    }

    /// <summary>
    ///     在当前发布的资产里找匹配本机版本的增量包。找不到就返回 null，调用方退回完整包。
    /// </summary>
    private static SpecialAsset? FindPatch(SpecialRelease release, string current)
    {
        if (release.Assets is not { Count: > 0 })
        {
            return null;
        }

        return release.Assets.FirstOrDefault(a =>
            string.Equals(SpecialPatch.FromVersionOf(a.Name), current, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     下载增量包并解压校验，然后询问是否立即重启完成安装。
    ///     <para>返回 false 表示没能装上（下载失败、校验失败、解压失败），调用方应退回完整包链接。</para>
    /// </summary>
    private static async Task<bool> ApplyPatchAsync(
        SpecialAsset patch, string version, IHttpClientFactory httpClientFactory, ILogger logger)
    {
        var download = App.GetService<IDownloadService>();
        string target = Path.Combine(SpecialPatch.Root, "patch.zip");

        try
        {
            Directory.CreateDirectory(SpecialPatch.Root);

            // 已下载过同名包且大小对得上时直接复用，避免失败重试又下一遍
            if (File.Exists(target) && patch.Size > 0 && new FileInfo(target).Length == patch.Size)
            {
                logger.LogInformation("复用已下载的增量包");
            }
            else
            {
                if (download is null)
                {
                    logger.LogWarning("下载服务不可用，无法增量更新");
                    return false;
                }

                // 主地址直连 GitHub，失败自动换镜像；三个镜像都挂时还有断点续传
                var mirrors = DownloadMirrors
                    .Select(m => new Uri(m + patch.Url))
                    .Where(u => u.IsAbsoluteUri)
                    .ToList();

                var task = download.Start(new DownloadRequest(
                    new Uri(patch.Url), $"特别版 {version} 增量包", DownloadCategories.SpecialUpdate)
                {
                    DestinationPath = target,
                    ExpectedSize = patch.Size > 0 ? patch.Size : null,
                    MaxBytes = 2L * 1024 * 1024 * 1024,
                    Mirrors = mirrors,
                    Origin = "检查更新",
                });

                var window = new SpecialUpdateProgressWindow($"正在下载特别版 {version} 增量包");
                task.ProgressChanged += (_, p) => window.Report(p);
                window.Show();
                try
                {
                    target = await task.Completion.ConfigureAwait(true);
                }
                finally
                {
                    window.Close();
                }
            }

            var stage = new SpecialUpdateProgressWindow("正在解压并校验更新");
            stage.Show();
            (bool ok, string message) result;
            try
            {
                result = await SpecialPatch.StageAsync(target, logger).ConfigureAwait(true);
            }
            finally
            {
                stage.Close();
            }

            if (!result.ok)
            {
                logger.LogWarning("增量更新准备失败：{Message}", result.message);
                await ThemedMessageBox.WarningAsync(result.message + "\n\n下面给出完整包地址，可手动覆盖安装。", "增量更新失败");
                return false;
            }

            var restart = await ThemedMessageBox.QuestionAsync(
                result.message + "\n\n" +
                "需要重启 BetterGI 才能生效。现在重启？（会先关闭当前窗口，几秒后自动打开）",
                "更新已就绪", MessageBoxButton.YesNo);

            if (restart == MessageBoxResult.Yes)
            {
                if (SpecialPatch.StartApplyAndRestart(logger))
                {
                    App.GetService<IConfigService>()?.Save();
                    Application.Current.Shutdown();
                    return true;
                }

                await ThemedMessageBox.WarningAsync(
                    "无法启动更新脚本，可能被安全软件拦截。更新文件已解压到 update\\staging，可手动覆盖。", "更新失败");
                return false;
            }

            await ThemedMessageBox.InformationAsync(
                "更新已下载并校验完成，下次启动 BetterGI 时会自动应用（只换程序文件，User 目录里的配置与脚本不动）。", "更新已就绪");
            return true;
        }
        catch (Exception e)
        {
            logger.LogError(e, "增量更新失败");
            await ThemedMessageBox.WarningAsync($"增量更新失败：{e.Message}\n\n下面给出完整包地址。", "增量更新失败");
            return false;
        }
    }

    /// <summary>
    ///     依次尝试直连 API、gh-proxy 的 API，再退到 releases.atom。
    ///     全部失败返回 null 与最后一次的错误，调用方据此提示。
    /// </summary>
    private static async Task<(List<SpecialRelease>? Releases, string Error)> FetchReleasesAsync(
        IHttpClientFactory httpClientFactory, ILogger logger)
    {
        string lastError = "未知错误";
        var httpClient = httpClientFactory.CreateClient(HttpClientNames.Default);

        foreach (var endpoint in ApiEndpoints)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                string json = await httpClient.GetStringAsync(endpoint, cts.Token).ConfigureAwait(false);
                var releases = JsonConvert.DeserializeObject<List<SpecialRelease>>(json);
                if (releases is { Count: > 0 })
                {
                    logger.LogDebug("特别版更新信息来自 {Endpoint}", endpoint);
                    return (releases, string.Empty);
                }

                lastError = "返回内容为空";
            }
            catch (Exception e)
            {
                lastError = e.Message;
                logger.LogDebug(e, "更新源不可用：{Endpoint}", endpoint);
            }
        }

        foreach (var endpoint in AtomEndpoints)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                string xml = await httpClient.GetStringAsync(endpoint, cts.Token).ConfigureAwait(false);
                var releases = ParseAtom(xml);
                if (releases is { Count: > 0 })
                {
                    logger.LogInformation("特别版更新信息来自 atom（不含资产名，按命名规则拼下载地址）");
                    return (releases, string.Empty);
                }

                lastError = "atom 里没有特别版条目";
            }
            catch (Exception e)
            {
                lastError = e.Message;
                logger.LogDebug(e, "atom 源不可用：{Endpoint}", endpoint);
            }
        }

        return (null, lastError);
    }

    /// <summary>
    ///     从 releases.atom 里抠 tag。只用到 link 里的 tag 名，不解析整套 XML。
    ///     <para>
    ///         资产名拿不到，就按发布流水线的命名规则补上：
    ///         BetterGI.Install.{版本}.exe（Kachina 安装器）与 BetterGI_v{版本}_special-win-x64.7z。
    ///         这两条名字由 build-special.yml 固定生成，改流水线时这里要一起改。
    ///     </para>
    /// </summary>
    private static List<SpecialRelease>? ParseAtom(string xml)
    {
        var tags = Regex.Matches(xml, @"releases/tag/([^""'<>\s]+)")
            .Select(m => m.Groups[1].Value)
            .Where(t => t.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();
        if (tags.Count == 0)
        {
            return null;
        }

        return tags.Select(tag =>
        {
            string version = tag[TagPrefix.Length..];
            string root = $"https://github.com/{Repo}/releases/download/{tag}/";
            string setup = $"BetterGI.Install.{version}.exe";
            string archive = $"BetterGI_v{version}_special-win-x64.7z";
            return new SpecialRelease
            {
                TagName = tag,
                Assets =
                [
                    new SpecialAsset { Name = setup, Url = root + setup },
                    new SpecialAsset { Name = archive, Url = root + archive },
                ],
            };
        }).ToList();
    }

    /// <summary>优先 Kachina 安装器（Install.exe），其次旧的自解压包（Setup.exe），再次 7z 压缩包。</summary>
    private static string? PickAsset(SpecialRelease release)
    {
        var assets = release.Assets;
        if (assets == null || assets.Count == 0)
        {
            return null;
        }

        // Install 与 setup 都可能是安装器；先挑名字里带 install 的，旧版本只有 setup 时再退回
        return assets.FirstOrDefault(a => a.Name.Contains("install", StringComparison.OrdinalIgnoreCase)
                                          && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))?.Url
               ?? assets.FirstOrDefault(a => a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase)
                                             && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))?.Url
               ?? assets.FirstOrDefault(a => a.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))?.Url
               ?? assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))?.Url;
    }

    /// <summary>当前版本号，去掉信息版本里附带的 +提交号。</summary>
    private static string CurrentVersion()
    {
        string version = Global.Version ?? "未知";
        int plus = version.IndexOf('+');
        return plus > 0 ? version[..plus] : version;
    }

    /// <summary>
    ///     把 <c>0.67.0-special.7.2</c> 编成可直接按字符串比较的定长键，例如
    ///     <c>00000.00067.00000.00007.00002</c>。每段补足 5 位，所以字典序就是版本序。
    ///     取不到主版本时返回空串，空串小于任何键，这样“未知版本”永远算旧，
    ///     用户点检查更新能拿到提示；同时 <c>special.7</c> 与 <c>special.7.1</c>
    ///     也能分出先后（前者是后者的前缀，比较时为小）。
    /// </summary>
    private static string ParseVersion(string text)
    {
        text ??= string.Empty;
        var m = Regex.Match(text, @"(\d+)\.(\d+)\.(\d+)");
        if (!m.Success)
        {
            return string.Empty;
        }

        var parts = new List<int>
        {
            int.Parse(m.Groups[1].Value),
            int.Parse(m.Groups[2].Value),
            int.Parse(m.Groups[3].Value)
        };

        // special 后面的尾号可能有多段：special.7 / special.7.1 / special.7.2
        var tail = Regex.Match(text, @"special\.(\d+(?:\.\d+)*)", RegexOptions.IgnoreCase);
        if (tail.Success)
        {
            foreach (string seg in tail.Groups[1].Value.Split('.'))
            {
                parts.Add(int.Parse(seg));
            }
        }

        return string.Join('.', parts.Select(p => p.ToString("D5")));
    }

    private static int CompareVersion(string left, string right)
    {
        return string.CompareOrdinal(ParseVersion(left), ParseVersion(right));
    }

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }
    }

    // 这两个类型原来只在弹窗路径里用，远程指令要拿到增量包信息，所以放到 internal
    internal sealed class SpecialRelease
    {
        [JsonProperty("tag_name")] public string TagName { get; set; } = "";

        [JsonProperty("assets")] public List<SpecialAsset>? Assets { get; set; }
    }

    internal sealed class SpecialAsset
    {
        [JsonProperty("name")] public string Name { get; set; } = "";

        [JsonProperty("browser_download_url")] public string Url { get; set; } = "";

        /// <summary>资产字节数，atom 兜底时拿不到，按 0 处理。</summary>
        [JsonProperty("size")] public long Size { get; set; }
    }
}
