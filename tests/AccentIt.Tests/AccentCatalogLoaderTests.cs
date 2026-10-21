using System.Text;
using AccentIt.Models;
using AccentIt.Services;

namespace AccentIt.Tests;

public class AccentCatalogLoaderTests
{
    private readonly AccentCatalogLoader _loader = new();

    [Fact]
    public void DefaultCatalogLoadsWithExpectedContent()
    {
        var result = _loader.LoadFromFile(TestCatalog.DefaultCatalogPath);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Issues));
        Assert.Equal(TestCatalog.DefaultCatalogPath, result.SourcePath);

        var catalog = result.Catalog!;
        Assert.Equal(1, catalog.SchemaVersion);
        Assert.Equal(3, catalog.Positions.Count);
        Assert.Equal(28, catalog.Marks.Count);
        Assert.Equal(CompositionMode.Nfc, catalog.CompositionMode);
        Assert.Empty(catalog.SpecialRules);
        Assert.Equal("锐音符", catalog.FindMark("acute")!.Name);
        Assert.Equal(9, catalog.GetMarksForPosition("below").Count);

        Assert.NotNull(catalog.LetterComposition);
        Assert.Equal(4, catalog.LetterComposition!.Blocks.Count);
        Assert.Contains(
            catalog.LetterComposition.Operations,
            operation => operation.Id == "turn");
        Assert.Contains(
            catalog.LetterComposition.Rules,
            rule => rule.BaseLetter.Value == 'M'
                    && rule.OperationIds.Contains("turn"));
        Assert.Contains(
            catalog.LetterComposition.Rules,
            rule => rule.BaseLetter.Value == 'o'
                    && rule.MarkIds.Contains("overlay-long-slash"));
        Assert.Contains(
            catalog.LetterComposition.OtherLetters,
            letter => letter.Letter.Value == 0x00F0);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreAccepted()
    {
        var result = _loader.LoadFromJson(TestCatalog.MinimalJson, "minimal.jsonc");

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Issues));
        Assert.Equal(3, result.Catalog!.Positions.Count);
        Assert.Equal(4, result.Catalog.Marks.Count);
    }

    [Fact]
    public void MalformedJsonReportsAParseError()
    {
        var result = _loader.LoadFromJson("{ \"schemaVersion\": 1,", "bad.jsonc");

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("JSON 格式错误"));
    }

    [Fact]
    public void NonCombiningCodePointIsRejected()
    {
        var json = TestCatalog.MinimalJson.Replace(
            "\"codePoint\": \"U+0301\"",
            "\"codePoint\": \"U+0041\"");

        var result = _loader.LoadFromJson(json);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("不是组合附加符号"));
    }

    [Fact]
    public void MarkWithUnknownPositionIsRejected()
    {
        var json = TestCatalog.MinimalJson.Replace(
            "\"position\": \"above\"",
            "\"position\": \"missing\"");

        var result = _loader.LoadFromJson(json);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("未在 positions 中定义"));
    }

    [Fact]
    public void DuplicateMarkIdIsRejected()
    {
        var json = TestCatalog.MinimalJson.Replace(
            "\"id\": \"circumflex\"",
            "\"id\": \"acute\"");

        var result = _loader.LoadFromJson(json);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("符号 id “acute” 重复"));
    }

    [Fact]
    public void UnknownCompositionModeIsRejected()
    {
        var json = TestCatalog.MinimalJson.Replace(
            "\"mode\": \"nfc\"",
            "\"mode\": \"nfkc\"");

        var result = _loader.LoadFromJson(json);

        Assert.False(result.IsSuccess);
        Assert.Contains(
            result.Issues,
            issue => issue.Location.Contains("composition.mode")
                     && issue.Message.Contains("模式必须"));
    }

    [Fact]
    public void ExplicitRuleIsParsed()
    {
        var json = TestCatalog.MinimalJson.Replace(
            "\"rules\": []",
            """
            "rules": [
              {
                "base": "o",
                "marks": ["overlay-long-slash"],
                "resultCodePoint": "U+00F8",
                "note": "explicit mapping"
              }
            ]
            """);

        var result = _loader.LoadFromJson(json);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Issues));
        var rule = Assert.Single(result.Catalog!.SpecialRules);
        Assert.Equal(new Rune('o'), rule.BaseLetter);
        Assert.Equal(new Rune(0x00F8), rule.Result);
        Assert.Equal(new[] { "overlay-long-slash" }, rule.MarkIds);
        Assert.Equal("explicit mapping", rule.Note);
    }

    [Fact]
    public void DuplicateExplicitRulesAreRejectedRegardlessOfMarkOrder()
    {
        var json = TestCatalog.MinimalJson.Replace(
            "\"rules\": []",
            """
            "rules": [
              {
                "base": "a",
                "marks": ["acute", "dot-below"],
                "resultCodePoint": "U+1EA1"
              },
              {
                "base": "a",
                "marks": ["dot-below", "acute"],
                "resultCodePoint": "U+1EA1"
              }
            ]
            """);

        var result = _loader.LoadFromJson(json);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("重复规则"));
    }

    [Fact]
    public void NfdOutputModeIsParsed()
    {
        var json = TestCatalog.MinimalJson.Replace(
            "\"mode\": \"nfc\"",
            "\"mode\": \"nfd\"");

        var result = _loader.LoadFromJson(json);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Issues));
        Assert.Equal(CompositionMode.Nfd, result.Catalog!.CompositionMode);
    }

    [Fact]
    public void LetterCompositionSectionIsParsed()
    {
        var result = _loader.LoadFromJson(
            TestCatalog.JsonWithLetterComposition,
            "paths.jsonc");

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Issues));
        var composition = result.Catalog!.LetterComposition;

        Assert.NotNull(composition);
        Assert.Single(composition!.Blocks);
        Assert.Equal(2, composition.Operations.Count);
        Assert.Equal(3, composition.Rules.Count);
        Assert.Single(composition.OtherLetters);

        var operationRule = composition.Rules.Single(
            rule => rule.OperationIds.Contains("turn"));
        Assert.Equal(new Rune(0x019C), operationRule.Result);
        Assert.Equal(new Rune('M'), operationRule.BaseLetter);

        var markRule = composition.Rules.Single(rule => rule.IsMarkException);
        Assert.Equal(new[] { "overlay-long-slash" }, markRule.MarkIds);
        Assert.Equal(new Rune(0x00F8), markRule.Result);

        Assert.Equal(0x0259, composition.OtherLetters[0].Letter.Value);
        Assert.Equal("ipa", composition.OtherLetters[0].BlockId);
    }

    [Fact]
    public void PathRuleCannotMixMarksAndOperations()
    {
        var json = TestCatalog.JsonWithLetterComposition.Replace(
            """{ "base": "M", "operations": ["turn"], "resultCodePoint": "U+019C" }""",
            """{ "base": "M", "marks": ["acute"], "operations": ["turn"], "resultCodePoint": "U+019C" }""");

        var result = _loader.LoadFromJson(json);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("不能同时使用"));
    }

    [Fact]
    public void PathRuleWithUnknownOperationIsRejected()
    {
        var json = TestCatalog.JsonWithLetterComposition.Replace(
            """{ "base": "M", "operations": ["turn"], "resultCodePoint": "U+019C" }""",
            """{ "base": "M", "operations": ["missing"], "resultCodePoint": "U+019C" }""");

        var result = _loader.LoadFromJson(json);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("未在 operations 中定义"));
    }

    [Fact]
    public void DuplicatePathRulesAreRejected()
    {
        const string rule = """{ "base": "M", "operations": ["turn"], "resultCodePoint": "U+019C" }""";
        var json = TestCatalog.JsonWithLetterComposition.Replace(
            rule,
            rule + ",\n" + rule);

        var result = _loader.LoadFromJson(json);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("重复路径"));
    }

    [Fact]
    public void OtherLetterAlreadyCoveredByPathIsRejected()
    {
        var json = TestCatalog.JsonWithLetterComposition.Replace(
            "\"codePoint\": \"U+0259\"",
            "\"codePoint\": \"U+019C\"");

        var result = _loader.LoadFromJson(json);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("已有组合路径"));
    }

    [Fact]
    public void OperationRequiringSecondLetterRejectsRuleWithoutSecond()
    {
        var json = TestCatalog.JsonWithLetterComposition.Replace(
            """{ "base": "i", "second": "j", "operations": ["ligature"], "resultCodePoint": "U+0133" }""",
            """{ "base": "i", "operations": ["ligature"], "resultCodePoint": "U+0133" }""");

        var result = _loader.LoadFromJson(json);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("需要第二个字母"));
    }

    [Fact]
    public void OtherLetterWithUnknownBlockIsRejected()
    {
        var json = TestCatalog.JsonWithLetterComposition.Replace(
            "\"block\": \"ipa\"",
            "\"block\": \"missing\"");

        var result = _loader.LoadFromJson(json);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("未在 blocks 中定义"));
    }
}
