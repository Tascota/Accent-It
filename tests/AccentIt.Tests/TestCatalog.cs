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

    public const string JsonWithLetterComposition = """
        {
          "schemaVersion": 1,
          "positions": [
            { "id": "above", "displayName": "上方" }
          ],
          "marks": [
            { "id": "acute", "codePoint": "U+0301", "name": "锐音符", "position": "above" },
            { "id": "double-grave", "codePoint": "U+030F", "name": "双重音符", "position": "above" },
            { "id": "overlay-long-slash", "codePoint": "U+0338", "name": "长斜线覆盖", "position": "above" }
          ],
          "composition": {
            "mode": "nfc",
            "rules": []
          },
          "letterComposition": {
            "blocks": [
              {
                "id": "ipa",
                "displayName": "国际音标扩展",
                "firstCodePoint": "U+0250",
                "lastCodePoint": "U+02AF"
              }
            ],
            "operations": [
              { "id": "turn", "name": "倒转", "group": "方向" },
              { "id": "ligature", "name": "合字", "group": "结构", "requiresSecondLetter": true }
            ],
            "rules": [
              { "base": "M", "operations": ["turn"], "resultCodePoint": "U+019C" },
              { "base": "i", "second": "j", "operations": ["ligature"], "resultCodePoint": "U+0133" },
              { "base": "o", "marks": ["overlay-long-slash"], "resultCodePoint": "U+00F8" }
            ],
            "otherLetters": [
              {
                "codePoint": "U+0259",
                "name": "LATIN SMALL LETTER SCHWA",
                "block": "ipa",
                "aliases": ["schwa"]
              }
            ]
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
        IEnumerable<SpecialCompositionRule>? rules = null,
        LetterCompositionCatalog? letterComposition = null)
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

        return new AccentCatalog(1, positions, marks, compositionMode, rules, letterComposition);
    }

    public static LetterCompositionCatalog CreateLetterComposition()
    {
        var blocks = new[]
        {
            new LetterBlock("ipa", "国际音标扩展", new Rune(0x0250), new Rune(0x02AF))
        };

        var operations = new[]
        {
            new LetterOperation("turn", "倒转", "方向"),
            new LetterOperation("long-leg", "长腿", "笔画"),
            new LetterOperation("open", "开口", "形态"),
            new LetterOperation("ligature", "合字", "结构", RequiresSecondLetter: true)
        };

        var rules = new[]
        {
            new LetterPathRule(
                new Rune('M'),
                null,
                Array.Empty<string>(),
                new[] { "turn" },
                new Rune(0x019C),
                "M + 倒转 → Ɯ"),
            new LetterPathRule(
                new Rune('m'),
                null,
                Array.Empty<string>(),
                new[] { "turn" },
                new Rune(0x026F),
                "m + 倒转 → ɯ"),
            new LetterPathRule(
                new Rune('m'),
                null,
                Array.Empty<string>(),
                new[] { "turn", "long-leg" },
                new Rune(0x0270),
                "m + 倒转 + 长腿 → ɰ"),
            new LetterPathRule(
                new Rune('o'),
                null,
                new[] { "overlay-long-slash" },
                Array.Empty<string>(),
                new Rune(0x00F8),
                "o + 长斜线 → ø"),
            new LetterPathRule(
                new Rune('o'),
                null,
                Array.Empty<string>(),
                new[] { "open" },
                new Rune(0x0254),
                "o + 开口 → ɔ"),
            new LetterPathRule(
                new Rune('i'),
                new Rune('j'),
                Array.Empty<string>(),
                new[] { "ligature" },
                new Rune(0x0133),
                "i + j + 合字 → ĳ")
        };

        return new LetterCompositionCatalog(
            blocks,
            operations,
            rules,
            Array.Empty<OtherLetter>());
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
