using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
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
/// </summary>
public static class SpecialUpdater
{
    /// <summary>特别版发布所在的仓库。</summary>
    public const string Repo = "Baisuyy/BGI-quest-scripts";

    private const string ReleasesApi = "https://api.github.com/repos/" + Repo + "/releases?per_page=20";
    private const string ReleasesPage = "https://github.com/" + Repo + "/releases";
    private const string TagPrefix = "special-v";

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

        SpecialRelease? latest;
        try
        {
            var httpClient = httpClientFactory.CreateClient(HttpClientNames.Default);
            string json = await httpClient.GetStringAsync(ReleasesApi);
            var releases = JsonConvert.DeserializeObject<List<SpecialRelease>>(json);

            latest = releases?
                .Where(r => !string.IsNullOrWhiteSpace(r.TagName)
                            && r.TagName.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => ParseBuild(r.TagName))
                .FirstOrDefault();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "获取特别版发布信息失败");
            var retry = await ThemedMessageBox.QuestionAsync(
                $"获取特别版更新信息失败：{e.Message}\n\n是否直接打开 GitHub 发布页？", "检查更新", MessageBoxButton.YesNo);
            if (retry == MessageBoxResult.Yes)
            {
                OpenReleases();
            }

            return true;
        }

        if (latest == null)
        {
            await ThemedMessageBox.InformationAsync(
                $"当前版本：{current}\n\nGitHub 上没有找到特别版发布，请手动打开发布页查看。", "检查更新");
            OpenReleases();
            return true;
        }

        string latestVersion = latest.TagName[TagPrefix.Length..];

        if (string.Equals(latestVersion, current, StringComparison.OrdinalIgnoreCase))
        {
            await ThemedMessageBox.InformationAsync($"当前已是最新的特别版（{current}）。", "检查更新");
            return true;
        }

        var open = await ThemedMessageBox.QuestionAsync(
            $"特别版最新发布：{latestVersion}\n当前版本：{current}\n\n" +
            "特别版没有在线自动更新（官方更新通道会把客户端覆盖回官方版），需要下载安装包覆盖安装。\n现在打开下载页？",
            "发现新版本", MessageBoxButton.YesNo);

        if (open == MessageBoxResult.Yes)
        {
            OpenReleases();
        }

        return true;
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

    private static void OpenReleases()
    {
        Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true });
    }

    private sealed class SpecialRelease
    {
        [JsonProperty("tag_name")] public string TagName { get; set; } = "";
    }
}
