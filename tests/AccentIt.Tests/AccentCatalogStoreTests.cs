using System.IO;
using AccentIt.Services;

namespace AccentIt.Tests;

public class AccentCatalogStoreTests
{
    [Fact]
    public void LoadsDefaultCatalogWithoutCreatingUserCatalog()
    {
        using var temp = new TemporaryDirectory();
        var seedPath = CreateSeedPath(temp);

        Directory.CreateDirectory(Path.GetDirectoryName(seedPath)!);
        File.WriteAllText(seedPath, TestCatalog.MinimalJson);

        var store = new AccentCatalogStore(seedPath);
        var result = store.LoadEffectiveCatalog();

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Issues));
        Assert.Equal(seedPath, result.SourcePath);
        Assert.False(File.Exists(Path.Combine(temp.Path, "app", "Data", "accents.jsonc")));
    }

    [Fact]
    public void ExistingUserCatalogIsIgnored()
    {
        using var temp = new TemporaryDirectory();
        var seedPath = CreateSeedPath(temp);

        Directory.CreateDirectory(Path.GetDirectoryName(seedPath)!);
        File.WriteAllText(seedPath, TestCatalog.MinimalJson);

        var userPath = Path.Combine(temp.Path, "app", "Data", "accents.jsonc");
        Directory.CreateDirectory(Path.GetDirectoryName(userPath)!);
        var editedJson = TestCatalog.MinimalJson.Replace(
            "\"name\": \"锐音符\"",
            "\"name\": \"锐音符（已编辑）\"");
        File.WriteAllText(userPath, editedJson);

        var store = new AccentCatalogStore(seedPath);
        var result = store.LoadEffectiveCatalog();

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Issues));
        Assert.Equal("锐音符", result.Catalog!.FindMark("acute")!.Name);
        Assert.Equal(editedJson, File.ReadAllText(userPath));
    }

    [Fact]
    public void InvalidDefaultCatalogFailsWithoutFallback()
    {
        using var temp = new TemporaryDirectory();
        var seedPath = CreateSeedPath(temp);

        Directory.CreateDirectory(Path.GetDirectoryName(seedPath)!);
        File.WriteAllText(seedPath, "{ this is not valid json");

        var store = new AccentCatalogStore(seedPath);
        var result = store.LoadEffectiveCatalog();

        Assert.False(result.IsSuccess);
        Assert.Null(result.Catalog);
        Assert.Equal(seedPath, result.SourcePath);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("JSON 格式错误"));
    }

    [Fact]
    public void MissingDefaultCatalogFailsWithoutThrowing()
    {
        using var temp = new TemporaryDirectory();
        var store = new AccentCatalogStore(CreateSeedPath(temp));
        var result = store.LoadEffectiveCatalog();

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("找不到符号目录"));
    }

    private static string CreateSeedPath(TemporaryDirectory temp)
        => Path.Combine(temp.Path, "app", "Data", "accents.default.jsonc");
}
