using System.Windows;
using System.Windows.Controls;

namespace BetterGenshinImpact.View.Controls;

/// <summary>
/// 特别版设置卡片：日志（落盘级别、诊断包、日志上传）与远程任务（QQ 机器人、米游社扫码绑定）。
/// 数据上下文沿用设置页的 ViewModel；单独成文件是为了在上游同步覆盖
/// CommonSettingsPage.xaml 后只需重新插一行引用，不会再把整段设置吃掉。
/// </summary>
public partial class SpecialSettingsCards : UserControl
{
    public SpecialSettingsCards()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 打开米游社扫码绑定窗口：比让用户去开发者工具里抄 Cookie 靠谱得多。
    /// </summary>
    private void OnMysLoginClick(object sender, RoutedEventArgs e)
    {
        var window = new BetterGenshinImpact.View.Windows.MysLoginWindow
        {
            Owner = Window.GetWindow(this)
        };
        window.Show();
    }
}
