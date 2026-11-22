using System.Windows.Controls;
using BetterGenshinImpact.ViewModel.Pages;

namespace BetterGenshinImpact.View.Pages;

/// <summary>
/// 搜索结果页。关键词由顶部搜索栏写入 <see cref="Service.Search.GlobalSearchState"/>，
/// 页面在导航到这里时读取并展开结果。
/// </summary>
public partial class SearchResultPage : Page
{
    private SearchResultPageViewModel ViewModel { get; }

    /// <summary>
    /// </summary>
    /// <param name="viewModel">页面视图模型。</param>
    public SearchResultPage(SearchResultPageViewModel viewModel)
    {
        DataContext = ViewModel = viewModel;
        InitializeComponent();
    }
}
