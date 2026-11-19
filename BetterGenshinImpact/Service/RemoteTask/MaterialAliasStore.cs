using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using BetterGenshinImpact.Core.Config;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service.RemoteTask;

/// <summary>
/// 「角色名 / 口语说法」到采集物名的对照表。
/// <para>
/// 「米提亚材料」这类说法，第一次只能靠模型判断；一旦跑通就记下来，
/// 之后同一个说法直接查表，不用再花一次模型调用，也不怕模型换代后忘掉。
/// 文件在 <c>User\RemoteTask\material-alias.json</c>，纯文本，可以手改。
/// </para>
/// </summary>
public sealed class MaterialAliasStore
{
    private static readonly string FilePath = Global.Absolute(@"User\RemoteTask\material-alias.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly object Gate = new();

    private static Dictionary<string, string>? _cache;

    private static ILogger? _logger;

    private static ILogger Log => _logger ??= App.GetLogger<MaterialAliasStore>();

    /// <summary>
    /// 查一个说法对应的采集物。
    /// </summary>
    /// <param name="query">用户原话里的素材/角色名。</param>
    /// <returns>记过的采集物名；没有记录时返回 null。</returns>
    public static string? TryGet(string? query)
    {
        var key = Normalize(query);
        if (key.Length == 0)
        {
            return null;
        }

        lock (Gate)
        {
            var map = Load();
            return map.TryGetValue(key, out var material) && material.Length > 0 ? material : null;
        }
    }

    /// <summary>
    /// 记下一次「这个说法对应这个采集物」。已经记过且没变就不写盘。
    /// </summary>
    /// <param name="query">用户原话里的素材/角色名。</param>
    /// <param name="material">实际跑通的采集物名。</param>
    public static void Remember(string? query, string? material)
    {
        var key = Normalize(query);
        var value = (material ?? string.Empty).Trim();
        if (key.Length == 0 || value.Length == 0)
        {
            return;
        }

        lock (Gate)
        {
            var map = Load();
            if (map.TryGetValue(key, out var existing) && string.Equals(existing, value, StringComparison.Ordinal))
            {
                return;
            }

            map[key] = value;
            try
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(FilePath, JsonSerializer.Serialize(map, Options));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 写不进去不影响本次采集，只是下次还得再判断一次
                Log.LogWarning(e, "素材对照表写入失败");
            }
        }
    }

    /// <summary>
    /// 归一化查询词：去掉「材料/素材」这类修饰词，统一小写。
    /// </summary>
    private static string Normalize(string? query)
    {
        var cleaned = RemoteTaskCommandParser.StripMaterialNoise(query);
        return cleaned.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// 读盘并缓存；文件损坏时当作空表，不让坏数据把聊天链路带崩。
    /// </summary>
    private static Dictionary<string, string> Load()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(json, Options)
                         ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.LogWarning(e, "素材对照表读取失败，按空表处理");
            _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return _cache;
    }
}
