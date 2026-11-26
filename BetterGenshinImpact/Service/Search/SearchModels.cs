using System;

namespace BetterGenshinImpact.Service.Search;

/// <summary>
/// 搜索结果的来源类别，决定结果页里的分组标题。
/// </summary>
public enum SearchHitKind
{
    /// <summary>左侧导航里的页面。</summary>
    Page,

    /// <summary>独立任务。</summary>
    Task,

    /// <summary>「全自动」里的配置组与脚本。</summary>
    Script
}

/// <summary>
/// 一条搜索结果。
/// </summary>
/// <param name="Title">主标题，用户一眼要找的东西。</param>
/// <param name="Subtitle">补充说明：所属分组、用途等。</param>
/// <param name="Kind">来源类别，用于分组。</param>
/// <param name="PageType">点击后要跳转的页面。</param>
/// <param name="Keyword">任务标识或脚本路径，点击后用来定位。</param>
public sealed record SearchHit(
    string Title,
    string Subtitle,
    SearchHitKind Kind,
    Type? PageType,
    string? Keyword = null)
{
    /// <summary>
    /// 分组标题。
    /// </summary>
    public string GroupName => Kind switch
    {
        SearchHitKind.Task => "独立任务",
        SearchHitKind.Script => "脚本",
        _ => "页面"
    };

    /// <summary>
    /// 分组在结果页里的先后。页面是导航入口，放最前。
    /// </summary>
    public int GroupOrder => Kind switch
    {
        SearchHitKind.Page => 0,
        SearchHitKind.Task => 1,
        _ => 2
    };
}

/// <summary>
/// 一次搜索在页面之间传递的状态。
/// <para>
/// 导航 API 只接受页面类型，带不了参数；搜索页需要知道搜的是什么，
/// 所以把关键词放在这个单例里，进入页面时再取。
/// </para>
/// </summary>
public sealed class GlobalSearchState
{
    /// <summary>
    /// 最近一次提交的关键词。
    /// </summary>
    public string Query { get; set; } = string.Empty;
}
