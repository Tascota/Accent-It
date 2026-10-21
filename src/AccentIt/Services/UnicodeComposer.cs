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
            error = "请输入基础字母";
            return false;
        }

        var runes = text.EnumerateRunes().ToArray();

        if (runes.Length != 1)
        {
            error = "基础输入必须是单个字母";
            return false;
        }

        if (!Rune.IsLetter(runes[0]))
        {
            error = "基础输入必须是字母";
            return false;
        }

        letter = runes[0];
        return true;
    }

    public static CompositionResult Compose(
        Rune baseLetter,
        IReadOnlyList<string> markIds,
        AccentCatalog catalog,
        CompositionMode? modeOverride = null,
        IReadOnlyList<string>? operationIds = null,
        string? secondLetterText = null)
    {
        ArgumentNullException.ThrowIfNull(markIds);
        ArgumentNullException.ThrowIfNull(catalog);

        if (!Rune.IsLetter(baseLetter))
        {
            return CompositionResult.Failure("基础输入必须是字母");
        }

        var selectedMarkIds = new HashSet<string>(StringComparer.Ordinal);
        var marks = new List<AccentMark>(markIds.Count);

        foreach (var markId in markIds)
        {
            if (string.IsNullOrWhiteSpace(markId))
            {
                return CompositionResult.Failure("已选符号中存在空 id");
            }

            if (!selectedMarkIds.Add(markId))
            {
                return CompositionResult.Failure($"符号“{markId}”被重复选择");
            }

            var mark = catalog.FindMark(markId);

            if (mark is null)
            {
                return CompositionResult.Failure($"符号“{markId}”不在当前符号目录中");
            }

            marks.Add(mark);
        }

        var selectedOperationIds = new HashSet<string>(StringComparer.Ordinal);
        var operations = new List<LetterOperation>();

        if (operationIds is not null)
        {
            if (operationIds.Count > 0 && catalog.LetterComposition is null)
            {
                return CompositionResult.Failure("当前配置没有字母变形路径");
            }

            foreach (var operationId in operationIds)
            {
                if (string.IsNullOrWhiteSpace(operationId))
                {
                    return CompositionResult.Failure("已选变形中存在空 id");
                }

                if (!selectedOperationIds.Add(operationId))
                {
                    return CompositionResult.Failure($"变形“{operationId}”被重复选择");
                }

                var operation = catalog.LetterComposition?.FindOperation(operationId);

                if (operation is null)
                {
                    return CompositionResult.Failure($"变形“{operationId}”不在当前配置中");
                }

                operations.Add(operation);
            }
        }

        Rune? secondLetter = null;

        if (!string.IsNullOrWhiteSpace(secondLetterText))
        {
            if (!TryParseBaseLetter(secondLetterText.Trim(), out var parsedSecond, out var secondError))
            {
                return CompositionResult.Failure($"第二个字母无效：{secondError}");
            }

            secondLetter = parsedSecond;
        }

        if (operations.Count > 0)
        {
            if (operations.Any(operation => operation.RequiresSecondLetter) && secondLetter is null)
            {
                return CompositionResult.Failure("该变形需要第二个字母");
            }

            var pathRule = FindOperationPathRule(
                baseLetter,
                selectedOperationIds,
                secondLetter,
                catalog.LetterComposition);

            if (pathRule is null)
            {
                return CompositionResult.Failure("没有这条组合路径");
            }

            return ComposeWithOperationPath(pathRule, marks, catalog, modeOverride);
        }

        if (secondLetter is not null)
        {
            return CompositionResult.Failure("没有这条组合路径");
        }

        return ComposeWithoutOperationPath(
            baseLetter,
            selectedMarkIds,
            marks,
            catalog,
            modeOverride);
    }

    private static CompositionResult ComposeWithOperationPath(
        LetterPathRule pathRule,
        IReadOnlyList<AccentMark> marks,
        AccentCatalog catalog,
        CompositionMode? modeOverride)
    {
        var transformedBase = pathRule.Result;
        var rawSequence = transformedBase.ToString()
            + string.Concat(marks.Select(mark => mark.Symbol.ToString()));
        var nfc = rawSequence.Normalize(NormalizationForm.FormC);
        var mode = modeOverride ?? catalog.CompositionMode;

        if (mode == CompositionMode.Nfd)
        {
            return CompositionResult.Success(
                rawSequence.Normalize(NormalizationForm.FormD),
                rawSequence,
                nfc,
                CompositionKind.SpecialRule,
                appliedPathRule: pathRule);
        }

        if (marks.Count > 0)
        {
            var selectedMarkIds = new HashSet<string>(
                marks.Select(mark => mark.Id),
                StringComparer.Ordinal);
            var specialRule = FindSpecialRule(transformedBase, selectedMarkIds, catalog);

            if (specialRule is not null)
            {
                return CompositionResult.Success(
                    specialRule.Result.ToString(),
                    rawSequence,
                    nfc,
                    CompositionKind.SpecialRule,
                    appliedRule: specialRule,
                    appliedPathRule: pathRule);
            }

            var markPathRule = FindMarkPathRule(
                transformedBase,
                selectedMarkIds,
                catalog.LetterComposition);

            if (markPathRule is not null)
            {
                return CompositionResult.Success(
                    markPathRule.Result.ToString(),
                    rawSequence,
                    nfc,
                    CompositionKind.SpecialRule,
                    appliedPathRule: pathRule,
                    appliedMarkPathRule: markPathRule);
            }
        }

        if (marks.Count == 0)
        {
            return CompositionResult.Success(
                transformedBase.ToString().Normalize(NormalizationForm.FormC),
                rawSequence,
                nfc,
                CompositionKind.SpecialRule,
                appliedPathRule: pathRule);
        }

        var scalarCount = nfc.EnumerateRunes().Count();
        var kind = scalarCount == 1
            ? CompositionKind.Precomposed
            : CompositionKind.NfcSequence;

        return CompositionResult.Success(
            nfc,
            rawSequence,
            nfc,
            kind,
            appliedPathRule: pathRule);
    }

    private static CompositionResult ComposeWithoutOperationPath(
        Rune baseLetter,
        IReadOnlySet<string> selectedMarkIds,
        IReadOnlyList<AccentMark> marks,
        AccentCatalog catalog,
        CompositionMode? modeOverride)
    {
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
            var specialRule = FindSpecialRule(baseLetter, selectedMarkIds, catalog);

            if (specialRule is not null)
            {
                return CompositionResult.Success(
                    specialRule.Result.ToString(),
                    rawSequence,
                    nfc,
                    CompositionKind.SpecialRule,
                    specialRule);
            }

            var markPathRule = FindMarkPathRule(
                baseLetter,
                selectedMarkIds,
                catalog.LetterComposition);

            if (markPathRule is not null)
            {
                return CompositionResult.Success(
                    markPathRule.Result.ToString(),
                    rawSequence,
                    nfc,
                    CompositionKind.SpecialRule,
                    appliedMarkPathRule: markPathRule);
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

    private static LetterPathRule? FindOperationPathRule(
        Rune baseLetter,
        IReadOnlySet<string> selectedOperationIds,
        Rune? secondLetter,
        LetterCompositionCatalog? composition)
    {
        if (composition is null)
        {
            return null;
        }

        foreach (var rule in composition.Rules)
        {
            if (!rule.IsOperationPath
                || rule.BaseLetter.Value != baseLetter.Value
                || rule.SecondLetter?.Value != secondLetter?.Value)
            {
                continue;
            }

            if (rule.MatchesOperations(selectedOperationIds))
            {
                return rule;
            }
        }

        return null;
    }

    private static LetterPathRule? FindMarkPathRule(
        Rune baseLetter,
        IReadOnlySet<string> selectedMarkIds,
        LetterCompositionCatalog? composition)
    {
        if (composition is null)
        {
            return null;
        }

        foreach (var rule in composition.Rules)
        {
            if (!rule.IsMarkException
                || rule.BaseLetter.Value != baseLetter.Value)
            {
                continue;
            }

            if (rule.MatchesMarks(selectedMarkIds))
            {
                return rule;
            }
        }

        return null;
    }
}
