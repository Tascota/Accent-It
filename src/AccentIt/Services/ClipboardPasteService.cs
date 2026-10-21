using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace AccentIt.Services;

/// <summary>
/// 对原剪贴板的 best-effort 备份。特殊/延迟渲染格式无法保证完整备份；
/// Data 为 null 且 WasEmpty 为 false 时，表示原内容非空但无法恢复。
/// </summary>
public sealed class ClipboardBackup
{
    public DataObject? Data { get; init; }

    public bool WasEmpty { get; init; }

    public string? Warning { get; init; }

    public bool IsUsable => WasEmpty || Data is not null;

    public bool HasData => Data is not null;
}

/// <summary>
/// 剪贴板写入、备份和恢复。所有方法都必须在 WPF 的 STA UI 线程调用。
/// </summary>
public static class ClipboardPasteService
{
    private static readonly string[] KnownFormats =
    {
        DataFormats.UnicodeText,
        DataFormats.Text,
        DataFormats.Html,
        DataFormats.Rtf,
        DataFormats.CommaSeparatedValue,
        DataFormats.FileDrop,
        DataFormats.Bitmap
    };

    public static ClipboardBackup Capture()
    {
        try
        {
            var source = Clipboard.GetDataObject();

            if (source is null)
            {
                return new ClipboardBackup { WasEmpty = true };
            }

            var formats = source.GetFormats(autoConvert: false);

            if (formats is null || formats.Length == 0)
            {
                return new ClipboardBackup { WasEmpty = true };
            }

            var backup = new DataObject();
            var copiedFormats = 0;
            var failedFormats = 0;

            foreach (var format in KnownFormats)
            {
                if (!formats.Contains(format, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var value = source.GetData(format, autoConvert: false);

                    if (value is null)
                    {
                        failedFormats++;
                        continue;
                    }

                    if (value is BitmapSource bitmap)
                    {
                        if (bitmap.CanFreeze)
                        {
                            bitmap.Freeze();
                        }
                    }
                    else if (value is string[] strings)
                    {
                        value = strings.ToArray();
                    }

                    backup.SetData(format, value);
                    copiedFormats++;
                }
                catch
                {
                    failedFormats++;
                }
            }

            if (copiedFormats == 0)
            {
                return new ClipboardBackup
                {
                    Warning = "原剪贴板包含无法备份的格式；关闭窗口时不会恢复原内容。"
                };
            }

            return new ClipboardBackup
            {
                Data = backup,
                Warning = failedFormats == 0
                    ? null
                    : $"原剪贴板有 {failedFormats} 种格式无法备份，关闭时可能无法完整恢复。"
            };
        }
        catch (Exception ex)
        {
            return new ClipboardBackup
            {
                Warning = $"原剪贴板无法读取（{ex.Message}）；关闭窗口时不会恢复原内容。"
            };
        }
    }

    /// <summary>
    /// 把结果写入剪贴板。剪贴板被其他程序临时占用时短暂重试，
    /// 总等待时间由 timeout 控制。
    /// </summary>
    public static bool TrySetResultText(
        string text,
        TimeSpan timeout,
        out DataObject? resultData,
        out string error)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        Exception? lastError = null;
        resultData = null;

        while (true)
        {
            try
            {
                var data = new DataObject();
                data.SetText(text);
                Clipboard.SetDataObject(data, copy: true);
                resultData = data;
                error = string.Empty;
                return true;
            }
            catch (ExternalException ex)
            {
                // CLIPBRD_E_CANT_OPEN 等临时占用会走这里，稍后重试。
                lastError = ex;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            if (Environment.TickCount64 >= deadline)
            {
                break;
            }

            Thread.Sleep(60);
        }

        error = lastError?.Message ?? "剪贴板被其他程序占用。";
        return false;
    }

    public static uint GetSequenceNumber()
        => NativeMethods.GetClipboardSequenceNumber();

    /// <summary>
    /// 剪贴板仍是我们刚写入的结果吗？序列号未变或 WPF 认为当前 DataObject
    /// 仍属于本进程，都算作未变化。若其他程序期间复制了新内容，则返回 false。
    /// </summary>
    public static bool IsResultStillCurrent(
        long sequenceNumber,
        DataObject? resultData)
    {
        if (sequenceNumber != 0
            && NativeMethods.GetClipboardSequenceNumber() == (uint)sequenceNumber)
        {
            return true;
        }

        if (resultData is null)
        {
            return false;
        }

        try
        {
            return Clipboard.IsCurrent(resultData);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 恢复插入前的剪贴板。初始为空时清空；恢复失败时保留当前结果，
    /// 不会为了“看起来成功”而静默清空。
    /// </summary>
    public static bool TryRestore(
        ClipboardBackup backup,
        TimeSpan timeout,
        out string error)
    {
        if (!backup.IsUsable)
        {
            error = "原剪贴板内容无法备份。";
            return false;
        }

        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        Exception? lastError = null;

        while (true)
        {
            try
            {
                if (backup.WasEmpty)
                {
                    Clipboard.Clear();
                }
                else if (backup.Data is { } data)
                {
                    Clipboard.SetDataObject(data, copy: true);
                }
                else
                {
                    error = "原剪贴板内容无法备份。";
                    return false;
                }

                error = string.Empty;
                return true;
            }
            catch (ExternalException ex)
            {
                lastError = ex;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            if (Environment.TickCount64 >= deadline)
            {
                break;
            }

            Thread.Sleep(60);
        }

        error = lastError?.Message ?? "剪贴板被其他程序占用。";
        return false;
    }

    /// <summary>
    /// 用 SendInput 发送 Ctrl+V。四个事件必须全部送出；部分失败时
    /// 主动抬起可能仍处于按下状态的 Ctrl/V，避免污染键盘状态。
    /// </summary>
    public static bool TrySendCtrlV(out string error)
    {
        var inputs = new[]
        {
            CreateKeyboardInput((ushort)NativeMethods.VK_CONTROL, keyUp: false),
            CreateKeyboardInput((ushort)NativeMethods.VK_V, keyUp: false),
            CreateKeyboardInput((ushort)NativeMethods.VK_V, keyUp: true),
            CreateKeyboardInput((ushort)NativeMethods.VK_CONTROL, keyUp: true)
        };

        var sent = NativeMethods.SendInput(
            (uint)inputs.Length,
            inputs,
            Marshal.SizeOf<NativeMethods.INPUT>());
        var win32Error = sent == (uint)inputs.Length
            ? 0
            : Marshal.GetLastWin32Error();

        if (sent == (uint)inputs.Length)
        {
            error = string.Empty;
            return true;
        }

        // 发送数量不足时不要留下按下的键。即使这些抬起事件也失败，
        // 用户松开物理键后系统状态会恢复。
        var releaseInputs = new[]
        {
            CreateKeyboardInput((ushort)NativeMethods.VK_V, keyUp: true),
            CreateKeyboardInput((ushort)NativeMethods.VK_CONTROL, keyUp: true)
        };
        NativeMethods.SendInput(
            (uint)releaseInputs.Length,
            releaseInputs,
            Marshal.SizeOf<NativeMethods.INPUT>());

        error = win32Error == 5
            ? "Windows 拒绝了模拟按键（目标可能以管理员权限运行）。"
            : $"只发送了 {sent}/{inputs.Length} 个键盘事件（错误代码 {win32Error}）。";
        return false;
    }

    private static NativeMethods.INPUT CreateKeyboardInput(ushort virtualKey, bool keyUp)
        => new()
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.INPUTUNION
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = virtualKey,
                    dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0
                }
            }
        };
}
