using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Infrastructure.Network;
using BetterGenshinImpact.Model;
using BetterGenshinImpact.View.Windows;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace BetterGenshinImpact.Core.Special;

/// <summary>
///     特别版的更新检查。
///     <para>
///         官方的三条更新路径都指向官方包：稳定版读官方 OSS 的 notice.json、Alpha 读 Mirror 酱、
///         点「更新」拉起的是 Kachina 生成的 BetterGI.update.exe。特别版一旦走这些路径，
///         就会被整体覆盖回官方版，内置 AI 也随之消失。所以这里只读自己的 Release，
///         手动检查、给出提示、跳转下载页，不做任何自动替换。
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
    ///     处理一次更新检查。特别版总是自行处理，返回 true 表示调用方不应再走官方逻辑。
    /// </summary>
    public static async Task<bool> TryHandleAsync(UpdateOption option, IHttpClientFactory httpClientFactory, ILogger logger)
    {
        // 启动时的自动检查直接跳过：特别版不弹更新提醒，避免每次开机都打扰
        if (option.Trigger != UpdateTrigger.Manual)
        {
            return true;
        }

        string current = CurrentVersion();

        var (releases, error) = await FetchReleasesAsync(httpClientFactory, logger).ConfigureAwait(false);
        if (releases == null)
        {
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
            .OrderByDescending(r => ParseVersion(r.TagName))
            .FirstOrDefault();

        if (latest == null)
        {
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
            await ThemedMessageBox.InformationAsync($"当前已是最新的特别版（{current}）。", "检查更新");
            return true;
        }

        // 直接给安装包直链：发布页在大陆常常打不开，能让用户少走一步就少走一步
        string? assetUrl = PickAsset(latest);
        var urls = assetUrl is null
            ? Array.Empty<string>()
            : DownloadMirrors.Select(m => m + assetUrl).ToArray();

        string hint = urls.Length > 0
            ? "安装包地址（任选一条，打不开就换下一条）：\n" + string.Join("\n", urls)
            : "现在打开下载页？\n\n" + string.Join("\n", DownloadPageMirrors);

        var open = await ThemedMessageBox.QuestionAsync(
            $"特别版最新发布：{latestVersion}\n当前版本：{current}\n\n" +
            "特别版没有在线自动更新（官方更新通道会把客户端覆盖回官方版），需要下载安装包覆盖安装。\n\n" + hint,
            "发现新版本", MessageBoxButton.YesNo);

        if (open == MessageBoxResult.Yes)
        {
            Open(urls.Length > 0 ? urls[0] : DownloadPageMirrors[^1]);
        }

        return true;
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
    ///         BetterGI_v{版本}_Setup.exe 与 BetterGI_v{版本}_special-win-x64.7z。
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
            string setup = $"BetterGI_v{version}_Setup.exe";
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

    /// <summary>优先安装包（setup.exe），其次 7z 压缩包。</summary>
    private static string? PickAsset(SpecialRelease release)
    {
        var assets = release.Assets;
        if (assets == null || assets.Count == 0)
        {
            return null;
        }

        return assets.FirstOrDefault(a => a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase)
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
    ///     把 0.67.0-special.5 拆成可比较的四段。取不到主版本时返回 (-1, -1, -1, 0)，
    ///     这样“未知版本”永远算旧，用户点检查更新能拿到提示。
    /// </summary>
    private static (int Major, int Minor, int Patch, int Build) ParseVersion(string text)
    {
        text ??= string.Empty;
        var m = Regex.Match(text, @"(\d+)\.(\d+)\.(\d+)");
        if (!m.Success)
        {
            return (-1, -1, -1, 0);
        }

        int build = 0;
        var b = Regex.Match(text, @"special\.(\d+)");
        if (b.Success)
        {
            build = int.Parse(b.Groups[1].Value);
        }

        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), build);
    }

    private static int CompareVersion(string left, string right)
    {
        return ParseVersion(left).CompareTo(ParseVersion(right));
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

    private sealed class SpecialRelease
    {
        [JsonProperty("tag_name")] public string TagName { get; set; } = "";

        [JsonProperty("assets")] public List<SpecialAsset>? Assets { get; set; }
    }

    private sealed class SpecialAsset
    {
        [JsonProperty("name")] public string Name { get; set; } = "";

        [JsonProperty("browser_download_url")] public string Url { get; set; } = "";
    }
}
