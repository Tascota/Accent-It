using System.IO;
using AccentIt.Models;
using AccentIt.Services;

namespace AccentIt.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void FirstLoadCreatesDefaultSettingsFile()
    {
        using var temp = new TemporaryDirectory();
        var path = CreatePath(temp);

        var store = new SettingsStore(path);
        var result = store.Load();

        Assert.True(result.IsSuccess);
        Assert.False(result.UsedFallback);
        Assert.True(result.CreatedDefaultFile);
        Assert.Equal(Path.GetFullPath(path), result.SourcePath);
        Assert.True(File.Exists(path));
        Assert.Equal(HotkeyGesture.Default, result.Settings!.Hotkey);

        var text = File.ReadAllText(path);
        Assert.Contains("schemaVersion", text);
        Assert.Contains("Ctrl+Alt+Space", text);
    }

    [Fact]
    public void ExistingFileIsNotOverwritten()
    {
        using var temp = new TemporaryDirectory();
        var path = CreatePath(temp);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        const string content = """
            {
              "schemaVersion": 1,
              "hotkey": "Ctrl+Shift+A"
            }
            """;

        File.WriteAllText(path, content);

        var store = new SettingsStore(path);
        var result = store.Load();

        Assert.True(result.IsSuccess);
        Assert.False(result.UsedFallback);
        Assert.False(result.CreatedDefaultFile);
        Assert.Equal("Ctrl+Shift+A", result.Settings!.Hotkey.ToString());
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void JsoncCommentsAndTrailingCommasAreAccepted()
    {
        using var temp = new TemporaryDirectory();
        var path = CreatePath(temp);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        const string content = """
            {
              // 设置文件允许注释和尾随逗号。
              "schemaVersion": 1,
              "hotkey": "Alt+F8",
            }
            """;

        File.WriteAllText(path, content);

        var result = new SettingsStore(path).Load();

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Issues));
        Assert.False(result.UsedFallback);
        Assert.Equal("Alt+F8", result.Settings!.Hotkey.ToString());
    }

    [Fact]
    public void InvalidJsonFallsBackToDefaultWithoutOverwriting()
    {
        using var temp = new TemporaryDirectory();
        var path = CreatePath(temp);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        const string content = "{ not json";
        File.WriteAllText(path, content);

        var result = new SettingsStore(path).Load();

        Assert.True(result.UsedFallback);
        Assert.Equal(HotkeyGesture.Default, result.Settings!.Hotkey);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("JSON"));
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("{ \"schemaVersion\": 2, \"hotkey\": \"Ctrl+Shift+A\" }", "schemaVersion")]
    [InlineData("{ \"schemaVersion\": 1, \"hotkey\": \"A\" }", "hotkey")]
    [InlineData("{ \"schemaVersion\": 1 }", "hotkey")]
    public void InvalidSettingsFallBackToDefault(string json, string expectedLocation)
    {
        using var temp = new TemporaryDirectory();
        var path = CreatePath(temp);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);

        var result = new SettingsStore(path).Load();

        Assert.True(result.UsedFallback);
        Assert.Equal(HotkeyGesture.Default, result.Settings!.Hotkey);
        Assert.Contains(result.Issues, issue => issue.Location == expectedLocation);
        Assert.Equal(json, File.ReadAllText(path));
    }

    private static string CreatePath(TemporaryDirectory temp)
        => Path.Combine(temp.Path, "app", "Data", "settings.jsonc");
}
