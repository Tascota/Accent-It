using System.IO;
using System.Text;
using System.Text.Json;
using AccentIt.Models;

namespace AccentIt.Services;

/// <summary>
/// 记住用户选择的界面主题。使用独立的 Data/theme.json，
/// 不写入 settings.jsonc 或 accents.default.jsonc。
/// 读写失败只回退到浅色主题，不影响程序其他功能。
/// </summary>
public sealed class ThemeStore
{
    public const string FileName = "theme.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    public ThemeStore(string themePath)
    {
        if (string.IsNullOrWhiteSpace(themePath))
        {
            throw new ArgumentException("主题文件路径不能为空。", nameof(themePath));
        }

        ThemePath = Path.GetFullPath(themePath);
    }

    public string ThemePath { get; }

    public static ThemeStore CreateDefault()
        => new(Path.Combine(AppContext.BaseDirectory, "Data", FileName));

    public AppTheme Load()
    {
        try
        {
            if (!File.Exists(ThemePath))
            {
                return AppTheme.Light;
            }

            var json = File.ReadAllText(ThemePath, Encoding.UTF8);
            return TryParse(json, out var theme) ? theme : AppTheme.Light;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or ArgumentException)
        {
            return AppTheme.Light;
        }
    }

    public bool Save(AppTheme theme)
    {
        try
        {
            var directory = Path.GetDirectoryName(ThemePath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(
                new ThemeFileDto { Theme = theme == AppTheme.Dark ? "dark" : "light" },
                JsonOptions);

            File.WriteAllText(ThemePath, json + Environment.NewLine, new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>解析主题文件内容；未知值或格式错误都回退到浅色。</summary>
    public static bool TryParse(string? json, out AppTheme theme)
    {
        theme = AppTheme.Light;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<ThemeFileDto>(json, JsonOptions);

            if (string.Equals(dto?.Theme, "dark", StringComparison.OrdinalIgnoreCase))
            {
                theme = AppTheme.Dark;
                return true;
            }

            if (string.Equals(dto?.Theme, "light", StringComparison.OrdinalIgnoreCase))
            {
                theme = AppTheme.Light;
                return true;
            }
        }
        catch (JsonException)
        {
            // 主题文件损坏时继续用浅色，不阻止程序启动。
        }

        return false;
    }

    private sealed class ThemeFileDto
    {
        public string? Theme { get; set; }
    }
}
