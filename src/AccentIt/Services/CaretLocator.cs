using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using AccentIt.Models;

namespace AccentIt.Services;

public sealed record CaretLocationResult(
    PhysicalRect? CaretRect,
    PhysicalRect? FocusBounds,
    bool IsPasswordField,
    AutomationElement? FocusElement,
    string? Warning);

/// <summary>
/// 当目标应用没有可用的 Win32 系统光标时，用 UI Automation 的文本选区
/// 或焦点元素外框来估算插入位置。UIA 是跨进程同步调用，因此所有调用
/// 都放在工作线程，并由调用方设置约 250 ms 的总超时。
/// </summary>
public static class CaretLocator
{
    public static async Task<CaretLocationResult> LocateAsync(
        TargetContext target,
        TimeSpan timeout)
    {
        if (!target.IsExternal || target.WindowHandle == IntPtr.Zero)
        {
            return Empty("没有外部目标。");
        }

        var locateTask = Task.Run(() => LocateCore(target));
        var completed = await Task.WhenAny(locateTask, Task.Delay(timeout));

        if (completed != locateTask)
        {
            // 超时后不再等待 UIA；观察可能的异常，防止出现未观察任务异常。
            _ = locateTask.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return Empty("UI Automation 定位超时。");
        }

        try
        {
            return await locateTask;
        }
        catch (Exception ex)
        {
            return Empty($"UI Automation 定位失败：{ex.Message}");
        }
    }

    private static CaretLocationResult LocateCore(TargetContext target)
    {
        AutomationElement? focusedElement;

        try
        {
            focusedElement = AutomationElement.FocusedElement;
        }
        catch (Exception ex)
        {
            return Empty($"无法读取 UI Automation 焦点：{ex.Message}");
        }

        if (focusedElement is null)
        {
            return Empty("没有获取到 UI Automation 焦点元素。");
        }

        try
        {
            if (focusedElement.Current.ProcessId != (int)target.ProcessId)
            {
                return Empty("UI Automation 焦点已经切换到其他进程。");
            }

            // 先检查密码框。若为密码框，绝不读取选区、文本或内容。
            if (focusedElement.Current.IsPassword)
            {
                return new CaretLocationResult(
                    null,
                    null,
                    true,
                    focusedElement,
                    "目标是密码框，不会自动插入。");
            }
        }
        catch (Exception ex)
        {
            return Empty($"无法读取 UI Automation 元素信息：{ex.Message}");
        }

        var focusBounds = TryGetBoundingRectangle(focusedElement);
        PhysicalRect? caretRect = null;
        string? warning = null;

        try
        {
            if (focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject)
                && patternObject is TextPattern textPattern)
            {
                var selection = textPattern.GetSelection();

                foreach (var range in selection)
                {
                    // 非空选区直接用它；退化空选区通常返回空数组，
                    // 需要先扩展到当前字符，才能拿到鼠标附近的光标矩形。
                    var rectangles = range.GetBoundingRectangles();
                    caretRect = TryGetRectangle(rectangles);

                    if (caretRect is null)
                    {
                        try
                        {
                            range.ExpandToEnclosingUnit(TextUnit.Character);
                            caretRect = TryGetRectangle(range.GetBoundingRectangles());
                        }
                        catch (Exception ex)
                        {
                            warning = $"无法扩展文本选区：{ex.Message}";
                        }
                    }

                    if (caretRect is not null)
                    {
                        break;
                    }
                }
            }
            else
            {
                warning = "目标输入框不支持 UI Automation 文本选区。";
            }
        }
        catch (Exception ex)
        {
            warning = $"无法读取文本选区：{ex.Message}";
        }

        return new CaretLocationResult(
            caretRect,
            focusBounds,
            false,
            focusedElement,
            warning);
    }

    private static PhysicalRect? TryGetBoundingRectangle(AutomationElement element)
    {
        try
        {
            var rect = element.Current.BoundingRectangle;
            var result = PhysicalRect.FromDoubles(rect.X, rect.Y, rect.Width, rect.Height);
            return result.IsValid ? result : null;
        }
        catch
        {
            return null;
        }
    }

    private static PhysicalRect? TryGetRectangle(Rect[] rectangles)
    {
        // 多行选区会返回多个矩形。窗口只需要靠近选区起始处，
        // 因此取第一个有效矩形，避免把窗口放到整个选区的下方。
        foreach (var rectangle in rectangles)
        {
            var rect = PhysicalRect.FromDoubles(
                rectangle.X,
                rectangle.Y,
                rectangle.Width,
                rectangle.Height);

            if (rect.IsValid)
            {
                return rect;
            }
        }

        return null;
    }

    private static CaretLocationResult Empty(string warning)
        => new(null, null, false, null, warning);
}
