using System.Text;

namespace AccentIt.Models;

public enum CompositionMode
{
    Nfc,
    Nfd
}

public sealed record AccentPosition(string Id, string DisplayName);

public sealed record AccentMark(
    string Id,
    Rune Symbol,
    string Name,
    string PositionId,
    string? Description = null);

public sealed class SpecialCompositionRule
{
    public SpecialCompositionRule(
        Rune baseLetter,
        IEnumerable<string> markIds,
        Rune result,
        string? note = null)
    {
        BaseLetter = baseLetter;
        MarkIds = markIds.ToArray();
        Result = result;
        Note = note;
    }

    public Rune BaseLetter { get; }

    public IReadOnlyList<string> MarkIds { get; }

    public Rune Result { get; }

    public string? Note { get; }
}

public sealed class AccentCatalog
{
    private readonly Dictionary<string, AccentMark> _marksById;

    public AccentCatalog(
        int schemaVersion,
        IEnumerable<AccentPosition> positions,
        IEnumerable<AccentMark> marks,
        CompositionMode compositionMode,
        IEnumerable<SpecialCompositionRule>? specialRules = null,
        LetterCompositionCatalog? letterComposition = null)
    {
        SchemaVersion = schemaVersion;
        Positions = positions.ToArray();
        Marks = marks.ToArray();
        CompositionMode = compositionMode;
        SpecialRules = specialRules?.ToArray() ?? Array.Empty<SpecialCompositionRule>();
        LetterComposition = letterComposition;
        _marksById = Marks.ToDictionary(mark => mark.Id, StringComparer.Ordinal);
    }

    public int SchemaVersion { get; }

    public IReadOnlyList<AccentPosition> Positions { get; }

    public IReadOnlyList<AccentMark> Marks { get; }

    public CompositionMode CompositionMode { get; }

    public IReadOnlyList<SpecialCompositionRule> SpecialRules { get; }

    /// <summary>
    /// 字母变形路径与“其他字符”数据。配置没有 letterComposition 段时为 null，
    /// 界面只显示组合符号。
    /// </summary>
    public LetterCompositionCatalog? LetterComposition { get; }

    public AccentMark? FindMark(string id) => _marksById.GetValueOrDefault(id);

    public IReadOnlyList<AccentMark> GetMarksForPosition(string positionId)
        => Marks
            .Where(mark => string.Equals(mark.PositionId, positionId, StringComparison.Ordinal))
            .ToArray();

    public AccentCatalog WithLetterComposition(LetterCompositionCatalog letterComposition)
        => new(
            SchemaVersion,
            Positions,
            Marks,
            CompositionMode,
            SpecialRules,
            letterComposition);

    public AccentCatalog WithMarks(IEnumerable<AccentMark> marks)
        => new(
            SchemaVersion,
            Positions,
            marks,
            CompositionMode,
            SpecialRules,
            LetterComposition);
}
