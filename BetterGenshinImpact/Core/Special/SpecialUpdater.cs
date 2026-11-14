using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
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
///         GitHub 在国内经常连不上，所以 API 与下载都走「直连 → 代理镜像」的降级链，
///         并在全部失败时把地址给出来让用户自己贴到浏览器，而不是只说一句「检查失败」。
///     </para>
/// </summary>
public static class SpecialUpdater
{
    /// <summary>特别版发布所在的仓库。</summary>
    public const string Repo = "Baisuyy/BGI-quest-scripts";

    private const string ReleasesPage = "https://github.com/" + Repo + "/releases";
    private const string TagPrefix = "special-v";

    /// <summary>能直连就走直连，不通再走这两个常见镜像。顺序即优先级。</summary>
    private static readonly string[] ApiEndpoints =
    [
        "https://api.github.com/repos/" + Repo + "/releases?per_page=20",
        "https://gh-proxy.com/https://api.github.com/repos/" + Repo + "/releases?per_page=20",
        "https://api.kkgithub.com/repos/" + Repo + "/releases?per_page=20",
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
            .OrderByDescending(r => ParseBuild(r.TagName))
            .FirstOrDefault();

        if (latest == null)
        {
            await ThemedMessageBox.InformationAsync(
                $"当前版本：{current}\n\nGitHub 上没有找到特别版发布，请手动打开发布页查看。\n" +
                string.Join("\n", DownloadPageMirrors), "检查更新");
            return true;
        }

        string latestVersion = latest.TagName[TagPrefix.Length..];

        if (string.Equals(latestVersion, current, StringComparison.OrdinalIgnoreCase))
        {
            await ThemedMessageBox.InformationAsync($"当前已是最新的特别版（{current}）。", "检查更新");
            return true;
        }

        // 直接给安装包直链：发布页在大陆常常打不开，能让用户少走一步就少走一步
        string? assetUrl = PickAsset(latest);
        string downloadUrl = assetUrl is null ? string.Empty : Mirror(assetUrl);
        string hint = downloadUrl.Length > 0
            ? "现在下载安装包？\n\n" + downloadUrl
            : "现在打开下载页？\n\n" + string.Join("\n", DownloadPageMirrors);

        var open = await ThemedMessageBox.QuestionAsync(
            $"特别版最新发布：{latestVersion}\n当前版本：{current}\n\n" +
            "特别版没有在线自动更新（官方更新通道会把客户端覆盖回官方版），需要下载安装包覆盖安装。\n\n" + hint,
            "发现新版本", MessageBoxButton.YesNo);

        if (open == MessageBoxResult.Yes)
        {
            Open(downloadUrl.Length > 0 ? downloadUrl : DownloadPageMirrors[^1]);
        }

        return true;
    }

    /// <summary>
    ///     依次尝试直连与镜像。全部失败返回 null 与最后一次的错误，调用方据此提示。
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

        return (null, lastError);
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

    /// <summary>GitHub 原始下载链接转成可用镜像；已经是镜像的不重复加前缀。</summary>
    private static string Mirror(string url)
    {
        return url.Contains("gh-proxy.com", StringComparison.OrdinalIgnoreCase)
            ? url
            : "https://gh-proxy.com/" + url;
    }

    /// <summary>当前版本号，去掉信息版本里附带的 +提交号。</summary>
    private static string CurrentVersion()
    {
        string version = Global.Version ?? "未知";
        int plus = version.IndexOf('+');
        return plus > 0 ? version[..plus] : version;
    }

    /// <summary>把 special.6 之类的尾号取出来用于排序，取不到就当 0。</summary>
    private static int ParseBuild(string tag)
    {
        int dot = tag.LastIndexOf('.');
        return dot >= 0 && int.TryParse(tag[(dot + 1)..], out int build) ? build : 0;
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
