using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AccentIt.Models;

namespace AccentIt.Services;

public sealed record ActivationResult(bool Success, string Message);

/// <summary>
/// 负责捕获前台目标、恢复目标焦点和检查目标是否仍然有效。
/// 这里不读取目标应用的文本内容，只处理窗口/线程/焦点句柄和位置。
/// </summary>
public static class ForegroundWindowService
{
    private static readonly int[] ModifierVirtualKeys =
    {
        NativeMethods.VK_SHIFT,
        NativeMethods.VK_CONTROL,
        NativeMethods.VK_MENU,
        NativeMethods.VK_LWIN,
        NativeMethods.VK_RWIN
    };

    /// <summary>
    /// 在热键处理中同步捕获当前前台应用。Win32 调用很快；UI Automation 的
    /// 备用定位由 CaretLocator 在工作线程中单独执行。
    /// </summary>
    public static TargetContext? Capture()
    {
        var windowHandle = NativeMethods.GetForegroundWindow();

        if (windowHandle == IntPtr.Zero)
        {
            return null;
        }

        var threadId = NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        var isExternal = processId != Environment.ProcessId;
        var target = new TargetContext
        {
            WindowHandle = windowHandle,
            ProcessId = processId,
            ThreadId = threadId,
            ProcessName = GetProcessName(processId),
            IsExternal = isExternal
        };

        if (!isExternal)
        {
            // 目标就是 Accent It 自己。紧凑窗口不应尝试向自身插入。
            return target;
        }

        var threadInfo = GetThreadInfo(threadId);
        var focusHandle = threadInfo?.hwndFocus ?? IntPtr.Zero;
        var isPassword = IsClassicPasswordField(focusHandle);
        var caretRect = threadInfo is { } info ? TryGetWin32Caret(info) : null;

        return target with
        {
            FocusHandle = focusHandle,
            IsPasswordField = isPassword,
            CaretRect = caretRect,
            CaretSource = caretRect is not null ? CaretSource.Win32Caret : CaretSource.None
        };
    }

    /// <summary>
    /// 把 CaretLocator 的 UIA 结果合并到目标上下文。
    /// 密码框只记录“不可自动插入”，不保留文本选区或内容。
    /// </summary>
    public static TargetContext ApplyCaretLocation(
        TargetContext target,
        CaretLocationResult location)
    {
        if (location.IsPasswordField)
        {
            return target with
            {
                IsPasswordField = true,
                CaretRect = null,
                FocusBounds = null,
                AutomationFocusElement = location.FocusElement ?? target.AutomationFocusElement,
                CaretSource = CaretSource.None
            };
        }

        var caretRect = location.CaretRect ?? target.CaretRect;
        var focusBounds = location.FocusBounds ?? target.FocusBounds;
        var source = target.CaretSource;

        if (location.CaretRect is not null)
        {
            source = CaretSource.UiaTextSelection;
        }
        else if (location.FocusBounds is not null && source == CaretSource.None)
        {
            source = CaretSource.UiaFocusBounds;
        }

        return target with
        {
            CaretRect = caretRect,
            FocusBounds = focusBounds,
            CaretSource = source,
            AutomationFocusElement = location.FocusElement ?? target.AutomationFocusElement
        };
    }

    /// <summary>
    /// 确认目标顶层窗口仍存在，进程/线程没有变化，而且没有最小化。
    /// 注意：紧凑窗口打开后目标本来就会失去前台，因此这里不能检查目标的
    /// 当前焦点；目标焦点会在 ActivateAsync 把它切回前台之后再核对。
    /// </summary>
    public static bool TryValidate(TargetContext? target, out string error)
    {
        if (target is null || !target.HasUsableTarget)
        {
            error = "没有捕获到可插入的外部目标。";
            return false;
        }

        if (!NativeMethods.IsWindow(target.WindowHandle))
        {
            error = "目标窗口已经关闭。";
            return false;
        }

        var threadId = NativeMethods.GetWindowThreadProcessId(target.WindowHandle, out var processId);

        if (processId != target.ProcessId || threadId != target.ThreadId)
        {
            error = "前台目标已经换成其他窗口。";
            return false;
        }

        if (NativeMethods.IsIconic(target.WindowHandle))
        {
            error = "目标窗口已经最小化。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool IsForeground(IntPtr windowHandle)
        => windowHandle != IntPtr.Zero && NativeMethods.GetForegroundWindow() == windowHandle;

    /// <summary>
    /// 关闭紧凑窗口时尽力把前台和键盘焦点还给原目标。这里只使用 Win32
    /// 焦点句柄，不在关闭路径上等待 UI Automation；目标已失效时安全放弃。
    /// </summary>
    public static bool TryRestoreTargetFocus(TargetContext? target)
    {
        if (target is null
            || !target.HasUsableTarget
            || !TryValidate(target, out _)
            || !TryBringToForeground(target.WindowHandle))
        {
            return false;
        }

        if (target.FocusHandle != IntPtr.Zero)
        {
            var threadInfo = GetThreadInfo(target.ThreadId);

            if (threadInfo is null || threadInfo.Value.hwndFocus != target.FocusHandle)
            {
                TrySetWin32Focus(target.ThreadId, target.FocusHandle);
                threadInfo = GetThreadInfo(target.ThreadId);

                if (threadInfo is null || threadInfo.Value.hwndFocus != target.FocusHandle)
                {
                    return false;
                }
            }
        }

        return IsForeground(target.WindowHandle);
    }

    /// <summary>
    /// 把一个窗口带到前台。Windows 会限制后台程序抢前台，因此第一次
    /// SetForegroundWindow 失败时，临时 AttachThreadInput 到当前前台线程再试。
    /// </summary>
    public static bool TryBringToForeground(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle))
        {
            return false;
        }

        NativeMethods.SetForegroundWindow(windowHandle);

        if (IsForeground(windowHandle))
        {
            return true;
        }

        var foregroundWindow = NativeMethods.GetForegroundWindow();
        var foregroundThread = foregroundWindow == IntPtr.Zero
            ? 0
            : NativeMethods.GetWindowThreadProcessId(foregroundWindow, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();

        if (foregroundThread == 0 || foregroundThread == currentThread)
        {
            NativeMethods.SetForegroundWindow(windowHandle);
            return IsForeground(windowHandle);
        }

        var attached = NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);

        try
        {
            NativeMethods.SetForegroundWindow(windowHandle);
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        return IsForeground(windowHandle);
    }

    /// <summary>
    /// 恢复目标前台，并在可能时把键盘焦点放回捕获的输入框。
    /// UIA 的 SetFocus 是跨进程调用，放到工作线程并设置短超时，避免卡住界面。
    /// </summary>
    public static async Task<ActivationResult> ActivateAsync(
        TargetContext target,
        TimeSpan focusTimeout)
    {
        if (!TryValidate(target, out var validationError))
        {
            return new ActivationResult(false, validationError);
        }

        if (!TryBringToForeground(target.WindowHandle))
        {
            await Task.Delay(80);

            if (!TryBringToForeground(target.WindowHandle))
            {
                return new ActivationResult(false, "无法把目标窗口切换到前台。");
            }
        }

        if (!IsForeground(target.WindowHandle))
        {
            return new ActivationResult(false, "目标窗口未能保持前台。");
        }

        var win32FocusRestored = target.FocusHandle == IntPtr.Zero;

        if (target.FocusHandle != IntPtr.Zero)
        {
            var threadInfo = GetThreadInfo(target.ThreadId);

            if (threadInfo is null)
            {
                return new ActivationResult(false, "无法读取目标窗口的输入焦点。");
            }

            if (threadInfo.Value.hwndFocus != target.FocusHandle)
            {
                TrySetWin32Focus(target.ThreadId, target.FocusHandle);
                await Task.Delay(60);
                threadInfo = GetThreadInfo(target.ThreadId);
            }

            win32FocusRestored = threadInfo is not null
                && threadInfo.Value.hwndFocus == target.FocusHandle;

            if (!win32FocusRestored && target.AutomationFocusElement is null)
            {
                return new ActivationResult(false, "无法把输入焦点放回目标输入框。");
            }
        }

        if (target.AutomationFocusElement is { } focusElement
            && (target.FocusHandle == IntPtr.Zero || !win32FocusRestored))
        {
            // Win32 焦点没有恢复时才使用 UIA 作为备用。Chromium 等应用的目标
            // 没有独立的 Win32 子焦点句柄，此时 UIA 是恢复输入框焦点的唯一依据。
            if (!await TrySetUiaFocusAsync(focusElement, focusTimeout))
            {
                return new ActivationResult(false, "无法把焦点放回目标输入框。");
            }

            // UIA 的 SetFocus 成功不代表 Win32 焦点一定回到了捕获的控件。
            // 捕获过具体控件时再核对一次，避免把文本粘贴到错误的位置。
            if (target.FocusHandle != IntPtr.Zero)
            {
                var threadInfo = GetThreadInfo(target.ThreadId);

                if (threadInfo is null || threadInfo.Value.hwndFocus != target.FocusHandle)
                {
                    return new ActivationResult(false, "目标输入焦点与捕获时不一致。");
                }
            }
        }

        return IsForeground(target.WindowHandle)
            ? new ActivationResult(true, string.Empty)
            : new ActivationResult(false, "目标窗口未能保持前台。");
    }

    /// <summary>
    /// 等待用户松开热键修饰键，避免目标应用收到 Ctrl+Alt+Space+V 这样的组合。
    /// GetAsyncKeyState 读取物理键状态，超时后返回 false。
    /// </summary>
    public static async Task<bool> WaitForModifiersReleasedAsync(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (AreModifierKeysDown())
        {
            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            await Task.Delay(20);
        }

        return true;
    }

    public static bool AreModifierKeysDown()
    {
        foreach (var virtualKey in ModifierVirtualKeys)
        {
            if ((NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static NativeMethods.GUITHREADINFO? GetThreadInfo(uint threadId)
    {
        if (threadId == 0)
        {
            return null;
        }

        var info = new NativeMethods.GUITHREADINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>()
        };

        return NativeMethods.GetGUIThreadInfo(threadId, ref info) ? info : null;
    }

    /// <summary>
    /// rcCaret 是 hwndCaret 客户区坐标。优先用 Per-Monitor DPI 感知的转换，
    /// 失败时退回普通的 ClientToScreen。返回值是物理屏幕坐标。
    /// </summary>
    private static PhysicalRect? TryGetWin32Caret(NativeMethods.GUITHREADINFO info)
    {
        if (info.hwndCaret == IntPtr.Zero)
        {
            return null;
        }

        var caret = info.rcCaret;

        if (caret.Bottom <= caret.Top)
        {
            return null;
        }

        var topLeft = new NativeMethods.POINT(caret.Left, caret.Top);
        var bottomRight = new NativeMethods.POINT(caret.Right, caret.Bottom);

        if (!TryConvertClientPoint(info.hwndCaret, ref topLeft)
            || !TryConvertClientPoint(info.hwndCaret, ref bottomRight))
        {
            return null;
        }

        // 细光标的宽度可能是 0；保持至少 1 像素，避免后续 isValid 判断把它丢掉。
        var right = Math.Max(bottomRight.X, topLeft.X + 1);
        var bottom = Math.Max(bottomRight.Y, topLeft.Y + 1);
        return new PhysicalRect(topLeft.X, topLeft.Y, right, bottom);
    }

    private static bool TryConvertClientPoint(IntPtr windowHandle, ref NativeMethods.POINT point)
    {
        var converted = point;

        if (NativeMethods.LogicalToPhysicalPointForPerMonitorDPI(windowHandle, ref converted))
        {
            point = converted;
            return true;
        }

        converted = point;
        if (NativeMethods.ClientToScreen(windowHandle, ref converted))
        {
            point = converted;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 经典 Win32 Edit 密码框可以通过窗口样式识别。UIA 的密码框会在
    /// CaretLocator 中单独识别。
    /// </summary>
    private static bool IsClassicPasswordField(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        var className = new StringBuilder(256);

        if (NativeMethods.GetClassName(windowHandle, className, className.Capacity) <= 0
            || !string.Equals(className.ToString(), "Edit", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var style = NativeMethods.GetWindowLong(windowHandle, NativeMethods.GWL_STYLE);
        return (style & NativeMethods.ES_PASSWORD) != 0;
    }

    private static void TrySetWin32Focus(uint targetThreadId, IntPtr focusHandle)
    {
        if (focusHandle == IntPtr.Zero || !NativeMethods.IsWindow(focusHandle))
        {
            return;
        }

        var currentThread = NativeMethods.GetCurrentThreadId();
        var attached = NativeMethods.AttachThreadInput(currentThread, targetThreadId, true);

        try
        {
            NativeMethods.SetFocus(focusHandle);
        }
        catch
        {
            // SetFocus 不抛异常，但保留保护；焦点验证会决定下一步。
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(currentThread, targetThreadId, false);
            }
        }
    }

    private static async Task<bool> TrySetUiaFocusAsync(
        System.Windows.Automation.AutomationElement focusElement,
        TimeSpan timeout)
    {
        var focusTask = Task.Run(() =>
        {
            try
            {
                focusElement.SetFocus();
                return true;
            }
            catch
            {
                return false;
            }
        });

        var completed = await Task.WhenAny(focusTask, Task.Delay(timeout));

        if (completed != focusTask)
        {
            // UIA 调用可能永远不返回；不要等待它，也不要让未观察的异常影响进程。
            _ = focusTask.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return false;
        }

        var success = await focusTask;

        if (success)
        {
            // 给目标应用一点时间处理 WM_SETFOCUS；随后由调用方检查前台。
            await Task.Delay(50);
        }

        return success;
    }

    private static string GetProcessName(uint processId)
    {
        if (processId == 0)
        {
            return "系统界面";
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return $"进程 {processId}";
        }
    }
}
