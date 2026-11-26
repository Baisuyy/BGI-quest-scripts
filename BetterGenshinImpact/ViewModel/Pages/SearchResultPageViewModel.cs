using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using BetterGenshinImpact.Service.Search;
using BetterGenshinImpact.ViewModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui;
using Wpf.Ui.Abstractions.Controls;

namespace BetterGenshinImpact.ViewModel.Pages;

/// <summary>
/// 一组搜索结果：分组标题 + 组内条目。
/// </summary>
/// <param name="Title">分组标题。</param>
/// <param name="Items">组内结果。</param>
public sealed class SearchResultGroup(string title, IReadOnlyList<SearchHit> items)
{
    /// <summary>分组标题。</summary>
    public string Title { get; } = title;

    /// <summary>组内结果。</summary>
    public IReadOnlyList<SearchHit> Items { get; } = items;
}

/// <summary>
/// 搜索结果页：把顶部搜索栏提交的关键词展开成分组列表。
/// <para>
/// 顶部栏只负责「输入 + 跳转」，真正的结果在这里出。这样做的好处是结果能滚动、
/// 能分组、能点开，而不是挤在标题栏下面一条窄窄的下拉里。
/// </para>
/// </summary>
public partial class SearchResultPageViewModel : ViewModel
{
    private readonly GlobalSearchService _search;
    private readonly GlobalSearchState _state;
    private readonly INavigationService _navigationService;

    /// <summary>
    /// 当前关键词。
    /// </summary>
    [ObservableProperty]
    private string _query = string.Empty;

    /// <summary>
    /// 页面里那个搜索框的内容。改完回车重搜，不用回到标题栏。
    /// </summary>
    [ObservableProperty]
    private string _input = string.Empty;

    /// <summary>
    /// 结果概览，例如「找到 12 条」或「没有找到」。
    /// </summary>
    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>
    /// 是否有结果，用于切换空状态。
    /// </summary>
    [ObservableProperty]
    private bool _hasResults;

    /// <summary>
    /// 没有结果（或还没搜）时展示引导文案。
    /// </summary>
    [ObservableProperty]
    private bool _isEmpty = true;

    /// <summary>
    /// 分组后的结果。
    /// </summary>
    public ObservableCollection<SearchResultGroup> Groups { get; } = [];

    /// <summary>
    /// </summary>
    /// <param name="search">搜索服务。</param>
    /// <param name="state">跨页面传递的关键词。</param>
    /// <param name="navigationService">导航服务，点击条目后跳转。</param>
    public SearchResultPageViewModel(
        GlobalSearchService search,
        GlobalSearchState state,
        INavigationService navigationService)
    {
        _search = search;
        _state = state;
        _navigationService = navigationService;
    }

    /// <inheritdoc />
    public override void OnNavigatedTo()
    {
        Input = _state.Query;
        Run(_state.Query);
    }

    /// <summary>
    /// 用页面里的搜索框重搜。
    /// </summary>
    [RelayCommand]
    private void Search()
    {
        Run(Input);
        Input = Query;
    }

    /// <summary>
    /// 回上一页。搜索页不在左侧导航里，没有返回就只能靠左侧随便点一个。
    /// </summary>
    [RelayCommand]
    private void GoBack() => _navigationService.GoBack();

    /// <summary>
    /// 用新关键词重跑一次。
    /// </summary>
    /// <param name="keyword">关键词。</param>
    public void Run(string? keyword)
    {
        var text = (keyword ?? string.Empty).Trim();
        Query = text;
        _state.Query = text;

        Groups.Clear();
        if (text.Length == 0)
        {
            Summary = "在上面输入关键词，回车搜索。";
            HasResults = false;
            IsEmpty = true;
            return;
        }

        var hits = _search.Search(text);
        foreach (var group in hits.GroupBy(h => h.GroupName))
        {
            Groups.Add(new SearchResultGroup(group.Key, group.ToList()));
        }

        HasResults = hits.Count > 0;
        IsEmpty = !HasResults;
        Summary = hits.Count > 0
            ? $"「{text}」找到 {hits.Count} 条"
            : $"没有找到「{text}」。换个说法试试，比如「日志」「圣遗物」「录制」。";
    }

    /// <summary>
    /// 打开一条结果：跳到它所属的页面。
    /// </summary>
    /// <param name="hit">被点击的结果。</param>
    [RelayCommand]
    private void Open(SearchHit? hit)
    {
        if (hit?.PageType is { } page)
        {
            _navigationService.Navigate(page);
        }
    }
}
