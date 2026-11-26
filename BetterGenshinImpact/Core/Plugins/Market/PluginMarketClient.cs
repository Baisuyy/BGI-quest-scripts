using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Infrastructure.Network;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Core.Plugins.Market;

/// <summary>
/// 插件市场的网络侧：拉索引、下载安装包、校验哈希。
/// <para>
/// 这里只管「拿到一个可信的安装包文件」，装还是交给 <see cref="Hosting.PluginManager" />，
/// 和从本地文件安装走同一个入口——两套安装逻辑迟早会不一致。
/// </para>
/// </summary>
public sealed class PluginMarketClient
{
    /// <summary>
    /// 默认索引地址。改地址不需要重编译：插件页的「市场设置」写进 config.json 即可。
    /// </summary>
    public const string DefaultIndexUrl =
        "https://raw.githubusercontent.com/Baisuyy/BGI-quest-scripts/bgi-special/plugin-market/index.json";

    /// <summary>
    /// 单个安装包的大小上限。插件是托管的 .NET 程序集，正常只有几 MB。
    /// </summary>
    private const long MaxPackageBytes = 200L * 1024 * 1024;

    /// <summary>
    /// 下载超时：Download 客户端本身不设总时长，慢网下要自己兜住一个上界。
    /// </summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PluginMarketClient> _logger;

    /// <summary>
    /// 创建市场客户端。
    /// </summary>
    public PluginMarketClient(IHttpClientFactory httpClientFactory, ILogger<PluginMarketClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// 本机缓存下载包的目录，安装成功后由调用方删除。
    /// </summary>
    public static string DownloadDirectory => Path.Combine(Path.GetTempPath(), "BGI-PluginMarket");

    /// <summary>
    /// 读取索引。网络失败、格式错误都抛异常，由界面显示原因。
    /// </summary>
    public async Task<PluginMarketIndex> LoadIndexAsync(string indexUrl, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientNames.Default);
        using var response = await client.GetAsync(indexUrl, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"读取插件索引失败：HTTP {(int)response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var index = await JsonSerializer
            .DeserializeAsync<PluginMarketIndex>(stream, Global.ManifestJsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("插件索引是空文件");

        if (index.ManifestVersion > 1)
        {
            throw new InvalidDataException($"插件索引格式版本 {index.ManifestVersion} 高于本程序支持的 1，请更新 BetterGI");
        }

        var usable = index.Plugins.FindAll(p => p.IsUsable);
        if (usable.Count != index.Plugins.Count)
        {
            _logger.LogWarning("插件索引里有 {Count} 个条目缺少下载地址或校验值，已忽略",
                index.Plugins.Count - usable.Count);
        }

        index.Plugins = usable;
        _logger.LogInformation("插件索引已加载：{Count} 个插件", usable.Count);
        return index;
    }

    /// <summary>
    /// 下载安装包并校验 SHA-256，返回本地文件路径。校验不通过时删除文件并抛异常。
    /// </summary>
    public async Task<string> DownloadAsync(PluginMarketEntry entry, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (!entry.IsUsable)
        {
            throw new InvalidDataException($"插件“{entry.Name}”的索引条目缺少下载地址或校验值");
        }

        Directory.CreateDirectory(DownloadDirectory);
        // 文件名带上版本：同一插件新旧版本并存时互不覆盖，重试也能复用
        var target = Path.Combine(DownloadDirectory, $"{entry.Id}-{entry.Version}.pkg");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);

        var client = _httpClientFactory.CreateClient(HttpClientNames.Download);
        try
        {
            using var response = await client
                .GetAsync(entry.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"下载失败：HTTP {(int)response.StatusCode}");
            }

            var total = response.Content.Headers.ContentLength ?? (entry.Size > 0 ? entry.Size : 0);
            if (total > MaxPackageBytes)
            {
                throw new InvalidDataException($"安装包过大（{total / 1024d / 1024d:F0} MB），已中止");
            }

            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var destination = File.Create(target))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > MaxPackageBytes)
                    {
                        throw new InvalidDataException("安装包超过大小上限，已中止");
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    if (total > 0)
                    {
                        progress?.Report(Math.Min(1d, received / (double)total));
                    }
                }
            }

            await VerifyAsync(target, entry.Sha256, timeout.Token).ConfigureAwait(false);
            progress?.Report(1d);
            return target;
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    /// <summary>
    /// 校验文件哈希。插件以管理员权限运行，这一步是唯一的完整性防线。
    /// </summary>
    private static async Task VerifyAsync(string path, string expected, CancellationToken cancellationToken)
    {
        // 包只有几 MB，放在线程池里算就够了，不值得为它引一条异步哈希路径
        var actual = await Task.Run(() =>
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"安装包校验失败：期望 {expected[..Math.Min(12, expected.Length)]}…，实际 {actual[..12]}…。文件可能被篡改或下载不完整。");
        }
    }

    /// <summary>
    /// 删除下载缓存，失败不抛出——临时目录里的残留不影响使用。
    /// </summary>
    public static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
