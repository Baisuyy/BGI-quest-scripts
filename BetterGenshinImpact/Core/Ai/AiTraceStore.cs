using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Core.Ai;

/// <summary>
/// 轨迹中的一步动作。
/// </summary>
/// <param name="Index">第几步，从 1 开始。</param>
/// <param name="Tool">工具名。</param>
/// <param name="Args">参数原文，坐标类动作保留像素值。</param>
/// <param name="Note">执行结果的一句话摘要。</param>
/// <param name="ElapsedMs">这一步耗时。</param>
public sealed record AiTraceStep(int Index, string Tool, string Args, string Note, int ElapsedMs);

/// <summary>
/// 一次自由操作的完整轨迹，也是「自学习」的学习样本。
/// </summary>
public sealed record AiTraceEntry(
    string Id,
    DateTime StartedAt,
    string Goal,
    bool Success,
    string Summary,
    int Steps,
    int Calls,
    IReadOnlyList<AiTraceStep> Trace)
{
    /// <summary>
    /// 给模型看的一行式回放，只保留动作序列。
    /// </summary>
    public string ToPlaybook()
    {
        var actions = Trace.Select(s => $"{s.Tool}({s.Args})");
        return $"{Goal} → {string.Join(" → ", actions)}";
    }
}

/// <summary>
/// 轨迹仓库：把「模型怎么点出来的」落盘，下次遇到同类请求先给模型看历史成功路径。
/// <para>
/// 放在 <c>log/ai-trace/</c> 下，与运行日志同目录，用户回传日志时顺手就把轨迹带出来了。
/// 每条既有 JSON（给程序读），也有一行 index.log（给人看、方便贴给开发者）。
/// </para>
/// <para>
/// 这里不做任何归纳与训练：所谓自学习就是「成功路径复用」，失败路径同样记录，
/// 但只在同类请求没有任何成功样本时才作为参考，避免把错误做法教给模型。
/// </para>
/// </summary>
public sealed class AiTraceStore
{
    private static readonly ILogger Logger = App.GetLogger<AiTraceStore>();

    /// <summary>
    /// 轨迹目录。
    /// </summary>
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "log", "ai-trace");

    /// <summary>
    /// 人类可读索引文件。
    /// </summary>
    private static string IndexFile => Path.Combine(Folder, "index.log");

    /// <summary>
    /// 最多保留的轨迹文件数，超出后按时间删除最旧的。
    /// </summary>
    private const int MaxFiles = 400;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// 保存一条轨迹。写盘失败只记日志，不影响正在跑的操作。
    /// </summary>
    /// <param name="entry">轨迹。</param>
    public async Task SaveAsync(AiTraceEntry entry)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var name = $"{entry.StartedAt:yyyyMMdd-HHmmss}-{entry.Id}.json";
            await File.WriteAllTextAsync(
                Path.Combine(Folder, name),
                JsonSerializer.Serialize(entry, JsonOptions)).ConfigureAwait(false);

            var line = $"{entry.StartedAt:yyyy-MM-dd HH:mm:ss}\t{(entry.Success ? "OK" : "FAIL")}\t" +
                       $"{entry.Steps}步/{entry.Calls}次\t{entry.Goal}\t{entry.Summary}";
            await File.AppendAllTextAsync(IndexFile, line + Environment.NewLine).ConfigureAwait(false);

            Logger.LogInformation("[AI轨迹] {Result} · {Steps} 步 · {Goal} · {Summary}",
                entry.Success ? "成功" : "失败", entry.Steps, entry.Goal, entry.Summary);
            foreach (var step in entry.Trace)
            {
                Logger.LogInformation("[AI轨迹]   {Index}. {Tool}({Args}) → {Note}",
                    step.Index, step.Tool, step.Args, step.Note);
            }

            Prune();
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "保存 AI 轨迹失败");
        }
    }

    /// <summary>
    /// 读最近若干条轨迹（新在前）。
    /// </summary>
    /// <param name="limit">最多读多少条。</param>
    public IReadOnlyList<AiTraceEntry> Recent(int limit = 20)
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                return [];
            }

            return Directory.EnumerateFiles(Folder, "*.json")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(Math.Max(1, limit))
                .Select(Read)
                .Where(entry => entry != null)
                .Select(entry => entry!)
                .ToArray();
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "读取 AI 轨迹失败");
            return [];
        }
    }

    /// <summary>
    /// 找一条可以复用的成功轨迹：目标文本相似度最高者。
    /// </summary>
    /// <param name="goal">本次目标描述。</param>
    /// <returns>没有足够相似的样本时返回 null。</returns>
    public AiTraceEntry? FindReusable(string goal)
    {
        if (string.IsNullOrWhiteSpace(goal))
        {
            return null;
        }

        return Recent(80)
            .Where(entry => entry.Success)
            .Select(entry => (Entry: entry, Score: Similarity(goal, entry.Goal)))
            .Where(pair => pair.Score >= 0.5)
            .OrderByDescending(pair => pair.Score)
            .Select(pair => pair.Entry)
            .FirstOrDefault();
    }

    /// <summary>
    /// 找一条同类的失败轨迹，提炼成一句「上次卡在哪」的教训。
    /// <para>
    /// 没有成功样本时才知道该避开什么：失败轨迹的最后两步通常就是死循环，
    /// 原样喂给模型比让它从零摸索省几步。有成功样本时不返回，免得把错的做法教过去。
    /// </para>
    /// </summary>
    /// <param name="goal">本次目标描述。</param>
    /// <returns>一句话教训；没有可用样本时返回 null。</returns>
    public string? FindFailedLesson(string goal)
    {
        if (string.IsNullOrWhiteSpace(goal))
        {
            return null;
        }

        var sample = Recent(80)
            .Where(entry => !entry.Success && entry.Trace.Count >= 2)
            .Select(entry => (Entry: entry, Score: Similarity(goal, entry.Goal)))
            .Where(pair => pair.Score >= 0.6)
            .OrderByDescending(pair => pair.Score)
            .Select(pair => pair.Entry)
            .FirstOrDefault();

        if (sample == null)
        {
            return null;
        }

        var last = sample.Trace[^1];
        var before = sample.Trace[^2];
        var lesson = $"上次做同类的事，{sample.Steps} 步没成（{sample.Summary}）。" +
                     $"最后卡在 {before.Tool}({before.Args}) → {last.Tool}({last.Args})，这两个别按原样再来一遍。";

        // 后两步完全相同时，说明是原地打转，单独点出来
        if (string.Equals(before.Tool, last.Tool, StringComparison.Ordinal)
            && string.Equals(before.Args, last.Args, StringComparison.Ordinal))
        {
            lesson += "注意是同参数重复，等于没动。";
        }

        return lesson;
    }

    /// <summary>
    /// 清掉超出上限的旧文件，避免目录无限膨胀。
    /// </summary>
    private static void Prune()
    {
        try
        {
            var files = Directory.EnumerateFiles(Folder, "*.json")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Skip(MaxFiles)
                .ToArray();
            foreach (var file in files)
            {
                File.Delete(file);
            }
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "清理 AI 轨迹失败");
        }
    }

    /// <summary>
    /// 读一个轨迹文件，损坏时返回 null。
    /// </summary>
    private static AiTraceEntry? Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<AiTraceEntry>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 中文短句相似度：按字符二元组算 Jaccard，够用且不需要分词。
    /// </summary>
    /// <param name="left">文本一。</param>
    /// <param name="right">文本二。</param>
    private static double Similarity(string left, string right)
    {
        var a = Bigrams(left);
        var b = Bigrams(right);
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var overlap = a.Count(bigram => b.Contains(bigram));
        return (double)overlap / (a.Count + b.Count - overlap);
    }

    /// <summary>
    /// 取字符二元组集合，忽略空白与标点。
    /// </summary>
    private static HashSet<string> Bigrams(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c))
            {
                builder.Append(c);
            }
        }

        var clean = builder.ToString();
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < clean.Length; i++)
        {
            set.Add(clean.Substring(i, 2));
        }

        if (clean.Length == 1)
        {
            set.Add(clean);
        }

        return set;
    }

    /// <summary>
    /// 轨迹标识，用时间戳保证同秒内也不重名。
    /// </summary>
    public static string NewId() => DateTime.Now.ToString("ffffff", CultureInfo.InvariantCulture);
}
