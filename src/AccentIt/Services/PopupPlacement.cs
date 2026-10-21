using AccentIt.Models;

namespace AccentIt.Services;

/// <summary>
/// 计算紧凑窗口的物理像素位置。定位优先级与计划一致：
/// Win32 光标 → UIA 文本选区 → UIA 焦点外框 → 鼠标位置 → 显示器工作区中央。
/// 纯计算部分独立成方法，便于单元测试。
/// </summary>
public static class PopupPlacement
{
    public const int Margin = 12;

    public static PhysicalPoint ComputePosition(
        TargetContext? target,
        int width,
        int height)
    {
        if (target?.CaretRect is { IsValid: true } caretRect)
        {
            return ComputeForAnchor(
                caretRect,
                GetWorkAreaForPoint(caretRect.Center),
                width,
                height);
        }

        if (target?.FocusBounds is { IsValid: true } focusBounds)
        {
            return ComputeForAnchor(
                focusBounds,
                GetWorkAreaForPoint(focusBounds.Center),
                width,
                height);
        }

        if (NativeMethods.GetCursorPos(out var cursor))
        {
            var cursorPoint = new PhysicalPoint(cursor.X, cursor.Y);
            return ComputeNearPoint(
                cursorPoint,
                GetWorkAreaForPoint(cursorPoint),
                width,
                height);
        }

        return ComputeCentered(GetWorkArea(target), width, height);
    }

    /// <summary>优先放在锚点下方；下方空间不足时放到上方，最后钳制在工作区内。</summary>
    public static PhysicalPoint ComputeForAnchor(
        PhysicalRect anchor,
        PhysicalRect workArea,
        int width,
        int height)
    {
        var x = anchor.Left;
        var y = anchor.Bottom + Margin;

        if (y + height + Margin > workArea.Bottom)
        {
            y = anchor.Top - height - Margin;
        }

        return Clamp(new PhysicalPoint(x, y), workArea, width, height);
    }

    public static PhysicalPoint ComputeNearPoint(
        PhysicalPoint point,
        PhysicalRect workArea,
        int width,
        int height)
    {
        var x = point.X + Margin;
        var y = point.Y + Margin;

        if (y + height + Margin > workArea.Bottom)
        {
            y = point.Y - height - Margin;
        }

        return Clamp(new PhysicalPoint(x, y), workArea, width, height);
    }

    public static PhysicalPoint ComputeCentered(
        PhysicalRect workArea,
        int width,
        int height)
    {
        var x = workArea.Left + (workArea.Width - width) / 2;
        var y = workArea.Top + (workArea.Height - height) / 2;
        return Clamp(new PhysicalPoint(x, y), workArea, width, height);
    }

    /// <summary>插入完成后把窗口移到当前显示器工作区右下角，避免遮住目标文本。</summary>
    public static PhysicalPoint ComputeCorner(
        TargetContext? target,
        int width,
        int height)
        => ComputeCorner(GetWorkArea(target), width, height);

    public static PhysicalPoint ComputeCorner(
        PhysicalRect workArea,
        int width,
        int height)
        => Clamp(
            new PhysicalPoint(
                workArea.Right - width - Margin,
                workArea.Bottom - height - Margin),
            workArea,
            width,
            height);

    public static PhysicalPoint Clamp(
        PhysicalPoint position,
        PhysicalRect workArea,
        int width,
        int height)
    {
        var minX = workArea.Left + Margin;
        var maxX = workArea.Right - width - Margin;

        if (maxX < minX)
        {
            maxX = minX;
        }

        var minY = workArea.Top + Margin;
        var maxY = workArea.Bottom - height - Margin;

        if (maxY < minY)
        {
            maxY = minY;
        }

        return new PhysicalPoint(
            Math.Min(Math.Max(position.X, minX), maxX),
            Math.Min(Math.Max(position.Y, minY), maxY));
    }

    public static PhysicalRect GetWorkArea(TargetContext? target)
    {
        if (target is null)
        {
            return GetPrimaryWorkArea();
        }

        if (target.CaretRect is { IsValid: true } caretRect)
        {
            return GetWorkAreaForPoint(caretRect.Center);
        }

        if (target.FocusBounds is { IsValid: true } focusBounds)
        {
            return GetWorkAreaForPoint(focusBounds.Center);
        }

        if (target.WindowHandle != IntPtr.Zero)
        {
            var monitor = NativeMethods.MonitorFromWindow(
                target.WindowHandle,
                NativeMethods.MONITOR_DEFAULTTONEAREST);

            if (TryGetMonitorWorkArea(monitor, out var workArea))
            {
                return workArea;
            }
        }

        return GetPrimaryWorkArea();
    }

    public static PhysicalRect GetWorkAreaForPoint(PhysicalPoint point)
    {
        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.POINT(point.X, point.Y),
            NativeMethods.MONITOR_DEFAULTTONEAREST);

        return TryGetMonitorWorkArea(monitor, out var workArea)
            ? workArea
            : GetPrimaryWorkArea();
    }

    public static PhysicalRect GetPrimaryWorkArea()
    {
        // MonitorFromWindow(NULL, MONITOR_DEFAULTTOPRIMARY) 返回主显示器句柄。
        var monitor = NativeMethods.MonitorFromWindow(
            IntPtr.Zero,
            NativeMethods.MONITOR_DEFAULTTOPRIMARY);

        if (TryGetMonitorWorkArea(monitor, out var workArea))
        {
            return workArea;
        }

        return new PhysicalRect(
            0,
            0,
            Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN)),
            Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN)));
    }

    public static PhysicalSize? GetWindowSize(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero
            || !NativeMethods.GetWindowRect(windowHandle, out var rect))
        {
            return null;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        return width > 0 && height > 0
            ? new PhysicalSize(width, height)
            : null;
    }

    public static bool MoveWindow(IntPtr windowHandle, PhysicalPoint position)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        // 使用物理像素定位，保留 WPF 计算好的窗口尺寸，并且不改变 Z 序/不激活窗口。
        return NativeMethods.SetWindowPos(
            windowHandle,
            IntPtr.Zero,
            position.X,
            position.Y,
            0,
            0,
            NativeMethods.SWP_NOSIZE
            | NativeMethods.SWP_NOZORDER
            | NativeMethods.SWP_NOACTIVATE);
    }

    private static bool TryGetMonitorWorkArea(IntPtr monitor, out PhysicalRect workArea)
    {
        workArea = default;

        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new NativeMethods.MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };

        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        workArea = new PhysicalRect(
            info.rcWork.Left,
            info.rcWork.Top,
            info.rcWork.Right,
            info.rcWork.Bottom);
        return workArea.IsValid;
    }
}
