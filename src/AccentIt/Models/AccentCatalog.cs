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
        IEnumerable<SpecialCompositionRule>? specialRules = null)
    {
        SchemaVersion = schemaVersion;
        Positions = positions.ToArray();
        Marks = marks.ToArray();
        CompositionMode = compositionMode;
        SpecialRules = specialRules?.ToArray() ?? Array.Empty<SpecialCompositionRule>();
        _marksById = Marks.ToDictionary(mark => mark.Id, StringComparer.Ordinal);
    }

    public int SchemaVersion { get; }

    public IReadOnlyList<AccentPosition> Positions { get; }

    public IReadOnlyList<AccentMark> Marks { get; }

    public CompositionMode CompositionMode { get; }

    public IReadOnlyList<SpecialCompositionRule> SpecialRules { get; }

    public AccentMark? FindMark(string id) => _marksById.GetValueOrDefault(id);

    public IReadOnlyList<AccentMark> GetMarksForPosition(string positionId)
        => Marks
            .Where(mark => string.Equals(mark.PositionId, positionId, StringComparison.Ordinal))
            .ToArray();
}
