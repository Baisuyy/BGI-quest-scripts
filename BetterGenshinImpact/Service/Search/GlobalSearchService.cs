using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.GameTask.SoloTask;
using BetterGenshinImpact.Service.Interface;
using BetterGenshinImpact.View.Pages;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service.Search;

/// <summary>
/// 全局搜索：把导航页、独立任务、脚本这三类入口拉平成一张可搜的清单。
/// <para>
/// 早期只能靠点左侧导航一页页翻：找个任务要先认清它在哪个分组，找脚本要进「全自动」
/// 再挑配置组。这两类的量都不小（任务近百个、脚本随时在加），翻列表比直接搜慢一个数量级。
/// </para>
/// <para>
/// 不做倒排索引：任务清单来自内存里的注册表，脚本目录一般也就几十个文件，
/// 每次搜索现扫一遍就够快；建索引反而多出「文件改了索引没刷新」这类不一致。
/// </para>
/// </summary>
public sealed class GlobalSearchService
{
    /// <summary>
    /// 单次最多返回多少条。结果页一屏放不下太多，超过这个数说明关键词该再具体点。
    /// </summary>
    public const int MaxHits = 80;

    /// <summary>
    /// 脚本目录的缓存时长。改完脚本组立刻搜可能差几十秒，比每次敲字都读盘划算。
    /// </summary>
    private static readonly TimeSpan ScriptCacheLifetime = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 导航页的搜索表：显示名、别名、目标页面。
    /// <para>
    /// 别名要写用户真正会输入的词（「日志」「遮罩」「代理」），而不是页面标题的变体。
    /// </para>
    /// </summary>
    private static readonly (string Title, string Aliases, Type Page)[] Pages =
    [
        ("启动", "启动 开始 运行 首页 控制 开始任务 home start", typeof(HomePage)),
        ("一条龙", "一条龙 流水线 一键 日常 one dragon", typeof(OneDragonFlowPage)),
        ("一条龙（新版）", "一条龙 新版 计划 pulonia", typeof(PuloniaOneDragonPage)),
        ("独立任务", "独立任务 任务 列表 单个 task", typeof(TaskSettingsPage)),
        ("实时触发", "实时触发 触发器 条件 自动 trigger", typeof(TriggerSettingsPage)),
        ("任务计划", "任务计划 排程 日程 定时 plan schedule", typeof(PuloniaTaskPlanPage)),
        ("执行记录", "执行记录 历史 报表 history", typeof(PuloniaTaskHistoryPage)),
        ("账户", "账户 账号 切换 多开 account", typeof(PuloniaAccountsPage)),
        ("账号记录", "账号记录 抽卡 圣遗物 养成 统计", typeof(AccountRecordsPage)),
        ("全自动", "全自动 配置组 脚本组 调度 脚本 script group", typeof(ScriptControlPage)),
        ("JS 脚本", "js 脚本 javascript 自定义", typeof(JsListPage)),
        ("地图追踪", "地图追踪 路径 寻路 路线 pathing route", typeof(MapPathingPage)),
        ("录制回放", "录制 回放 键鼠 操作记录 record replay", typeof(KeyMouseRecordPage)),
        ("AI 助手", "ai 助手 模型 对话 大模型 gpt llm", typeof(AiSettingsPage)),
        ("更多功能", "更多 功能 杂项 工具箱", typeof(MacroSettingsPage)),
        ("按键宏", "按键宏 宏 连招 macro key", typeof(KeyMacroPage)),
        ("自动演奏", "自动演奏 音乐 琴 乐器 music", typeof(MusicPage)),
        ("悬浮小窗", "悬浮小窗 画中画 小窗 直播 guide", typeof(GuideWindowSettingsPage)),
        ("快捷键", "快捷键 热键 按键 hotkey", typeof(HotKeyPage)),
        ("通知", "通知 推送 邮件 微信 提醒 notification", typeof(NotificationSettingsPage)),
        ("插件", "插件 市场 扩展 plugin market", typeof(PluginsPage)),
        ("设置", "设置 通用 日志 遮罩 背景 代理 启动 更新 字体 setting option", typeof(CommonSettingsPage))
    ];

    private readonly SoloTaskRegistry _registry;
    private readonly ILogger<GlobalSearchService> _logger;
    private readonly object _scriptLock = new();

    private IReadOnlyList<SearchHit> _scriptCache = [];
    private DateTime _scriptCacheAt = DateTime.MinValue;

    /// <summary>
    /// </summary>
    /// <param name="registry">独立任务注册表，任务清单从它来。</param>
    /// <param name="logger">日志。</param>
    public GlobalSearchService(SoloTaskRegistry registry, ILogger<GlobalSearchService> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// 按关键词搜索。
    /// </summary>
    /// <param name="query">关键词，空格分隔视为「都要命中」。</param>
    /// <returns>按相关度排序的结果；关键词为空时返回空列表。</returns>
    public IReadOnlyList<SearchHit> Search(string? query)
    {
        var terms = (query ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return [];
        }

        var hits = new List<(SearchHit Hit, int Score)>();
        try
        {
            foreach (var hit in BuildCatalog())
            {
                var score = Score(terms, hit);
                if (score > 0)
                {
                    hits.Add((hit, score));
                }
            }
        }
        catch (Exception e)
        {
            // 搜索是辅助功能，脚本目录读不了之类的问题不该把整页导航带崩
            _logger.LogWarning(e, "全局搜索时读取索引失败，返回已拿到的 {Count} 条", hits.Count);
        }

        return hits
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Hit.GroupOrder)
            .ThenBy(x => x.Hit.Title, StringComparer.CurrentCulture)
            .Take(MaxHits)
            .Select(x => x.Hit)
            .ToList();
    }

    /// <summary>
    /// 把三类来源拼成一份清单。页面表是常量，任务来自注册表，脚本来自配置组目录。
    /// </summary>
    private IEnumerable<SearchHit> BuildCatalog()
    {
        foreach (var (title, aliases, page) in Pages)
        {
            yield return new SearchHit(title, aliases, SearchHitKind.Page, page, title);
        }

        foreach (var task in _registry.All)
        {
            var subtitle = string.IsNullOrWhiteSpace(task.Group)
                ? task.Description
                : $"{task.Group} · {task.Description}";
            yield return new SearchHit(
                task.DisplayName,
                subtitle,
                SearchHitKind.Task,
                typeof(TaskSettingsPage),
                task.TaskId);
        }

        foreach (var script in LoadScripts())
        {
            yield return script;
        }
    }

    /// <summary>
    /// 读「全自动」的配置组，把组名和组里的脚本都变成可搜条目。
    /// </summary>
    private IReadOnlyList<SearchHit> LoadScripts()
    {
        lock (_scriptLock)
        {
            if (DateTime.UtcNow - _scriptCacheAt < ScriptCacheLifetime)
            {
                return _scriptCache;
            }

            _scriptCache = ReadScripts();
            _scriptCacheAt = DateTime.UtcNow;
            return _scriptCache;
        }
    }

    private IReadOnlyList<SearchHit> ReadScripts()
    {
        var directory = Global.Absolute(@"User\ScriptGroup");
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var hits = new List<SearchHit>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            ScriptGroup? group;
            try
            {
                group = JsonSerializer.Deserialize<ScriptGroup>(
                    File.ReadAllText(file),
                    ConfigService.JsonOptions);
            }
            catch (Exception e)
            {
                // 单个配置文件坏了不该让整个搜索失灵，记一笔继续下一个
                _logger.LogDebug(e, "读取配置组失败，搜索时跳过：{File}", Path.GetFileName(file));
                continue;
            }

            if (group is null || string.IsNullOrWhiteSpace(group.Name))
            {
                continue;
            }

            hits.Add(new SearchHit(
                group.Name,
                "配置组 · 全自动",
                SearchHitKind.Script,
                typeof(ScriptControlPage),
                group.Name));

            foreach (var project in group.Projects.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
            {
                hits.Add(new SearchHit(
                    project.Name,
                    $"配置组「{group.Name}」里的脚本",
                    SearchHitKind.Script,
                    typeof(ScriptControlPage),
                    project.Name));
            }
        }

        return hits;
    }

    /// <summary>
    /// 打分：标题命中比副标题/别名命中更相关，前缀命中比中间命中更相关。
    /// </summary>
    /// <param name="terms">关键词（全部都要命中）。</param>
    /// <param name="hit">候选。</param>
    /// <returns>大于 0 表示命中，数值越大越相关。</returns>
    private static int Score(string[] terms, SearchHit hit)
    {
        var title = hit.Title;
        var haystack = hit.Title + " " + hit.Subtitle;

        var total = 0;
        foreach (var term in terms)
        {
            var score = 0;
            if (title.Equals(term, StringComparison.CurrentCultureIgnoreCase))
            {
                score = 1000;
            }
            else if (title.StartsWith(term, StringComparison.CurrentCultureIgnoreCase))
            {
                score = 800;
            }
            else if (title.Contains(term, StringComparison.CurrentCultureIgnoreCase))
            {
                score = 600;
            }
            else if (haystack.Contains(term, StringComparison.CurrentCultureIgnoreCase))
            {
                score = 300;
            }

            if (score == 0)
            {
                // 有一个词没命中就整条不算：搜「自动 演奏」不该把「自动任务」也算上
                return 0;
            }

            total += score;
        }

        // 标题越短说明关键词占的比重越大，「设置」这类短标题更容易是用户要的
        return total - title.Length;
    }
}
