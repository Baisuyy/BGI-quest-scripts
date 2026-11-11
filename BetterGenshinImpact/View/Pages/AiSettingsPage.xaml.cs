using System.Windows.Controls;
using BetterGenshinImpact.ViewModel.Pages;

namespace BetterGenshinImpact.View.Pages;

/// <summary>「AI 助手」页：模型连接 + 本地助手（Agent）。</summary>
public partial class AiSettingsPage : Page
{
    private AiSettingsPageViewModel ViewModel { get; }

    public AiSettingsPage(AiSettingsPageViewModel viewModel)
    {
        DataContext = ViewModel = viewModel;
        InitializeComponent();
    }
}
