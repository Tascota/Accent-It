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

    [Fact]
    public void OperationPathProducesTransformedLetter()
    {
        var result = ComposePath("M", Array.Empty<string>(), new[] { "turn" });

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(CompositionKind.SpecialRule, result.Kind);
        Assert.Equal(FromCodePoints(0x019C), result.Output);
        Assert.NotNull(result.AppliedPathRule);
        Assert.Equal("M + 倒转 → Ɯ", result.AppliedPathRule!.Note);
    }

    [Fact]
    public void MultipleOperationsMatchRegardlessOfOrder()
    {
        var result = ComposePath("m", Array.Empty<string>(), new[] { "long-leg", "turn" });

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(FromCodePoints(0x0270), result.Output);
    }

    [Fact]
    public void OperationPathCanBeFollowedByCombiningMark()
    {
        var result = ComposePath("M", new[] { "acute" }, new[] { "turn" });

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(FromCodePoints(0x019C, 0x0301), result.Output);
        Assert.Equal(CompositionKind.NfcSequence, result.Kind);
        Assert.NotNull(result.AppliedPathRule);
    }

    [Fact]
    public void MissingOperationPathIsRejected()
    {
        var result = ComposePath("x", Array.Empty<string>(), new[] { "turn" });

        Assert.False(result.IsValid);
        Assert.Contains("没有这条组合路径", result.Error);
        Assert.Null(result.AppliedPathRule);
    }

    [Fact]
    public void UnknownOperationIdIsRejected()
    {
        var result = ComposePath("M", Array.Empty<string>(), new[] { "missing" });

        Assert.False(result.IsValid);
        Assert.Contains("不在当前配置中", result.Error);
    }

    [Fact]
    public void LigatureRequiresASecondLetter()
    {
        var result = ComposePath("i", Array.Empty<string>(), new[] { "ligature" });

        Assert.False(result.IsValid);
        Assert.Contains("需要第二个字母", result.Error);
    }

    [Fact]
    public void LigatureUsesSecondLetter()
    {
        var result = ComposePath(
            "i",
            Array.Empty<string>(),
            new[] { "ligature" },
            secondLetter: "j");

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(FromCodePoints(0x0133), result.Output);
        Assert.NotNull(result.AppliedPathRule);
        Assert.Equal(0x006A, result.AppliedPathRule!.SecondLetter?.Value);
    }

    [Fact]
    public void MarkExceptionPathAppliesOnlyInNfc()
    {
        var nfc = ComposePath(
            "o",
            new[] { "overlay-long-slash" },
            Array.Empty<string>());
        var nfd = ComposePath(
            "o",
            new[] { "overlay-long-slash" },
            Array.Empty<string>(),
            mode: CompositionMode.Nfd);

        Assert.True(nfc.IsValid, nfc.Error);
        Assert.Equal(FromCodePoints(0x00F8), nfc.Output);
        Assert.NotNull(nfc.AppliedMarkPathRule);

        Assert.True(nfd.IsValid, nfd.Error);
        Assert.Equal(FromCodePoints(0x006F, 0x0338), nfd.Output);
        Assert.Null(nfd.AppliedMarkPathRule);
    }

    [Fact]
    public void SecondLetterWithoutOperationPathIsRejected()
    {
        var result = ComposePath(
            "i",
            Array.Empty<string>(),
            Array.Empty<string>(),
            secondLetter: "j");

        Assert.False(result.IsValid);
        Assert.Contains("没有这条组合路径", result.Error);
    }

    [Fact]
    public void NfdModeStillAppliesOperationPath()
    {
        var result = ComposePath(
            "M",
            Array.Empty<string>(),
            new[] { "turn" },
            mode: CompositionMode.Nfd);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(FromCodePoints(0x019C), result.Output);
        Assert.NotNull(result.AppliedPathRule);
    }

    [Fact]
    public void DefaultCatalogContainsThePlannedExamples()
    {
        var turnedM = ComposeDefaultPath(
            "M",
            Array.Empty<string>(),
            new[] { "turn" });
        Assert.True(turnedM.IsValid, turnedM.Error);
        Assert.Equal(FromCodePoints(0x019C), turnedM.Output);

        var turnedMLongLeg = ComposeDefaultPath(
            "m",
            Array.Empty<string>(),
            new[] { "turn", "long-leg" });
        Assert.True(turnedMLongLeg.IsValid, turnedMLongLeg.Error);
        Assert.Equal(FromCodePoints(0x0270), turnedMLongLeg.Output);

        var ligature = ComposeDefaultPath(
            "i",
            Array.Empty<string>(),
            new[] { "ligature" },
            "j");
        Assert.True(ligature.IsValid, ligature.Error);
        Assert.Equal(FromCodePoints(0x0133), ligature.Output);

        var strokedO = ComposeDefaultPath(
            "o",
            new[] { "overlay-long-slash" },
            Array.Empty<string>());
        Assert.True(strokedO.IsValid, strokedO.Error);
        Assert.Equal(FromCodePoints(0x00F8), strokedO.Output);
        Assert.NotNull(strokedO.AppliedMarkPathRule);

        var strokedB = ComposeDefaultPath(
            "b",
            new[] { "overlay-short-stroke" },
            Array.Empty<string>());
        Assert.True(strokedB.IsValid, strokedB.Error);
        Assert.Equal(FromCodePoints(0x0180), strokedB.Output);
    }

    [Fact]
    public void DefaultCatalogContainsExpandedLetterFamilies()
    {
        var hook = ComposeDefaultPath(
            "B",
            Array.Empty<string>(),
            new[] { "hook" });
        Assert.True(hook.IsValid, hook.Error);
        Assert.Equal(FromCodePoints(0x0181), hook.Output);

        var leftHook = ComposeDefaultPath(
            "N",
            Array.Empty<string>(),
            new[] { "left-hook" });
        Assert.True(leftHook.IsValid, leftHook.Error);
        Assert.Equal(FromCodePoints(0x019D), leftHook.Output);

        var reversedEzh = ComposeDefaultPath(
            "Ʒ",
            Array.Empty<string>(),
            new[] { "reverse" });
        Assert.True(reversedEzh.IsValid, reversedEzh.Error);
        Assert.Equal(FromCodePoints(0x01B8), reversedEzh.Output);

        var hookedOpenE = ComposeDefaultPath(
            "e",
            Array.Empty<string>(),
            new[] { "reverse", "open", "hook" });
        Assert.True(hookedOpenE.IsValid, hookedOpenE.Error);
        Assert.Equal(FromCodePoints(0x025D), hookedOpenE.Output);

        var dzLigature = ComposeDefaultPath(
            "d",
            Array.Empty<string>(),
            new[] { "ligature" },
            "z");
        Assert.True(dzLigature.IsValid, dzLigature.Error);
        Assert.Equal(FromCodePoints(0x01F3), dzLigature.Output);

        var dzFallback = TestCatalog.LoadDefault().LetterComposition!.OtherLetters
            .Single(letter => letter.Letter.Value == 0x02A3);
        Assert.Equal("LATIN SMALL LETTER DZ DIGRAPH", dzFallback.Name);

        var smallCapitalOe = ComposeDefaultPath(
            "o",
            Array.Empty<string>(),
            new[] { "ligature", "small-capital" },
            "e");
        Assert.True(smallCapitalOe.IsValid, smallCapitalOe.Error);
        Assert.Equal(FromCodePoints(0x0276), smallCapitalOe.Output);

        var barredZ = ComposeDefaultPath(
            "Z",
            new[] { "overlay-short-stroke" },
            Array.Empty<string>());
        Assert.True(barredZ.IsValid, barredZ.Error);
        Assert.Equal(FromCodePoints(0x01B5), barredZ.Output);

        var strokedLambda = ComposeDefaultPath(
            "λ",
            new[] { "overlay-short-stroke" },
            Array.Empty<string>());
        Assert.True(strokedLambda.IsValid, strokedLambda.Error);
        Assert.Equal(FromCodePoints(0x019B), strokedLambda.Output);

        var hvLigature = ComposeDefaultPath(
            "h",
            Array.Empty<string>(),
            new[] { "ligature" },
            "v");
        Assert.True(hvLigature.IsValid, hvLigature.Error);
        Assert.Equal(FromCodePoints(0x0195), hvLigature.Output);
    }

    private CompositionResult ComposeDefaultPath(
        string baseText,
        IReadOnlyList<string> markIds,
        IReadOnlyList<string> operationIds,
        string? secondLetter = null)
    {
        Assert.True(
            UnicodeComposer.TryParseBaseLetter(baseText, out var baseLetter, out var error),
            error);

        return UnicodeComposer.Compose(
            baseLetter,
            markIds,
            _catalog,
            operationIds: operationIds,
            secondLetterText: secondLetter);
    }

    private static CompositionResult ComposePath(
        string baseText,
        IReadOnlyList<string> markIds,
        IReadOnlyList<string> operationIds,
        string? secondLetter = null,
        CompositionMode? mode = null)
    {
        Assert.True(
            UnicodeComposer.TryParseBaseLetter(baseText, out var baseLetter, out var error),
            error);

        var catalog = TestCatalog.CreateCatalog(
            mode ?? CompositionMode.Nfc,
            letterComposition: TestCatalog.CreateLetterComposition());

        return UnicodeComposer.Compose(
            baseLetter,
            markIds,
            catalog,
            mode,
            operationIds,
            secondLetter);
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
