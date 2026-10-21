using System.Drawing;
using Forms = System.Windows.Forms;

namespace AccentIt.Services;

/// <summary>
/// 系统托盘图标和菜单。NotifyIcon 依赖创建线程的消息循环，因此必须在
/// WPF UI 线程创建和释放；事件也由该线程转发给窗口。
/// 使用 Windows 自带的 WinForms NotifyIcon，不引入第三方包。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Icon _icon;
    private readonly Forms.NotifyIcon _notifyIcon;
    private bool _disposed;

    public event EventHandler? ShowRequested;

    public event EventHandler? ExitRequested;

    public TrayIconService()
    {
        // 托盘需要一个图标。这里使用系统自带图标，避免额外图标资源；
        // Clone 后由本服务负责释放。
        _icon = (Icon)SystemIcons.Application.Clone();

        _menu = new Forms.ContextMenuStrip();

        var showItem = new Forms.ToolStripMenuItem("打开 Accent It");
        showItem.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

        var exitItem = new Forms.ToolStripMenuItem("退出 Accent It");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        _menu.Items.Add(showItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "Accent It（单击打开）",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                ShowRequested?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    /// <summary>显示托盘气泡提示。部分系统设置会禁用气泡，失败时不影响程序。</summary>
    public void ShowInfo(string title, string text)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _notifyIcon.ShowBalloonTip(
                3000,
                title,
                text,
                Forms.ToolTipIcon.Info);
        }
        catch
        {
            // 托盘提示只是辅助信息，不能因此中断关闭/退出流程。
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }
}
