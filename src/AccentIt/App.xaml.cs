using System.Windows;
using AccentIt.Services;

namespace AccentIt;

/// <summary>
/// 程序启动时先恢复上次选择的主题，再创建主窗口，避免先闪一下浅色界面。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ThemeManager.Initialize(ThemeStore.CreateDefault());

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
