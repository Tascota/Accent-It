using System.Globalization;
using System.Text;
using AccentIt.Models;
using AccentIt.Services;

namespace AccentIt.Tests;

/// <summary>
/// 默认目录的覆盖检查：四个区块里的每个字母都必须至少满足一种输入方式：
/// 1. 由路径规则直接得到；
/// 2. 由“基础字母 + 组合符号”在 NFC 下得到；
/// 3. 出现在“其他字符”列表中。
/// </summary>
public class DefaultCatalogCoverageTests
{
    private readonly AccentCatalog _catalog = TestCatalog.LoadDefault();

    [Fact]
    public void EveryLetterInSupportedBlocksIsCovered()
    {
        var composition = _catalog.LetterComposition;
        Assert.NotNull(composition);

        var pathResults = composition!.Rules
            .Select(rule => rule.Result.Value)
            .ToHashSet();
        var fallbackLetters = composition.OtherLetters
            .Select(letter => letter.Letter.Value)
            .ToHashSet();
        var markIdsByCodePoint = _catalog.Marks
            .ToDictionary(mark => mark.Symbol.Value, mark => mark.Id);

        var missing = new List<string>();
        var pathCovered = 0;
        var markCovered = 0;
        var fallbackCovered = 0;
        var excluded = 0;

        // 以下字符已明确不提供输入方式，不计入覆盖率要求。
        var intentionallyUnsupported = new HashSet<int>
        {
            0x00AA, // FEMININE ORDINAL INDICATOR
            0x00B5, // MICRO SIGN
            0x00BA  // MASCULINE ORDINAL INDICATOR
        };

        foreach (var block in composition.Blocks)
        {
            for (var value = block.First.Value; value <= block.Last.Value; value++)
            {
                if (!Rune.TryCreate(value, out var letter) || !Rune.IsLetter(letter))
                {
                    continue;
                }

                if (intentionallyUnsupported.Contains(value))
                {
                    excluded++;
                    continue;
                }

                if (pathResults.Contains(value))
                {
                    pathCovered++;
                    continue;
                }

                if (fallbackLetters.Contains(value))
                {
                    fallbackCovered++;
                    continue;
                }

                if (TryComposeFromCanonicalDecomposition(letter, markIdsByCodePoint))
                {
                    markCovered++;
                    continue;
                }

                missing.Add($"U+{value:X4} {letter}");
            }
        }

        Assert.True(
            missing.Count == 0,
            "以下字母没有被路径、其他字符或组合符号覆盖：" + string.Join("、", missing));

        // 四个区块目前共有 497 个字母。这个数字随区块定义变化，
        // 断言它可以防止新增区块后忘记补覆盖数据。
        Assert.Equal(497, pathCovered + markCovered + fallbackCovered + excluded);

        // “其他字符”是兜底，不应该成为主要输入方式。
        Assert.True(
            pathCovered + markCovered > fallbackCovered,
            $"组合覆盖 {pathCovered + markCovered} 个，其他字符 {fallbackCovered} 个。");
    }

    [Fact]
    public void EveryFallbackLetterBelongsToADeclaredBlock()
    {
        var composition = _catalog.LetterComposition;
        Assert.NotNull(composition);

        Assert.All(
            composition!.OtherLetters,
            letter => Assert.False(string.IsNullOrWhiteSpace(letter.BlockId)));
    }

    private bool TryComposeFromCanonicalDecomposition(
        Rune letter,
        IReadOnlyDictionary<int, string> markIdsByCodePoint)
    {
        var nfd = letter.ToString().Normalize(NormalizationForm.FormD);
        var runes = nfd.EnumerateRunes().ToArray();

        if (runes.Length < 2 || !Rune.IsLetter(runes[0]))
        {
            return false;
        }

        var markIds = new List<string>(runes.Length - 1);

        foreach (var rune in runes.Skip(1))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(rune.Value);

            if (category is not (UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark))
            {
                return false;
            }

            if (!markIdsByCodePoint.TryGetValue(rune.Value, out var markId))
            {
                return false;
            }

            markIds.Add(markId);
        }

        var result = UnicodeComposer.Compose(runes[0], markIds, _catalog);

        return result.IsValid
               && result.CodePoints.Count == 1
               && result.CodePoints[0] == letter.Value;
    }
}
