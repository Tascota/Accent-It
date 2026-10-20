using System.IO;
using AccentIt.Services;

namespace AccentIt.Tests;

public class AccentCatalogStoreTests
{
    [Fact]
    public void FirstLoadCreatesUserCatalogFromSeed()
    {
        using var temp = new TemporaryDirectory();
        var (seedPath, userPath) = CreatePaths(temp);

        Directory.CreateDirectory(Path.GetDirectoryName(seedPath)!);
        File.WriteAllText(seedPath, TestCatalog.MinimalJson);

        var store = new AccentCatalogStore(seedPath, userPath);
        var result = store.LoadEffectiveCatalog();

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Issues));
        Assert.False(result.UsedFallback);
        Assert.Equal(userPath, result.SourcePath);
        Assert.True(File.Exists(userPath));
        Assert.Equal(File.ReadAllText(seedPath), File.ReadAllText(userPath));
    }

    [Fact]
    public void ExistingUserCatalogIsNotOverwrittenByTheSeed()
    {
        using var temp = new TemporaryDirectory();
        var (seedPath, userPath) = CreatePaths(temp);

        Directory.CreateDirectory(Path.GetDirectoryName(seedPath)!);
        File.WriteAllText(seedPath, TestCatalog.MinimalJson);

        var store = new AccentCatalogStore(seedPath, userPath);
        Assert.True(store.LoadEffectiveCatalog().IsSuccess);

        var editedJson = TestCatalog.MinimalJson.Replace(
            "\"name\": \"锐音符\"",
            "\"name\": \"锐音符（已编辑）\"");

        File.WriteAllText(userPath, editedJson);

        var secondLoad = store.LoadEffectiveCatalog();

        Assert.True(secondLoad.IsSuccess, string.Join(Environment.NewLine, secondLoad.Issues));
        Assert.False(secondLoad.UsedFallback);
        Assert.Equal("锐音符（已编辑）", secondLoad.Catalog!.FindMark("acute")!.Name);
        Assert.Contains("已编辑", File.ReadAllText(userPath));
    }

    [Fact]
    public void InvalidUserCatalogFallsBackToSeed()
    {
        using var temp = new TemporaryDirectory();
        var (seedPath, userPath) = CreatePaths(temp);

        Directory.CreateDirectory(Path.GetDirectoryName(seedPath)!);
        File.WriteAllText(seedPath, TestCatalog.MinimalJson);
        Directory.CreateDirectory(Path.GetDirectoryName(userPath)!);
        File.WriteAllText(userPath, "{ this is not valid json");

        var store = new AccentCatalogStore(seedPath, userPath);
        var result = store.LoadEffectiveCatalog();

        Assert.True(result.IsSuccess);
        Assert.True(result.UsedFallback);
        Assert.Equal(seedPath, result.SourcePath);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("回退到程序自带的默认配置"));
    }

    [Fact]
    public void MissingDefaultCatalogFailsWithoutThrowing()
    {
        using var temp = new TemporaryDirectory();
        var (seedPath, userPath) = CreatePaths(temp);

        var store = new AccentCatalogStore(seedPath, userPath);
        var result = store.LoadEffectiveCatalog();

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("找不到程序自带的默认符号目录"));
    }

    private static (string SeedPath, string UserPath) CreatePaths(TemporaryDirectory temp)
    {
        var seedPath = Path.Combine(temp.Path, "seed", "accents.default.jsonc");
        var userPath = Path.Combine(temp.Path, "app", "Data", "accents.jsonc");
        return (seedPath, userPath);
    }
}
