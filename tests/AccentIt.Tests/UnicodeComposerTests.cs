using System.Text;
using AccentIt.Models;
using AccentIt.Services;

namespace AccentIt.Tests;

public class UnicodeComposerTests
{
    private readonly AccentCatalog _catalog = TestCatalog.LoadDefault();

    [Fact]
    public void SingleAccentBecomesPrecomposedCharacter()
    {
        var result = Compose("a", "acute");

        Assert.True(result.IsValid);
        Assert.Equal(CompositionKind.Precomposed, result.Kind);
        Assert.Equal(FromCodePoints(0x00E1), result.Output);
        Assert.Equal(FromCodePoints(0x0061, 0x0301), result.RawSequence);
        Assert.Equal(new[] { 0x00E1 }, result.CodePoints.ToArray());
    }

    [Fact]
    public void UppercaseBaseIsPreservedAndComposed()
    {
        var result = Compose("A", "grave");

        Assert.Equal(FromCodePoints(0x00C0), result.Output);
        Assert.Equal(CompositionKind.Precomposed, result.Kind);
    }

    [Fact]
    public void MultipleMarksCanComposeIntoOnePrecomposedCharacter()
    {
        var result = Compose("e", "circumflex", "acute");

        Assert.Equal(FromCodePoints(0x1EBF), result.Output);
        Assert.Equal(CompositionKind.Precomposed, result.Kind);
        Assert.Equal(new[] { 0x1EBF }, result.CodePoints.ToArray());
        Assert.Equal(FromCodePoints(0x0065, 0x0302, 0x0301), result.Nfd);
    }

    [Fact]
    public void NoCompletePrecompositionFallsBackToNfcSequence()
    {
        var result = Compose("x", "acute");

        Assert.Equal(FromCodePoints(0x0078, 0x0301), result.Output);
        Assert.Equal(CompositionKind.NfcSequence, result.Kind);
        Assert.Equal(new[] { 0x0078, 0x0301 }, result.CodePoints.ToArray());
    }

    [Fact]
    public void NfcModeCanProducePartialPrecomposition()
    {
        var result = Compose("a", "acute", "dot-below");

        Assert.Equal(FromCodePoints(0x1EA1, 0x0301), result.Output);
        Assert.Equal(CompositionKind.NfcSequence, result.Kind);
        Assert.Equal(new[] { 0x1EA1, 0x0301 }, result.CodePoints.ToArray());
        Assert.Equal(FromCodePoints(0x0061, 0x0301, 0x0323), result.RawSequence);
        Assert.Equal(FromCodePoints(0x1EA1, 0x0301), result.Nfc);
    }

    [Fact]
    public void NoMarksReturnsBaseOnly()
    {
        var result = Compose("a");

        Assert.Equal(CompositionKind.BaseOnly, result.Kind);
        Assert.Equal(FromCodePoints(0x0061), result.Output);
        Assert.Equal(FromCodePoints(0x0061), result.RawSequence);
        Assert.Equal(new[] { 0x0061 }, result.CodePoints.ToArray());
    }

    [Fact]
    public void NfdModeFullyDecomposesBaseAndMarks()
    {
        var catalog = TestCatalog.CreateCatalog(CompositionMode.Nfd);

        var result = UnicodeComposer.Compose(
            new Rune('a'),
            new[] { "acute", "dot-below" },
            catalog);

        Assert.Equal(CompositionKind.NfdSequence, result.Kind);
        Assert.Equal(FromCodePoints(0x0061, 0x0323, 0x0301), result.Output);
        Assert.Equal(new[] { 0x0061, 0x0323, 0x0301 }, result.CodePoints.ToArray());
        Assert.Equal(FromCodePoints(0x1EA1, 0x0301), result.Nfc);
        Assert.Equal(FromCodePoints(0x0061, 0x0323, 0x0301), result.Nfd);
    }

    [Fact]
    public void NfdModeKeepsEveryMarkDecomposedAndDoesNotApplySpecialRules()
    {
        var rule = new SpecialCompositionRule(
            new Rune('a'),
            new[] { "acute" },
            new Rune(0x00E0));

        var catalog = TestCatalog.CreateCatalog(
            CompositionMode.Nfd,
            new[] { rule });

        var result = UnicodeComposer.Compose(
            new Rune('a'),
            new[] { "acute" },
            catalog);

        Assert.Equal(CompositionKind.NfdSequence, result.Kind);
        Assert.Equal(FromCodePoints(0x0061, 0x0301), result.Output);
        Assert.Null(result.AppliedRule);
    }

    [Fact]
    public void CompositionModeOverrideCanForceNfd()
    {
        var result = UnicodeComposer.Compose(
            new Rune('e'),
            new[] { "circumflex" },
            _catalog,
            CompositionMode.Nfd);

        Assert.Equal(CompositionKind.NfdSequence, result.Kind);
        Assert.Equal(FromCodePoints(0x0065, 0x0302), result.Output);
    }

    [Fact]
    public void NfdModeDecomposesPrecomposedBaseWithoutMarks()
    {
        var catalog = TestCatalog.CreateCatalog(CompositionMode.Nfd);

        var result = UnicodeComposer.Compose(
            new Rune(0x00E9),
            Array.Empty<string>(),
            catalog);

        Assert.Equal(CompositionKind.BaseOnly, result.Kind);
        Assert.Equal(FromCodePoints(0x0065, 0x0301), result.Output);
    }

    [Fact]
    public void ExplicitRuleOverridesNfcAndPreservesRawSequence()
    {
        var rule = new SpecialCompositionRule(
            new Rune('a'),
            new[] { "acute" },
            new Rune(0x00E0),
            "explicit test mapping");

        var catalog = TestCatalog.CreateCatalog(rules: new[] { rule });

        var result = UnicodeComposer.Compose(
            new Rune('a'),
            new[] { "acute" },
            catalog);

        Assert.Equal(CompositionKind.SpecialRule, result.Kind);
        Assert.Equal(FromCodePoints(0x00E0), result.Output);
        Assert.Equal(FromCodePoints(0x0061, 0x0301), result.RawSequence);
        Assert.Same(rule, result.AppliedRule);
    }

    [Fact]
    public void ExplicitRuleMatchingIsIndependentOfSelectedMarkOrder()
    {
        var rule = new SpecialCompositionRule(
            new Rune('a'),
            new[] { "acute", "dot-below" },
            new Rune(0x1EA1));

        var catalog = TestCatalog.CreateCatalog(rules: new[] { rule });

        var result = UnicodeComposer.Compose(
            new Rune('a'),
            new[] { "dot-below", "acute" },
            catalog);

        Assert.Equal(CompositionKind.SpecialRule, result.Kind);
        Assert.Equal(FromCodePoints(0x1EA1), result.Output);
    }

    [Fact]
    public void NonBmpBaseLetterIsCountedAsOneScalar()
    {
        Assert.True(Rune.TryCreate(0x10400, out var baseLetter));

        var result = UnicodeComposer.Compose(
            baseLetter,
            new[] { "acute" },
            _catalog);

        Assert.Equal(FromCodePoints(0x10400, 0x0301), result.Output);
        Assert.Equal(new[] { 0x10400, 0x0301 }, result.CodePoints.ToArray());
        Assert.Equal(CompositionKind.NfcSequence, result.Kind);
    }

    [Fact]
    public void UnknownMarkIdIsRejectedWithoutThrowing()
    {
        var result = UnicodeComposer.Compose(
            new Rune('a'),
            new[] { "does-not-exist" },
            _catalog);

        Assert.False(result.IsValid);
        Assert.Contains("不在当前符号目录", result.Error);
    }

    [Fact]
    public void DuplicateMarkIdIsRejectedWithoutThrowing()
    {
        var result = Compose("a", "acute", "acute");

        Assert.False(result.IsValid);
        Assert.Contains("重复选择", result.Error);
    }

    [Fact]
    public void InvalidBaseInputIsRejected()
    {
        var invalidInputs = new[]
        {
            string.Empty,
            "ab",
            char.ConvertFromUtf32(0x0301)
        };

        foreach (var input in invalidInputs)
        {
            var parsed = UnicodeComposer.TryParseBaseLetter(input, out _, out var error);

            Assert.False(parsed);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }

    [Fact]
    public void NonBmpBaseInputParsesAsOneLetter()
    {
        var parsed = UnicodeComposer.TryParseBaseLetter(
            char.ConvertFromUtf32(0x10400),
            out var letter,
            out var error);

        Assert.True(parsed, error);
        Assert.Equal(0x10400, letter.Value);
    }

    private CompositionResult Compose(string baseText, params string[] markIds)
    {
        Assert.True(
            UnicodeComposer.TryParseBaseLetter(baseText, out var baseLetter, out var error),
            error);

        return UnicodeComposer.Compose(baseLetter, markIds, _catalog);
    }

    private static string FromCodePoints(params int[] codePoints)
        => string.Concat(codePoints.Select(char.ConvertFromUtf32));
}
