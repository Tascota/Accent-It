using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AccentIt.Models;

namespace AccentIt.Services;

public sealed class AccentCatalogLoader
{
    public const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public CatalogLoadResult LoadFromFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            return LoadFromJson(json, path);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or ArgumentException)
        {
            return Failure(path, new CatalogIssue(path, $"无法读取配置文件：{ex.Message}"));
        }
    }

    public CatalogLoadResult LoadFromJson(string json, string? sourcePath = null)
    {
        CatalogFileDto? dto;

        try
        {
            dto = JsonSerializer.Deserialize<CatalogFileDto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Failure(
                sourcePath,
                new CatalogIssue(sourcePath ?? "配置文件", $"JSON 格式错误：{FormatJsonException(ex)}"));
        }

        if (dto is null)
        {
            return Failure(
                sourcePath,
                new CatalogIssue(sourcePath ?? "配置文件", "配置文件的根对象不能为空。"));
        }

        var issues = new List<CatalogIssue>();

        void Add(string location, string message)
            => issues.Add(new CatalogIssue(location, message));

        if (dto.SchemaVersion != SupportedSchemaVersion)
        {
            Add("schemaVersion", $"当前程序只支持 schemaVersion = {SupportedSchemaVersion}。");
        }

        var positions = new List<AccentPosition>();
        var positionIds = new HashSet<string>(StringComparer.Ordinal);

        if (dto.Positions is null || dto.Positions.Count == 0)
        {
            Add("positions", "至少需要一个位置分组。");
        }
        else
        {
            for (var i = 0; i < dto.Positions.Count; i++)
            {
                var item = dto.Positions[i];
                var location = $"positions[{i}]";

                if (item is null)
                {
                    Add(location, "位置条目不能为 null。");
                    continue;
                }

                var id = item.Id?.Trim();
                var displayName = item.DisplayName?.Trim();

                if (string.IsNullOrWhiteSpace(id))
                {
                    Add($"{location}.id", "位置 id 不能为空。");
                    continue;
                }

                if (!IsValidId(id))
                {
                    Add($"{location}.id", "位置 id 只能包含 ASCII 字母、数字、-、_、.。");
                    continue;
                }

                if (!positionIds.Add(id))
                {
                    Add($"{location}.id", $"位置 id “{id}” 重复。");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(displayName))
                {
                    Add($"{location}.displayName", "位置显示名称不能为空。");
                    continue;
                }

                positions.Add(new AccentPosition(id, displayName));
            }
        }

        var marks = new List<AccentMark>();
        var markIds = new HashSet<string>(StringComparer.Ordinal);
        var markCodePoints = new HashSet<int>();

        if (dto.Marks is null || dto.Marks.Count == 0)
        {
            Add("marks", "至少需要一个组合附加符号。");
        }
        else
        {
            for (var i = 0; i < dto.Marks.Count; i++)
            {
                var item = dto.Marks[i];
                var location = $"marks[{i}]";

                if (item is null)
                {
                    Add(location, "符号条目不能为 null。");
                    continue;
                }

                var id = item.Id?.Trim();
                var name = item.Name?.Trim();
                var positionId = item.Position?.Trim();

                if (string.IsNullOrWhiteSpace(id))
                {
                    Add($"{location}.id", "符号 id 不能为空。");
                    continue;
                }

                if (!IsValidId(id))
                {
                    Add($"{location}.id", "符号 id 只能包含 ASCII 字母、数字、-、_、.。");
                    continue;
                }

                if (!markIds.Add(id))
                {
                    Add($"{location}.id", $"符号 id “{id}” 重复。");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    Add($"{location}.name", "符号名称不能为空。");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(positionId))
                {
                    Add($"{location}.position", "符号必须引用一个位置 id。");
                    continue;
                }

                if (!positionIds.Contains(positionId))
                {
                    Add($"{location}.position", $"位置 “{positionId}” 未在 positions 中定义。");
                    continue;
                }

                if (!TryParseCodePoint(item.CodePoint, out var symbol, out var codePointError))
                {
                    Add($"{location}.codePoint", codePointError);
                    continue;
                }

                if (!IsCombiningMark(symbol))
                {
                    Add($"{location}.codePoint", $"U+{symbol.Value:X4} 不是组合附加符号。");
                    continue;
                }

                if (!markCodePoints.Add(symbol.Value))
                {
                    Add($"{location}.codePoint", $"符号 U+{symbol.Value:X4} 重复。");
                    continue;
                }

                var description = string.IsNullOrWhiteSpace(item.Description)
                    ? null
                    : item.Description.Trim();

                marks.Add(new AccentMark(id, symbol, name, positionId, description));
            }
        }

        var compositionMode = CompositionMode.Nfc;

        if (dto.Composition is null)
        {
            Add("composition", "缺少 composition 配置。");
        }
        else if (!TryParseCompositionMode(dto.Composition.Mode, out compositionMode))
        {
            Add("composition.mode", "模式必须是 \"nfc\" 或 \"nfd\"。");
        }

        var rules = new List<SpecialCompositionRule>();
        var ruleKeys = new HashSet<string>(StringComparer.Ordinal);

        if (dto.Composition?.Rules is { Count: > 0 })
        {
            for (var i = 0; i < dto.Composition.Rules.Count; i++)
            {
                var item = dto.Composition.Rules[i];
                var location = $"composition.rules[{i}]";

                if (item is null)
                {
                    Add(location, "例外规则不能为 null。");
                    continue;
                }

                if (!TryParseSingleLetter(item.Base, out var baseLetter, out var baseError))
                {
                    Add($"{location}.base", baseError);
                    continue;
                }

                if (item.Marks is null || item.Marks.Count == 0)
                {
                    Add($"{location}.marks", "例外规则至少需要引用一个符号 id。");
                    continue;
                }

                var referencedIds = new List<string>();
                var seenIds = new HashSet<string>(StringComparer.Ordinal);
                var hasInvalidReference = false;

                for (var j = 0; j < item.Marks.Count; j++)
                {
                    var markId = item.Marks[j]?.Trim();

                    if (string.IsNullOrWhiteSpace(markId))
                    {
                        Add($"{location}.marks[{j}]", "符号 id 不能为空。");
                        hasInvalidReference = true;
                        continue;
                    }

                    if (!seenIds.Add(markId))
                    {
                        Add($"{location}.marks[{j}]", $"符号 id “{markId}” 重复。");
                        hasInvalidReference = true;
                        continue;
                    }

                    if (!markIds.Contains(markId))
                    {
                        Add($"{location}.marks[{j}]", $"符号 “{markId}” 未在 marks 中定义。");
                        hasInvalidReference = true;
                        continue;
                    }

                    referencedIds.Add(markId);
                }

                if (hasInvalidReference)
                {
                    continue;
                }

                if (!TryParseCodePoint(item.ResultCodePoint, out var result, out var resultError))
                {
                    Add($"{location}.resultCodePoint", resultError);
                    continue;
                }

                if (!Rune.IsLetter(result))
                {
                    Add($"{location}.resultCodePoint", $"U+{result.Value:X4} 不是字母，不能作为预组合结果。");
                    continue;
                }

                var ruleKey = MakeRuleKey(baseLetter, referencedIds);

                if (!ruleKeys.Add(ruleKey))
                {
                    Add(location, "存在基础字母和符号集合完全相同的重复规则。");
                    continue;
                }

                var note = string.IsNullOrWhiteSpace(item.Note) ? null : item.Note.Trim();
                rules.Add(new SpecialCompositionRule(baseLetter, referencedIds, result, note));
            }
        }

        LetterCompositionCatalog? letterComposition = null;

        if (dto.LetterComposition is not null)
        {
            var blocks = new List<LetterBlock>();
            var blockIds = new HashSet<string>(StringComparer.Ordinal);
            var blockRanges = new List<(int First, int Last, string Id)>();

            if (dto.LetterComposition.Blocks is { Count: > 0 })
            {
                for (var i = 0; i < dto.LetterComposition.Blocks.Count; i++)
                {
                    var item = dto.LetterComposition.Blocks[i];
                    var location = $"letterComposition.blocks[{i}]";

                    if (item is null)
                    {
                        Add(location, "区块条目不能为 null。");
                        continue;
                    }

                    var id = item.Id?.Trim();
                    var displayName = item.DisplayName?.Trim();

                    if (string.IsNullOrWhiteSpace(id))
                    {
                        Add($"{location}.id", "区块 id 不能为空。");
                        continue;
                    }

                    if (!IsValidId(id))
                    {
                        Add($"{location}.id", "区块 id 只能包含 ASCII 字母、数字、-、_、.。");
                        continue;
                    }

                    if (!blockIds.Add(id))
                    {
                        Add($"{location}.id", $"区块 id “{id}” 重复。");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        Add($"{location}.displayName", "区块显示名称不能为空。");
                        continue;
                    }

                    if (!TryParseCodePoint(item.FirstCodePoint, out var first, out var firstError))
                    {
                        Add($"{location}.firstCodePoint", firstError);
                        continue;
                    }

                    if (!TryParseCodePoint(item.LastCodePoint, out var last, out var lastError))
                    {
                        Add($"{location}.lastCodePoint", lastError);
                        continue;
                    }

                    if (first.Value > last.Value)
                    {
                        Add(location, "区块的起始码点不能大于结束码点。");
                        continue;
                    }

                    var overlapping = blockRanges.FirstOrDefault(
                        range => first.Value <= range.Last && last.Value >= range.First);

                    if (overlapping.Id is not null)
                    {
                        Add(location, $"区块范围与“{overlapping.Id}”重叠。");
                        continue;
                    }

                    blockRanges.Add((first.Value, last.Value, id));
                    blocks.Add(new LetterBlock(id, displayName, first, last));
                }
            }

            var operations = new List<LetterOperation>();
            var operationIds = new HashSet<string>(StringComparer.Ordinal);

            if (dto.LetterComposition.Operations is { Count: > 0 })
            {
                for (var i = 0; i < dto.LetterComposition.Operations.Count; i++)
                {
                    var item = dto.LetterComposition.Operations[i];
                    var location = $"letterComposition.operations[{i}]";

                    if (item is null)
                    {
                        Add(location, "变形操作条目不能为 null。");
                        continue;
                    }

                    var id = item.Id?.Trim();
                    var name = item.Name?.Trim();

                    if (string.IsNullOrWhiteSpace(id))
                    {
                        Add($"{location}.id", "变形操作 id 不能为空。");
                        continue;
                    }

                    if (!IsValidId(id))
                    {
                        Add($"{location}.id", "变形操作 id 只能包含 ASCII 字母、数字、-、_、.。");
                        continue;
                    }

                    if (!operationIds.Add(id))
                    {
                        Add($"{location}.id", $"变形操作 id “{id}” 重复。");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        Add($"{location}.name", "变形操作名称不能为空。");
                        continue;
                    }

                    var group = string.IsNullOrWhiteSpace(item.Group)
                        ? "其他"
                        : item.Group.Trim();
                    var description = string.IsNullOrWhiteSpace(item.Description)
                        ? null
                        : item.Description.Trim();

                    operations.Add(new LetterOperation(
                        id,
                        name,
                        group,
                        description,
                        item.RequiresSecondLetter));
                }
            }

            var pathRules = new List<LetterPathRule>();
            var pathRuleKeys = new HashSet<string>(StringComparer.Ordinal);

            if (dto.LetterComposition.Rules is { Count: > 0 })
            {
                for (var i = 0; i < dto.LetterComposition.Rules.Count; i++)
                {
                    var item = dto.LetterComposition.Rules[i];
                    var location = $"letterComposition.rules[{i}]";

                    if (item is null)
                    {
                        Add(location, "字母路径规则不能为 null。");
                        continue;
                    }

                    if (!TryParseSingleLetter(item.Base, out var pathBase, out var baseError))
                    {
                        Add($"{location}.base", baseError);
                        continue;
                    }

                    var ruleMarkIds = new List<string>();
                    var ruleOperationIds = new List<string>();
                    var hasInvalidReference = false;

                    if (item.Marks is { Count: > 0 })
                    {
                        var seenMarkIds = new HashSet<string>(StringComparer.Ordinal);

                        for (var j = 0; j < item.Marks.Count; j++)
                        {
                            var markId = item.Marks[j]?.Trim();

                            if (string.IsNullOrWhiteSpace(markId))
                            {
                                Add($"{location}.marks[{j}]", "符号 id 不能为空。");
                                hasInvalidReference = true;
                                continue;
                            }

                            if (!seenMarkIds.Add(markId))
                            {
                                Add($"{location}.marks[{j}]", $"符号 id “{markId}” 重复。");
                                hasInvalidReference = true;
                                continue;
                            }

                            if (!markIds.Contains(markId))
                            {
                                Add($"{location}.marks[{j}]", $"符号 “{markId}” 未在 marks 中定义。");
                                hasInvalidReference = true;
                                continue;
                            }

                            ruleMarkIds.Add(markId);
                        }
                    }

                    if (item.Operations is { Count: > 0 })
                    {
                        var seenOperationIds = new HashSet<string>(StringComparer.Ordinal);

                        for (var j = 0; j < item.Operations.Count; j++)
                        {
                            var operationId = item.Operations[j]?.Trim();

                            if (string.IsNullOrWhiteSpace(operationId))
                            {
                                Add($"{location}.operations[{j}]", "变形操作 id 不能为空。");
                                hasInvalidReference = true;
                                continue;
                            }

                            if (!seenOperationIds.Add(operationId))
                            {
                                Add($"{location}.operations[{j}]", $"变形操作 id “{operationId}” 重复。");
                                hasInvalidReference = true;
                                continue;
                            }

                            if (!operationIds.Contains(operationId))
                            {
                                Add($"{location}.operations[{j}]", $"变形操作 “{operationId}” 未在 operations 中定义。");
                                hasInvalidReference = true;
                                continue;
                            }

                            ruleOperationIds.Add(operationId);
                        }
                    }

                    if (hasInvalidReference)
                    {
                        continue;
                    }

                    if (ruleMarkIds.Count == 0 && ruleOperationIds.Count == 0)
                    {
                        Add(location, "规则必须至少引用一个组合符号或一个变形操作。");
                        continue;
                    }

                    if (ruleMarkIds.Count > 0 && ruleOperationIds.Count > 0)
                    {
                        Add(location, "同一条规则不能同时使用 marks 和 operations。");
                        continue;
                    }

                    Rune? secondLetter = null;

                    if (!string.IsNullOrWhiteSpace(item.Second))
                    {
                        if (!TryParseLetter(item.Second, "第二个字母", out var parsedSecond, out var secondError))
                        {
                            Add($"{location}.second", secondError);
                            continue;
                        }

                        secondLetter = parsedSecond;
                    }

                    if (ruleMarkIds.Count > 0 && secondLetter is not null)
                    {
                        Add($"{location}.second", "组合符号例外规则不能指定第二个字母。");
                        continue;
                    }

                    if (ruleOperationIds.Count > 0)
                    {
                        var requiresSecond = ruleOperationIds
                            .Select(id => operations.FirstOrDefault(operation => operation.Id == id))
                            .Any(operation => operation?.RequiresSecondLetter == true);

                        if (requiresSecond && secondLetter is null)
                        {
                            Add($"{location}.second", "该规则使用的变形操作需要第二个字母。");
                            continue;
                        }
                    }

                    if (!TryParseCodePoint(item.ResultCodePoint, out var result, out var resultError))
                    {
                        Add($"{location}.resultCodePoint", resultError);
                        continue;
                    }

                    if (!Rune.IsLetter(result))
                    {
                        Add($"{location}.resultCodePoint", $"U+{result.Value:X4} 不是字母，不能作为路径结果。");
                        continue;
                    }

                    var ruleKey = MakePathRuleKey(pathBase, secondLetter, ruleMarkIds, ruleOperationIds);

                    if (!pathRuleKeys.Add(ruleKey))
                    {
                        Add(location, "存在基础字母、操作/符号集合和第二字母完全相同的重复路径。");
                        continue;
                    }

                    var note = string.IsNullOrWhiteSpace(item.Note) ? null : item.Note.Trim();
                    pathRules.Add(new LetterPathRule(
                        pathBase,
                        secondLetter,
                        ruleMarkIds,
                        ruleOperationIds,
                        result,
                        note));
                }
            }

            var otherLetters = new List<OtherLetter>();
            var otherLetterCodePoints = new HashSet<int>();
            var coveredResults = new HashSet<int>(rules.Select(rule => rule.Result.Value));

            foreach (var pathRule in pathRules)
            {
                coveredResults.Add(pathRule.Result.Value);
            }

            if (dto.LetterComposition.OtherLetters is { Count: > 0 })
            {
                for (var i = 0; i < dto.LetterComposition.OtherLetters.Count; i++)
                {
                    var item = dto.LetterComposition.OtherLetters[i];
                    var location = $"letterComposition.otherLetters[{i}]";

                    if (item is null)
                    {
                        Add(location, "其他字符条目不能为 null。");
                        continue;
                    }

                    if (!TryParseCodePoint(item.CodePoint, out var letter, out var codePointError))
                    {
                        Add($"{location}.codePoint", codePointError);
                        continue;
                    }

                    if (!Rune.IsLetter(letter))
                    {
                        Add($"{location}.codePoint", $"U+{letter.Value:X4} 不是字母。");
                        continue;
                    }

                    if (!otherLetterCodePoints.Add(letter.Value))
                    {
                        Add($"{location}.codePoint", $"字符 U+{letter.Value:X4} 重复。");
                        continue;
                    }

                    var name = item.Name?.Trim();

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        Add($"{location}.name", "其他字符名称不能为空。");
                        continue;
                    }

                    var blockId = string.IsNullOrWhiteSpace(item.Block)
                        ? null
                        : item.Block.Trim();

                    if (blockId is not null && !blockIds.Contains(blockId))
                    {
                        Add($"{location}.block", $"区块 “{blockId}” 未在 blocks 中定义。");
                        continue;
                    }

                    if (coveredResults.Contains(letter.Value))
                    {
                        Add(
                            location,
                            $"U+{letter.Value:X4} 已有组合路径，不应放入 otherLetters。");
                        continue;
                    }

                    var aliases = new List<string>();

                    if (item.Aliases is not null)
                    {
                        var seenAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        foreach (var aliasText in item.Aliases)
                        {
                            var alias = aliasText?.Trim();

                            if (string.IsNullOrWhiteSpace(alias))
                            {
                                continue;
                            }

                            if (seenAliases.Add(alias))
                            {
                                aliases.Add(alias);
                            }
                        }
                    }

                    otherLetters.Add(new OtherLetter(letter, name, blockId, aliases));
                }
            }

            letterComposition = new LetterCompositionCatalog(
                blocks,
                operations,
                pathRules,
                otherLetters);
        }

        if (issues.Count > 0)
        {
            return new CatalogLoadResult
            {
                Issues = issues,
                SourcePath = sourcePath
            };
        }

        var catalog = new AccentCatalog(
            dto.SchemaVersion,
            positions,
            marks,
            compositionMode,
            rules,
            letterComposition);

        return new CatalogLoadResult
        {
            Catalog = catalog,
            SourcePath = sourcePath
        };
    }

    private static bool TryParseCodePoint(string? text, out Rune rune, out string error)
    {
        rune = default;
        error = string.Empty;

        var value = text?.Trim();

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "码点不能为空，请使用 U+XXXX 形式。";
            return false;
        }

        if (value.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        if (value.Length is 0 or > 6
            || !int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var scalar))
        {
            error = "码点格式无效，请使用 U+XXXX 形式，例如 U+0301。";
            return false;
        }

        if (!Rune.TryCreate(scalar, out rune))
        {
            error = $"U+{scalar:X4} 不是有效的 Unicode 标量值。";
            return false;
        }

        return true;
    }

    private static bool TryParseSingleLetter(string? text, out Rune letter, out string error)
        => TryParseLetter(text, "基础字母", out letter, out error);

    private static bool TryParseLetter(string? text, string role, out Rune letter, out string error)
    {
        letter = default;
        error = string.Empty;

        if (string.IsNullOrEmpty(text))
        {
            error = $"必须提供一个{role}。";
            return false;
        }

        var runes = text.EnumerateRunes().ToArray();

        if (runes.Length != 1)
        {
            error = $"{role}必须是单个 Unicode 字符。";
            return false;
        }

        if (!Rune.IsLetter(runes[0]))
        {
            error = $"{role}必须是一个字母。";
            return false;
        }

        letter = runes[0];
        return true;
    }

    private static bool IsCombiningMark(Rune rune)
        => Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;

    private static bool IsValidId(string id)
        => id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');

    private static bool TryParseCompositionMode(string? value, out CompositionMode mode)
    {
        mode = CompositionMode.Nfc;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value
            .Trim()
            .Replace("-", string.Empty)
            .Replace("_", string.Empty)
            .Replace(" ", string.Empty)
            .ToLowerInvariant();

        switch (normalized)
        {
            case "nfc":
                mode = CompositionMode.Nfc;
                return true;
            case "nfd":
                mode = CompositionMode.Nfd;
                return true;
            default:
                return false;
        }
    }

    private static string MakeRuleKey(Rune baseLetter, IEnumerable<string> markIds)
        => $"{baseLetter.Value:X}|{string.Join('\u001F', markIds.OrderBy(id => id, StringComparer.Ordinal))}";

    private static string MakePathRuleKey(
        Rune baseLetter,
        Rune? secondLetter,
        IEnumerable<string> markIds,
        IEnumerable<string> operationIds)
    {
        var second = secondLetter?.Value.ToString("X", CultureInfo.InvariantCulture) ?? "-";
        var kindAndIds = markIds.Any()
            ? $"M|{string.Join('\u001F', markIds.OrderBy(id => id, StringComparer.Ordinal))}"
            : $"O|{string.Join('\u001F', operationIds.OrderBy(id => id, StringComparer.Ordinal))}";

        return $"{baseLetter.Value:X}|{second}|{kindAndIds}";
    }

    private static string FormatJsonException(JsonException exception)
    {
        if (exception.LineNumber is long line && exception.BytePositionInLine is long position)
        {
            return $"第 {line + 1} 行、第 {position + 1} 字节附近：{exception.Message}";
        }

        return exception.Message;
    }

    private static CatalogLoadResult Failure(string? sourcePath, params CatalogIssue[] issues)
        => new()
        {
            Issues = issues,
            SourcePath = sourcePath
        };

    private sealed class CatalogFileDto
    {
        public int SchemaVersion { get; set; }

        public List<PositionDto?>? Positions { get; set; }

        public List<MarkDto?>? Marks { get; set; }

        public CompositionDto? Composition { get; set; }

        public LetterCompositionDto? LetterComposition { get; set; }
    }

    private sealed class PositionDto
    {
        public string? Id { get; set; }

        public string? DisplayName { get; set; }
    }

    private sealed class MarkDto
    {
        public string? Id { get; set; }

        public string? CodePoint { get; set; }

        public string? Name { get; set; }

        public string? Position { get; set; }

        public string? Description { get; set; }
    }

    private sealed class CompositionDto
    {
        public string? Mode { get; set; }

        public List<RuleDto?>? Rules { get; set; }
    }

    private sealed class RuleDto
    {
        public string? Base { get; set; }

        public List<string?>? Marks { get; set; }

        public string? ResultCodePoint { get; set; }

        public string? Note { get; set; }
    }

    private sealed class LetterCompositionDto
    {
        public List<BlockDto?>? Blocks { get; set; }

        public List<OperationDto?>? Operations { get; set; }

        public List<PathRuleDto?>? Rules { get; set; }

        public List<OtherLetterDto?>? OtherLetters { get; set; }
    }

    private sealed class BlockDto
    {
        public string? Id { get; set; }

        public string? DisplayName { get; set; }

        public string? FirstCodePoint { get; set; }

        public string? LastCodePoint { get; set; }
    }

    private sealed class OperationDto
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Group { get; set; }

        public string? Description { get; set; }

        public bool RequiresSecondLetter { get; set; }
    }

    private sealed class PathRuleDto
    {
        public string? Base { get; set; }

        public string? Second { get; set; }

        public List<string?>? Marks { get; set; }

        public List<string?>? Operations { get; set; }

        public string? ResultCodePoint { get; set; }

        public string? Note { get; set; }
    }

    private sealed class OtherLetterDto
    {
        public string? CodePoint { get; set; }

        public string? Name { get; set; }

        public string? Block { get; set; }

        public List<string?>? Aliases { get; set; }
    }
}
