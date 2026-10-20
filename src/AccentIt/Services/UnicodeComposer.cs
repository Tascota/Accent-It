using System.Text;
using AccentIt.Models;

namespace AccentIt.Services;

public static class UnicodeComposer
{
    public static bool TryParseBaseLetter(string? text, out Rune letter, out string error)
    {
        letter = default;
        error = string.Empty;

        if (string.IsNullOrEmpty(text))
        {
            error = "请输入一个基础字母。";
            return false;
        }

        var runes = text.EnumerateRunes().ToArray();

        if (runes.Length != 1)
        {
            error = "基础输入必须是单个 Unicode 字母。";
            return false;
        }

        if (!Rune.IsLetter(runes[0]))
        {
            error = "基础输入必须是一个 Unicode 字母。";
            return false;
        }

        letter = runes[0];
        return true;
    }

    public static CompositionResult Compose(
        Rune baseLetter,
        IReadOnlyList<string> markIds,
        AccentCatalog catalog,
        CompositionMode? modeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(markIds);
        ArgumentNullException.ThrowIfNull(catalog);

        if (!Rune.IsLetter(baseLetter))
        {
            return CompositionResult.Failure("基础输入必须是一个 Unicode 字母。");
        }

        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        var marks = new List<AccentMark>(markIds.Count);

        foreach (var markId in markIds)
        {
            if (string.IsNullOrWhiteSpace(markId))
            {
                return CompositionResult.Failure("已选符号中存在空 id。");
            }

            if (!selectedIds.Add(markId))
            {
                return CompositionResult.Failure($"符号“{markId}”被重复选择。");
            }

            var mark = catalog.FindMark(markId);

            if (mark is null)
            {
                return CompositionResult.Failure($"符号“{markId}”不在当前符号目录中。");
            }

            marks.Add(mark);
        }

        var baseText = baseLetter.ToString();
        var rawSequence = marks.Count == 0
            ? baseText
            : baseText + string.Concat(marks.Select(mark => mark.Symbol.ToString()));
        var nfc = rawSequence.Normalize(NormalizationForm.FormC);
        var mode = modeOverride ?? catalog.CompositionMode;

        if (mode == CompositionMode.Nfd)
        {
            return CompositionResult.Success(
                rawSequence.Normalize(NormalizationForm.FormD),
                rawSequence,
                nfc,
                marks.Count == 0 ? CompositionKind.BaseOnly : CompositionKind.NfdSequence);
        }

        if (marks.Count > 0)
        {
            var specialRule = FindSpecialRule(baseLetter, selectedIds, catalog);

            if (specialRule is not null)
            {
                return CompositionResult.Success(
                    specialRule.Result.ToString(),
                    rawSequence,
                    nfc,
                    CompositionKind.SpecialRule,
                    specialRule);
            }
        }

        if (marks.Count == 0)
        {
            return CompositionResult.Success(
                baseText,
                rawSequence,
                nfc,
                CompositionKind.BaseOnly);
        }

        var scalarCount = nfc.EnumerateRunes().Count();
        var kind = scalarCount == 1
            ? CompositionKind.Precomposed
            : CompositionKind.NfcSequence;

        return CompositionResult.Success(
            nfc,
            rawSequence,
            nfc,
            kind);
    }

    private static SpecialCompositionRule? FindSpecialRule(
        Rune baseLetter,
        IReadOnlySet<string> selectedIds,
        AccentCatalog catalog)
    {
        foreach (var rule in catalog.SpecialRules)
        {
            if (rule.BaseLetter.Value != baseLetter.Value
                || rule.MarkIds.Count != selectedIds.Count)
            {
                continue;
            }

            if (rule.MarkIds.All(selectedIds.Contains))
            {
                return rule;
            }
        }

        return null;
    }
}
