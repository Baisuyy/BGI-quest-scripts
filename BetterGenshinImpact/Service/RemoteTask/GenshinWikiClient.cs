using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service.RemoteTask;

/// <summary>
/// 从公开的图鉴数据里查「角色 → 突破材料」。
/// <para>
/// 模型并不认识所有角色，尤其是刚上线的那几个：它可能把「米提亚」编成别的名字，
/// 也可能干脆说不知道。这种时候任何提示词都救不了，只能去查真实数据。
/// 这里走 Project Amber 的镜像接口（yatta.moe），只取角色名与突破材料名，不下载图片。
/// </para>
/// <para>
/// 网络不通、接口改版、被墙都会让这里返回空，调用方应当退回模型判断：
/// 查得到就用查到的，查不到不能把整条采集指令卡死。
/// </para>
/// </summary>
public sealed class GenshinWikiClient(HttpClient httpClient, ILogger<GenshinWikiClient> logger)
{
    /// <summary>
    /// 接口根地址。语言段固定 <c>chs</c>，路线目录用的也是简体中文名。
    /// </summary>
    private const string BaseUrl = "https://gi.yatta.moe/api/v2/chs";

    /// <summary>
    /// 本地缓存。角色列表与已查过的角色材料都放这里，避免每次聊天都去请求一遍。
    /// </summary>
    private static readonly string CachePath = Global.Absolute(@"User\RemoteTask\wiki-avatar.json");

    /// <summary>
    /// 角色列表的保鲜期。新角色上线后最多等一周就能查到。
    /// </summary>
    private static readonly TimeSpan ListLifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// 单个角色的材料基本不会变，缓存久一点。
    /// </summary>
    private static readonly TimeSpan DetailLifetime = TimeSpan.FromDays(30);

    /// <summary>
    /// 单次请求的等待上限。用户是在聊天里等回复，宁可不查也不能让消息卡住。
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// 失败后的冷却时间。站点挂了或被墙时，不要每条消息都白等 8 秒。
    /// </summary>
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// 串行化磁盘读写，避免并发指令把缓存文件写坏。
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static WikiCache? _cache;

    private static DateTime _blockedUntil = DateTime.MinValue;

    /// <summary>
    /// 按角色名（或角色名的一部分）查突破材料名。
    /// </summary>
    /// <param name="query">用户说的角色名，例如「米提亚」。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>材料名列表，顺序为突破阶段中首次出现的先后；查不到时返回空列表。</returns>
    public async Task<IReadOnlyList<string>> GetAscensionMaterialsAsync(
        string? query,
        CancellationToken cancellationToken)
    {
        var name = (query ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > 24)
        {
            return [];
        }

        if (DateTime.UtcNow < _blockedUntil)
        {
            return [];
        }

        try
        {
            var cache = await LoadAsync(cancellationToken).ConfigureAwait(false);

            // 先看缓存：查过的角色直接返回，不联网
            if (TryMatch(cache, name, out var cachedId, out var cachedMaterials)
                && cachedMaterials.Count > 0)
            {
                return cachedMaterials;
            }

            if (DateTime.UtcNow - cache.FetchedAt > ListLifetime)
            {
                await RefreshIdsAsync(cache, cancellationToken).ConfigureAwait(false);
            }

            if (!TryMatch(cache, name, out var id, out _))
            {
                // 列表刚刷新过还是找不到，说明确实没有这个角色
                return [];
            }

            var materials = await FetchMaterialsAsync(id, cancellationToken).ConfigureAwait(false);
            if (materials.Count == 0)
            {
                return [];
            }

            cache.Characters[GetCharacterKey(cache, id)] = new CharacterEntry
            {
                Id = id,
                Materials = materials,
                CachedAt = DateTime.UtcNow,
            };
            await SaveAsync(cache, cancellationToken).ConfigureAwait(false);

            logger.LogInformation("图鉴查到「{Name}」的突破材料：{Materials}", name, string.Join("、", materials));
            return materials;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 超时、DNS 失败、证书错误、JSON 改版都走这里：冷却一段时间，期间直接返回空
            _blockedUntil = DateTime.UtcNow + FailureCooldown;
            logger.LogInformation(exception, "图鉴查询失败，{Minutes} 分钟内不再尝试", FailureCooldown.TotalMinutes);
            return [];
        }
    }

    /// <summary>
    /// 在缓存里找角色。支持完全同名与包含关系（「米提亚」对「米提亚·XX」）。
    /// </summary>
    private static bool TryMatch(
        WikiCache cache,
        string query,
        out string id,
        out List<string> materials)
    {
        id = string.Empty;
        materials = [];

        foreach (var pair in cache.Ids)
        {
            if (!string.Equals(pair.Key, query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            id = pair.Value;
            break;
        }

        if (id.Length == 0)
        {
            // 用户常省略后缀，或者只记得前半段
            var partial = cache.Ids
                .Where(pair => pair.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                               || query.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToList();

            // 多于一个候选时不好猜，交给模型反问更稳
            if (partial.Count == 1)
            {
                id = cache.Ids[partial[0]];
            }
        }

        if (id.Length == 0)
        {
            return false;
        }

        if (cache.Characters.TryGetValue(id, out var entry)
            && entry.Materials.Count > 0
            && DateTime.UtcNow - entry.CachedAt < DetailLifetime)
        {
            materials = entry.Materials.ToList();
        }

        return true;
    }

    /// <summary>
    /// 缓存里按 id 存的键就是 id，这里统一取一次，避免各处拼错。
    /// </summary>
    private static string GetCharacterKey(WikiCache cache, string id)
    {
        return id;
    }

    /// <summary>
    /// 拉角色列表，刷新「名字 → id」。
    /// </summary>
    private async Task RefreshIdsAsync(WikiCache cache, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"{BaseUrl}/avatar", cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return;
        }

        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in items.EnumerateObject())
        {
            if (property.Value.TryGetProperty("name", out var nameNode)
                && nameNode.GetString() is { Length: > 0 } name)
            {
                ids[name] = property.Name;
            }
        }

        if (ids.Count == 0)
        {
            return;
        }

        cache.Ids = ids;
        cache.FetchedAt = DateTime.UtcNow;
        await SaveAsync(cache, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("图鉴角色列表已更新：{Count} 个角色", ids.Count);
    }

    /// <summary>
    /// 拉单个角色的详情，取出全部突破材料名。
    /// </summary>
    private async Task<List<string>> FetchMaterialsAsync(string id, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"{BaseUrl}/avatar/{id}", cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return [];
        }

        if (!document.RootElement.TryGetProperty("data", out var data))
        {
            return [];
        }

        // 素材 id 先换成名字，界面与路线目录用的都是中文名
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in items.EnumerateObject())
            {
                if (property.Value.TryGetProperty("name", out var nameNode)
                    && nameNode.GetString() is { Length: > 0 } name)
                {
                    names[property.Name] = name;
                }
            }
        }

        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Collect(JsonElement costItems)
        {
            if (costItems.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var cost in costItems.EnumerateObject())
            {
                if (!names.TryGetValue(cost.Name, out var material) || material.Length == 0)
                {
                    continue;
                }

                if (seen.Add(material))
                {
                    ordered.Add(material);
                }
            }
        }

        // 突破材料在 promote 的每个阶段重复出现，按阶段收一遍顺序就稳定了
        if (data.TryGetProperty("upgrade", out var upgrade)
            && upgrade.TryGetProperty("promote", out var promote)
            && promote.ValueKind == JsonValueKind.Array)
        {
            foreach (var stage in promote.EnumerateArray())
            {
                if (stage.TryGetProperty("costItems", out var costItems))
                {
                    Collect(costItems);
                }
            }
        }

        // 老接口或个别角色只有 ascension 汇总表，兜底再收一次
        if (ordered.Count == 0
            && data.TryGetProperty("ascension", out var ascension)
            && ascension.ValueKind == JsonValueKind.Object)
        {
            Collect(ascension);
        }

        // 摩拉永远不是采集物，留着只会让匹配多走一轮
        ordered.RemoveAll(name => name is "摩拉");
        return ordered;
    }

    /// <summary>
    /// 发一次 GET 并按 JSON 解析。失败返回 null，由调用方决定怎么降级。
    /// </summary>
    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(RequestTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "BetterGI-Special/1.0");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogInformation("图鉴接口返回 {Status}：{Url}", (int)response.StatusCode, url);
            return null;
        }

        var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var document = JsonDocument.Parse(text);
        if (document.RootElement.TryGetProperty("response", out var code)
            && code.TryGetInt32(out var value)
            && value != 200)
        {
            document.Dispose();
            return null;
        }

        return document;
    }

    /// <summary>
    /// 读缓存；文件不存在或损坏时给一份空缓存，不让坏数据阻断查询。
    /// </summary>
    private async Task<WikiCache> LoadAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache is not null)
            {
                return _cache;
            }

            if (File.Exists(CachePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(CachePath, cancellationToken).ConfigureAwait(false);
                    _cache = JsonSerializer.Deserialize<WikiCache>(json, Options);
                }
                catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
                {
                    logger.LogWarning(e, "图鉴缓存读取失败，按空缓存处理");
                }
            }

            _cache ??= new WikiCache();
            return _cache;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// 写缓存。失败只记日志：下次重新查一遍即可，不影响本次结果。
    /// </summary>
    private async Task SaveAsync(WikiCache cache, CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(CachePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(cache, Options);
            await File.WriteAllTextAsync(CachePath, json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "图鉴缓存写入失败");
        }
    }

    /// <summary>
    /// 缓存文件结构。
    /// </summary>
    private sealed class WikiCache
    {
        /// <summary>
        /// 角色列表刷新时间。
        /// </summary>
        public DateTime FetchedAt { get; set; } = DateTime.MinValue;

        /// <summary>
        /// 角色名 → 角色 id。
        /// </summary>
        public Dictionary<string, string> Ids { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 角色 id → 已查过的材料。
        /// </summary>
        public Dictionary<string, CharacterEntry> Characters { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// 单个角色的缓存项。
    /// </summary>
    private sealed class CharacterEntry
    {
        /// <summary>
        /// 角色 id。
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// 突破材料名。
        /// </summary>
        public List<string> Materials { get; set; } = [];

        /// <summary>
        /// 查询时间，用于判断是否过期。
        /// </summary>
        public DateTime CachedAt { get; set; } = DateTime.MinValue;
    }
}
