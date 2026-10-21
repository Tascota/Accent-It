using System.Globalization;

namespace AccentIt.Models;

// 数值与 Win32 的 MOD_* 常量保持一致，注册热键时可以直接转换。
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008
}

public sealed record HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey, string KeyName)
{
    public static HotkeyGesture Default { get; } = new(
        HotkeyModifiers.Control | HotkeyModifiers.Alt,
        0x20,
        "Space");

    public override string ToString()
    {
        var parts = new List<string>(5);

        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Win))
        {
            parts.Add("Win");
        }

        parts.Add(KeyName);
        return string.Join('+', parts);
    }

    /// <summary>
    /// 解析设置文件中的快捷键文本，例如 "Ctrl+Alt+Space"。
    /// 只接受一个主键，并且至少需要一个修饰键，避免误吞普通输入。
    /// </summary>
    public static bool TryParse(string? text, out HotkeyGesture? gesture, out string error)
    {
        gesture = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "快捷键不能为空。";
            return false;
        }

        var tokens = text.Split('+');

        if (tokens.Any(string.IsNullOrWhiteSpace))
        {
            error = "快捷键中包含空的组合项，请检查加号两侧。";
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        string? keyName = null;
        uint virtualKey = 0;

        foreach (var rawToken in tokens)
        {
            var token = rawToken.Trim();

            if (TryParseModifier(token, out var modifier))
            {
                if (modifiers.HasFlag(modifier))
                {
                    error = $"修饰键“{token}”重复。";
                    return false;
                }

                modifiers |= modifier;
                continue;
            }

            if (keyName is not null)
            {
                error = "快捷键只能包含一个主键。";
                return false;
            }

            if (!TryParseKey(token, out keyName, out virtualKey, out error))
            {
                return false;
            }
        }

        if (modifiers == HotkeyModifiers.None)
        {
            error = "快捷键至少需要一个修饰键（Ctrl、Alt、Shift 或 Win）。";
            return false;
        }

        if (keyName is null)
        {
            error = "快捷键缺少主键，例如 A、1、Space 或 F8。";
            return false;
        }

        gesture = new HotkeyGesture(modifiers, virtualKey, keyName);
        return true;
    }

    private static bool TryParseModifier(string token, out HotkeyModifiers modifier)
    {
        modifier = token.ToLowerInvariant() switch
        {
            "ctrl" or "control" => HotkeyModifiers.Control,
            "alt" => HotkeyModifiers.Alt,
            "shift" => HotkeyModifiers.Shift,
            "win" or "windows" => HotkeyModifiers.Win,
            _ => HotkeyModifiers.None
        };

        return modifier != HotkeyModifiers.None;
    }

    private static bool TryParseKey(
        string token,
        out string? keyName,
        out uint virtualKey,
        out string error)
    {
        keyName = null;
        virtualKey = 0;
        error = string.Empty;

        if (token.Length == 1 && char.IsAsciiLetter(token[0]))
        {
            var upper = char.ToUpperInvariant(token[0]);
            keyName = upper.ToString();
            virtualKey = upper;
            return true;
        }

        if (token.Length == 1 && char.IsAsciiDigit(token[0]))
        {
            keyName = token;
            virtualKey = (uint)(0x30 + (token[0] - '0'));
            return true;
        }

        if (string.Equals(token, "Space", StringComparison.OrdinalIgnoreCase))
        {
            keyName = "Space";
            virtualKey = 0x20;
            return true;
        }

        // F1–F24 的虚拟键码是连续的；F12 被系统调试器永久保留。
        if (token.Length >= 2
            && (token[0] is 'F' or 'f')
            && int.TryParse(token[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number is >= 1 and <= 24)
        {
            if (number == 12)
            {
                error = "F12 保留给系统调试器，请换一个主键。";
                return false;
            }

            keyName = $"F{number}";
            virtualKey = (uint)(0x70 + (number - 1));
            return true;
        }

        error = $"不支持的主键“{token}”。可用：字母、数字、Space、F1–F24（F12 除外）。";
        return false;
    }
}
