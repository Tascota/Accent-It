using System.IO;
using System.Text;
using AccentIt.Models;
using AccentIt.Services;

namespace AccentIt.Tests;

internal static class TestCatalog
{
    public static string DefaultCatalogPath
        => Path.Combine(AppContext.BaseDirectory, "Data", "accents.default.jsonc");

    public const string MinimalJson = """
        {
          // 注释和尾随逗号都应该被接受。
          "schemaVersion": 1,
          "positions": [
            { "id": "above", "displayName": "上方" },
            { "id": "below", "displayName": "下方" },
            { "id": "overlay", "displayName": "叠加" },
          ],
          "marks": [
            { "id": "acute", "codePoint": "U+0301", "name": "锐音符", "position": "above" },
            { "id": "circumflex", "codePoint": "U+0302", "name": "抑扬符", "position": "above" },
            { "id": "dot-below", "codePoint": "U+0323", "name": "下点符", "position": "below" },
            { "id": "overlay-long-slash", "codePoint": "U+0338", "name": "长斜线覆盖", "position": "overlay" },
          ],
          "composition": {
            "mode": "nfc",
            "rules": [],
          }
        }
        """;

    public static AccentCatalog LoadDefault()
    {
        var result = new AccentCatalogLoader().LoadFromFile(DefaultCatalogPath);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Issues));

        return result.Catalog!;
    }

    public static AccentCatalog CreateCatalog(
        CompositionMode compositionMode = CompositionMode.Nfc,
        IEnumerable<SpecialCompositionRule>? rules = null)
    {
        var positions = new[]
        {
            new AccentPosition("above", "上方"),
            new AccentPosition("below", "下方"),
            new AccentPosition("overlay", "叠加")
        };

        var marks = new[]
        {
            new AccentMark("acute", new Rune(0x0301), "锐音符", "above"),
            new AccentMark("circumflex", new Rune(0x0302), "抑扬符", "above"),
            new AccentMark("dot-below", new Rune(0x0323), "下点符", "below"),
            new AccentMark("overlay-long-slash", new Rune(0x0338), "长斜线覆盖", "overlay")
        };

        return new AccentCatalog(1, positions, marks, compositionMode, rules);
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "AccentIt.Tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // 测试清理失败不影响测试结果。
        }
        catch (UnauthorizedAccessException)
        {
            // 测试清理失败不影响测试结果。
        }
    }
}
