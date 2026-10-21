using System.Text;

namespace AccentIt.Models;

/// <summary>
/// 一个 Unicode 字母区块的范围。范围可能包含非字母字符，
/// 统计或展示时需要再按 <see cref="Rune.IsLetter(Rune)"/> 过滤。
/// </summary>
public sealed record LetterBlock(
    string Id,
    string DisplayName,
    Rune First,
    Rune Last);

/// <summary>
/// 一种抽象变形操作。它本身没有 Unicode 码点，只作为路径规则的一部分。
/// </summary>
public sealed record LetterOperation(
    string Id,
    string Name,
    string Group,
    string? Description = null,
    bool RequiresSecondLetter = false);

/// <summary>
/// 一条字母路径规则。当前支持两类：
/// 1. 组合符号例外映射：只使用 <see cref="MarkIds"/>；
/// 2. 变形路径：只使用 <see cref="OperationIds"/>。
/// 两类规则不能混在同一条规则里。
/// </summary>
public sealed class LetterPathRule
{
    public LetterPathRule(
        Rune baseLetter,
        Rune? secondLetter,
        IEnumerable<string> markIds,
        IEnumerable<string> operationIds,
        Rune result,
        string? note = null)
    {
        BaseLetter = baseLetter;
        SecondLetter = secondLetter;
        MarkIds = markIds.ToArray();
        OperationIds = operationIds.ToArray();
        Result = result;
        Note = note;
    }

    public Rune BaseLetter { get; }

    public Rune? SecondLetter { get; }

    public IReadOnlyList<string> MarkIds { get; }

    public IReadOnlyList<string> OperationIds { get; }

    public Rune Result { get; }

    public string? Note { get; }

    public bool IsMarkException => MarkIds.Count > 0;

    public bool IsOperationPath => OperationIds.Count > 0;

    public bool MatchesMarks(IReadOnlySet<string> selectedIds)
        => MarkIds.Count == selectedIds.Count && MarkIds.All(selectedIds.Contains);

    public bool MatchesOperations(IReadOnlySet<string> selectedIds)
        => OperationIds.Count == selectedIds.Count && OperationIds.All(selectedIds.Contains);
}

/// <summary>
/// 没有可复用组合路径、只能直接选择的字母。
/// </summary>
public sealed record OtherLetter(
    Rune Letter,
    string Name,
    string? BlockId,
    IReadOnlyList<string> Aliases);

public sealed class LetterCompositionCatalog
{
    private readonly Dictionary<string, LetterOperation> _operationsById;

    public LetterCompositionCatalog(
        IEnumerable<LetterBlock> blocks,
        IEnumerable<LetterOperation> operations,
        IEnumerable<LetterPathRule> rules,
        IEnumerable<OtherLetter> otherLetters)
    {
        Blocks = blocks.ToArray();
        Operations = operations.ToArray();
        Rules = rules.ToArray();
        OtherLetters = otherLetters.ToArray();
        _operationsById = Operations.ToDictionary(operation => operation.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<LetterBlock> Blocks { get; }

    public IReadOnlyList<LetterOperation> Operations { get; }

    public IReadOnlyList<LetterPathRule> Rules { get; }

    public IReadOnlyList<OtherLetter> OtherLetters { get; }

    public LetterOperation? FindOperation(string id) => _operationsById.GetValueOrDefault(id);

    public LetterBlock? FindBlock(string id)
        => Blocks.FirstOrDefault(block => string.Equals(block.Id, id, StringComparison.Ordinal));
}
