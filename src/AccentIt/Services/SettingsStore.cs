using System.IO;
using System.Text;
using System.Text.Json;
using AccentIt.Models;

namespace AccentIt.Services;

public sealed class SettingsStore
{
    public const string FileName = "settings.jsonc";
    public const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    // 只在文件不存在时写入；之后绝不覆盖用户的修改。
    private const string DefaultJson = """
        {
          "schemaVersion": 1,

          // 全局快捷键：至少一个修饰键 + 一个主键。
          // 支持 Ctrl / Alt / Shift / Win，以及字母、数字、Space、F1-F24（F12 除外）。
          // 修改后重启 Accent It 生效。
          "hotkey": "Ctrl+Alt+Space"
        }
        """;

    public SettingsStore(string settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath))
        {
            throw new ArgumentException("设置文件路径不能为空。", nameof(settingsPath));
        }

        SettingsPath = Path.GetFullPath(settingsPath);
    }

    public string SettingsPath { get; }

    public static SettingsStore CreateDefault()
        => new(Path.Combine(AppContext.BaseDirectory, "Data", FileName));

    public SettingsLoadResult Load()
    {
        var issues = new List<SettingsIssue>();
        var createdDefaultFile = false;

        if (!File.Exists(SettingsPath))
        {
            createdDefaultFile = TryCreateDefaultFile(issues);
        }

        if (!File.Exists(SettingsPath))
        {
            return CreateFallback(issues, createdDefaultFile);
        }

        string json;

        try
        {
            json = File.ReadAllText(SettingsPath, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or ArgumentException)
        {
            issues.Add(new SettingsIssue(SettingsPath, $"无法读取设置文件：{ex.Message}"));
            return CreateFallback(issues, createdDefaultFile);
        }

        return Parse(json, createdDefaultFile);
    }

    private bool TryCreateDefaultFile(ICollection<SettingsIssue> issues)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(SettingsPath, DefaultJson, new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or ArgumentException)
        {
            if (File.Exists(SettingsPath))
            {
                return false;
            }

            issues.Add(new SettingsIssue(
                SettingsPath,
                $"无法创建默认设置文件：{ex.Message}"));
            return false;
        }
    }

    private SettingsLoadResult Parse(string json, bool createdDefaultFile)
    {
        SettingsFileDto? dto;

        try
        {
            dto = JsonSerializer.Deserialize<SettingsFileDto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return CreateFallback(
                new[]
                {
                    new SettingsIssue(
                        SettingsPath,
                        $"设置文件 JSON 格式错误：{FormatJsonException(ex)}")
                },
                createdDefaultFile);
        }

        if (dto is null)
        {
            return CreateFallback(
                new[] { new SettingsIssue(SettingsPath, "设置文件的根对象不能为空。") },
                createdDefaultFile);
        }

        var issues = new List<SettingsIssue>();

        if (dto.SchemaVersion != SupportedSchemaVersion)
        {
            issues.Add(new SettingsIssue(
                "schemaVersion",
                $"当前程序只支持 schemaVersion = {SupportedSchemaVersion}。"));
        }

        HotkeyGesture? hotkey = null;

        if (string.IsNullOrWhiteSpace(dto.Hotkey))
        {
            issues.Add(new SettingsIssue("hotkey", "快捷键不能为空。"));
        }
        else if (!HotkeyGesture.TryParse(dto.Hotkey, out hotkey, out var hotkeyError))
        {
            issues.Add(new SettingsIssue("hotkey", hotkeyError));
        }

        if (issues.Count > 0)
        {
            return CreateFallback(issues, createdDefaultFile);
        }

        return new SettingsLoadResult
        {
            Settings = new AppSettings(SupportedSchemaVersion, hotkey!),
            SourcePath = SettingsPath,
            CreatedDefaultFile = createdDefaultFile
        };
    }

    private SettingsLoadResult CreateFallback(
        IReadOnlyCollection<SettingsIssue> issues,
        bool createdDefaultFile)
        => new()
        {
            Settings = new AppSettings(SupportedSchemaVersion, HotkeyGesture.Default),
            Issues = issues.ToArray(),
            SourcePath = SettingsPath,
            UsedFallback = true,
            CreatedDefaultFile = createdDefaultFile
        };

    private static string FormatJsonException(JsonException exception)
    {
        if (exception.LineNumber is long line && exception.BytePositionInLine is long position)
        {
            return $"第 {line + 1} 行、第 {position + 1} 字节附近：{exception.Message}";
        }

        return exception.Message;
    }

    private sealed class SettingsFileDto
    {
        public int SchemaVersion { get; set; }

        public string? Hotkey { get; set; }
    }
}
