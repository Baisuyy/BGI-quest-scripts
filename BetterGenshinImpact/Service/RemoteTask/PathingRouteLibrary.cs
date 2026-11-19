using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterGenshinImpact.ViewModel.Pages;

namespace BetterGenshinImpact.Service.RemoteTask;

/// <summary>
/// 一条地图追踪路线。
/// </summary>
/// <param name="Relative">相对 <c>User\AutoPathing</c> 的路径，运行任务时原样传回去。</param>
/// <param name="Region">一级目录，通常是地区名，如「蒙德」。</param>
/// <param name="Material">二级目录，通常是采集物名，如「清心」。</param>
/// <param name="Name">文件名（不含扩展名），如「清心-1」。</param>
public sealed record PathingRoute(string Relative, string Region, string Material, string Name)
{
    /// <summary>
    /// 给人看的一行描述。
    /// </summary>
    public string Describe() => string.IsNullOrEmpty(Material) ? $"{Relative}" : $"{Material}（{Region}）{Name}";
}

/// <summary>
/// 本地地图追踪路线的索引。
/// <para>
/// 路线放在 <c>User\AutoPathing\&lt;地区&gt;\&lt;采集物&gt;\xxx.json</c>，素材名就在目录上。
/// 这里只扫目录、不解析 JSON：一个素材动辄几十条路线，解析一遍要好几秒，
/// 而目录名已经够回答「有没有清心的路线」了。
/// </para>
/// </summary>
public static class PathingRouteLibrary
{
    /// <summary>
    /// 索引缓存，避免每条指令都重扫一次目录。
    /// </summary>
    private static readonly object Gate = new();

    private static IReadOnlyList<PathingRoute> _routes = [];

    /// <summary>
    /// 素材名 → 路线。目录名就是素材名，按它建索引后最常见的查法不用再扫全表。
    /// </summary>
    private static Dictionary<string, List<PathingRoute>> _byMaterial =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 素材名去重排序后的缓存，提示词和帮助文案每次都要用。
    /// </summary>
    private static IReadOnlyList<string> _materials = [];

    private static DateTime _loadedAt = DateTime.MinValue;

    /// <summary>
    /// 缓存有效期。用户下载新路线后最多等这么久就能看到。
    /// </summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 当前可用的全部路线。
    /// </summary>
    public static IReadOnlyList<PathingRoute> All
    {
        get
        {
            lock (Gate)
            {
                if (DateTime.UtcNow - _loadedAt > CacheLifetime)
                {
                    var routes = Scan();
                    _routes = routes;
                    _byMaterial = BuildIndex(routes);
                    _materials = _byMaterial.Keys
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .ToList();
                    _loadedAt = DateTime.UtcNow;
                }

                return _routes;
            }
        }
    }

    /// <summary>
    /// 按关键词找路线：素材名精确 → 素材名包含 → 文件名或路径包含。
    /// </summary>
    /// <param name="keyword">素材名、角色名或路线名。</param>
    public static List<PathingRoute> Find(string? keyword)
    {
        var text = (keyword ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return [];
        }

        var all = All;
        if (_byMaterial.TryGetValue(text, out var exact) && exact.Count > 0)
        {
            return [.. exact];
        }

        var byMaterial = all
            .Where(r => r.Material.Contains(text, StringComparison.OrdinalIgnoreCase)
                        || text.Contains(r.Material, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byMaterial.Count > 0)
        {
            return byMaterial;
        }

        return all
            .Where(r => r.Relative.Contains(text, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// 列出索引里的全部素材名（去重）。
    /// </summary>
    public static List<string> Materials()
    {
        // 先触发一次加载，索引里的素材名与 _routes 同批更新
        _ = All;
        return [.. _materials];
    }

    /// <summary>
    /// 按素材名分组。同名的多条路线保持扫描顺序，第一条仍是「先跑这条」。
    /// </summary>
    private static Dictionary<string, List<PathingRoute>> BuildIndex(List<PathingRoute> routes)
    {
        var index = new Dictionary<string, List<PathingRoute>>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes)
        {
            if (string.IsNullOrWhiteSpace(route.Material))
            {
                continue;
            }

            if (!index.TryGetValue(route.Material, out var list))
            {
                list = [];
                index[route.Material] = list;
            }

            list.Add(route);
        }

        return index;
    }

    /// <summary>
    /// 扫一遍目录。
    /// </summary>
    private static List<PathingRoute> Scan()
    {
        var routes = new List<PathingRoute>();
        try
        {
            var root = MapPathingViewModel.PathJsonPath;
            if (!Directory.Exists(root))
            {
                return routes;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                var segments = relative.Split(Path.DirectorySeparatorChar);
                if (segments.Length < 2)
                {
                    // 直接放在根目录的路线没有素材信息，只按文件名匹配
                    routes.Add(new PathingRoute(relative, string.Empty, string.Empty, Path.GetFileNameWithoutExtension(file)));
                    continue;
                }

                var region = segments[0];
                var material = segments.Length >= 3 ? segments[1] : string.Empty;
                routes.Add(new PathingRoute(relative, region, material, Path.GetFileNameWithoutExtension(file)));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return routes;
        }

        return routes;
    }
}
