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
        Assert.False(result.UsedFallback);
        Assert.Equal(TestCatalog.DefaultCatalogPath, result.SourcePath);

        var catalog = result.Catalog!;
        Assert.Equal(1, catalog.SchemaVersion);
        Assert.Equal(3, catalog.Positions.Count);
        Assert.Equal(26, catalog.Marks.Count);
        Assert.Equal(CompositionMode.Nfc, catalog.CompositionMode);
        Assert.Empty(catalog.SpecialRules);
        Assert.Equal("锐音符", catalog.FindMark("acute")!.Name);
        Assert.Equal(9, catalog.GetMarksForPosition("below").Count);
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
}
