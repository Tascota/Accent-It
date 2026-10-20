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
            Add("composition.mode", "模式必须是 \"nfc\" 或 \"precomposed-or-raw\"。");
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
            rules);

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
    {
        letter = default;
        error = string.Empty;

        if (string.IsNullOrEmpty(text))
        {
            error = "必须提供一个基础字母。";
            return false;
        }

        var runes = text.EnumerateRunes().ToArray();

        if (runes.Length != 1)
        {
            error = "基础字母必须是单个 Unicode 字符。";
            return false;
        }

        if (!Rune.IsLetter(runes[0]))
        {
            error = "基础必须是一个字母。";
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
            case "precomposedorraw":
            case "precomposedorrawsequence":
                mode = CompositionMode.PrecomposedOrRawSequence;
                return true;
            default:
                return false;
        }
    }

    private static string MakeRuleKey(Rune baseLetter, IEnumerable<string> markIds)
        => $"{baseLetter.Value:X}|{string.Join('\u001F', markIds.OrderBy(id => id, StringComparer.Ordinal))}";

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
}
