using System.IO;
using AccentIt.Models;
using AccentIt.Services;

namespace AccentIt.Tests;

public class ThemeStoreTests
{
    [Fact]
    public void MissingFileDefaultsToLightWithoutCreatingFile()
    {
        using var temp = new TemporaryDirectory();
        var path = CreatePath(temp);

        var theme = new ThemeStore(path).Load();

        Assert.Equal(AppTheme.Light, theme);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void SaveAndReloadDarkTheme()
    {
        using var temp = new TemporaryDirectory();
        var path = CreatePath(temp);
        var store = new ThemeStore(path);

        Assert.True(store.Save(AppTheme.Dark));
        Assert.True(File.Exists(path));
        Assert.Equal(AppTheme.Dark, new ThemeStore(path).Load());

        var json = File.ReadAllText(path);
        Assert.Contains("\"dark\"", json);
    }

    [Fact]
    public void SaveAndReloadLightTheme()
    {
        using var temp = new TemporaryDirectory();
        var path = CreatePath(temp);
        var store = new ThemeStore(path);

        Assert.True(store.Save(AppTheme.Dark));
        Assert.True(store.Save(AppTheme.Light));
        Assert.Equal(AppTheme.Light, new ThemeStore(path).Load());
    }

    [Theory]
    [InlineData("{ }")]
    [InlineData("{ \"theme\": \"sepia\" }")]
    [InlineData("not json")]
    [InlineData("")]
    public void InvalidFileFallsBackToLight(string json)
    {
        using var temp = new TemporaryDirectory();
        var path = CreatePath(temp);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);

        Assert.Equal(AppTheme.Light, new ThemeStore(path).Load());
    }

    [Theory]
    [InlineData("DARK", AppTheme.Dark)]
    [InlineData("Light", AppTheme.Light)]
    public void ThemeNameIsCaseInsensitive(string value, AppTheme expected)
    {
        var json = $"{{ \"theme\": \"{value}\" }}";

        Assert.True(ThemeStore.TryParse(json, out var theme));
        Assert.Equal(expected, theme);
    }

    [Fact]
    public void UnknownThemeValueIsRejected()
    {
        Assert.False(ThemeStore.TryParse("{ \"theme\": \"blue\" }", out var theme));
        Assert.Equal(AppTheme.Light, theme);
    }

    private static string CreatePath(TemporaryDirectory temp)
        => Path.Combine(temp.Path, "app", "Data", "theme.json");
}
