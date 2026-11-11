using System.Windows.Controls;

namespace BetterGenshinImpact.View.Controls;

/// <summary>
/// 内置 AI 设置卡片。数据上下文沿用任务设置页的 ViewModel，
/// 单独成文件是为了在上游同步覆盖 TaskSettingsPage.xaml 后只需重新插一行引用。
/// </summary>
public partial class AiSettingsCard : UserControl
{
    public AiSettingsCard()
    {
        InitializeComponent();
    }
}
