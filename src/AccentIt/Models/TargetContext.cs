using System.Windows.Automation;

namespace AccentIt.Models;

/// <summary>
/// 屏幕坐标使用“物理像素”，不是 WPF 的设备无关单位（DIP）。
/// 第 3 步的窗口定位全部按物理像素计算，避免 150% 缩放下的位置误差。
/// </summary>
public readonly record struct PhysicalPoint(int X, int Y);

public readonly record struct PhysicalSize(int Width, int Height);

/// <summary>
/// 物理像素屏幕矩形。Right/Bottom 与 Win32 RECT 一样是开区间边界。
/// </summary>
public readonly record struct PhysicalRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);

    public int Height => Math.Max(0, Bottom - Top);

    public bool IsValid => Right > Left && Bottom > Top;

    public PhysicalPoint Center => new((Left + Right) / 2, (Top + Bottom) / 2);

    public static PhysicalRect FromDoubles(double left, double top, double width, double height)
    {
        if (!double.IsFinite(left)
            || !double.IsFinite(top)
            || !double.IsFinite(width)
            || !double.IsFinite(height)
            || width <= 0
            || height <= 0)
        {
            return default;
        }

        var right = left + width;
        var bottom = top + height;

        if (left < int.MinValue || top < int.MinValue
            || right > int.MaxValue || bottom > int.MaxValue)
        {
            return default;
        }

        return new PhysicalRect(
            (int)Math.Round(left),
            (int)Math.Round(top),
            (int)Math.Round(right),
            (int)Math.Round(bottom));
    }
}

public enum CaretSource
{
    None,
    Win32Caret,
    UiaTextSelection,
    UiaFocusBounds
}

/// <summary>
/// 打开紧凑窗口前捕获的前台目标信息。它只保存窗口/进程/焦点句柄和位置，
/// 不保存目标应用中的文本内容。
/// </summary>
public sealed record TargetContext
{
    public IntPtr WindowHandle { get; init; }

    /// <summary>捕获时目标线程的键盘焦点子窗口；某些应用会返回零。</summary>
    public IntPtr FocusHandle { get; init; }

    public uint ProcessId { get; init; }

    public uint ThreadId { get; init; }

    public string ProcessName { get; init; } = "未知应用";

    public bool IsExternal { get; init; }

    /// <summary>经典 Win32 密码框，或 UI Automation 报告 IsPassword。</summary>
    public bool IsPasswordField { get; init; }

    /// <summary>系统光标或 UI Automation 文本选区的位置；用于窗口定位，不用于读取文本。</summary>
    public PhysicalRect? CaretRect { get; init; }

    /// <summary>UI Automation 焦点元素的外框，作为没有文本光标时的定位备用。</summary>
    public PhysicalRect? FocusBounds { get; init; }

    public CaretSource CaretSource { get; init; }

    /// <summary>
    /// UI Automation 的焦点元素。仅在需要恢复焦点或没有系统光标时才会存在。
    /// 这是一个跨进程 COM 对象，可能随时失效，使用时要捕获异常。
    /// </summary>
    public AutomationElement? AutomationFocusElement { get; init; }

    public bool HasUsableTarget => IsExternal && WindowHandle != IntPtr.Zero;

    public string DisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ProcessName))
            {
                return "未知应用";
            }

            return ProcessName.Length <= 60 ? ProcessName : ProcessName[..57] + "…";
        }
    }
}
