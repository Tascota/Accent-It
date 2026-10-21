using System.Runtime.InteropServices;
using System.Windows.Interop;
using AccentIt.Models;

namespace AccentIt.Services;

public sealed record HotkeyRegistrationResult(bool Success, int ErrorCode, string Message);

public sealed class GlobalHotkeyService : IDisposable
{
    // 应用热键 id 必须在 0x0000–0xBFFF 之间。
    private const int HotkeyId = 0xACC1;
    private const int DebounceMilliseconds = 150;

    private HwndSource? _source;
    private IntPtr _windowHandle;
    private long _lastPressedTick;
    private bool _registered;
    private bool _disposed;

    public event EventHandler? HotkeyPressed;

    public bool IsRegistered => _registered;

    /// <summary>
    /// 在调用线程上创建 message-only 窗口并注册系统热键。
    /// 必须在 WPF UI 线程（同一 Dispatcher 线程）调用。
    /// </summary>
    public HotkeyRegistrationResult Register(HotkeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_registered)
        {
            return new HotkeyRegistrationResult(true, 0, "全局快捷键已经注册。");
        }

        try
        {
            if (_source is null)
            {
                // HWND_MESSAGE = (IntPtr)(-3)：创建一个只接收消息的隐藏窗口。
                // 它不显示、不进入任务栏，也不依赖 MainWindow 的生命周期。
                var parameters = new HwndSourceParameters("AccentIt.GlobalHotkey")
                {
                    ParentWindow = (IntPtr)(-3),
                    WindowStyle = 0,
                    Width = 0,
                    Height = 0
                };

                _source = new HwndSource(parameters);
                _source.AddHook(WndProc);
                _windowHandle = _source.Handle;
            }
        }
        catch (Exception ex)
        {
            DisposeSource();
            return new HotkeyRegistrationResult(
                false,
                0,
                $"无法创建热键消息窗口：{ex.Message}");
        }

        // HotkeyModifiers 的数值与 Win32 MOD_* 常量一致。
        // MOD_NOREPEAT 阻止按键自动重复产生多个 WM_HOTKEY。
        var modifiers = (uint)gesture.Modifiers | NativeMethods.MOD_NOREPEAT;

        if (!NativeMethods.RegisterHotKey(
                _windowHandle,
                HotkeyId,
                modifiers,
                gesture.VirtualKey))
        {
            var errorCode = Marshal.GetLastPInvokeError();
            DisposeSource();
            return new HotkeyRegistrationResult(
                false,
                errorCode,
                DescribeRegistrationError(errorCode));
        }

        _registered = true;
        return new HotkeyRegistrationResult(true, 0, $"已注册 {gesture}。");
    }

    private IntPtr WndProc(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg != NativeMethods.WM_HOTKEY || wParam.ToInt32() != HotkeyId)
        {
            return IntPtr.Zero;
        }

        handled = true;

        // MOD_NOREPEAT 之外再做一次短时间去抖，防止个别输入法或驱动重复投递。
        var now = Environment.TickCount64;

        if (now - _lastPressedTick < DebounceMilliseconds)
        {
            return IntPtr.Zero;
        }

        _lastPressedTick = now;
        HotkeyPressed?.Invoke(this, EventArgs.Empty);
        return IntPtr.Zero;
    }

    private static string DescribeRegistrationError(int errorCode)
        => errorCode switch
        {
            NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED
                => "快捷键已被其他程序占用，请在 settings.jsonc 中换一个组合后重启。",
            _ => $"注册全局快捷键失败（Win32 错误 {errorCode}）。"
        };

    private void DisposeSource()
    {
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }

        _windowHandle = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // UnregisterHotKey 必须在注册它的同一线程上调用；
        // 本项目在 WPF UI 线程创建，也在 MainWindow 关闭时释放。
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_windowHandle, HotkeyId);
            _registered = false;
        }

        DisposeSource();
        GC.SuppressFinalize(this);
    }
}
