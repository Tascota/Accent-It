using System.Text;

namespace AccentIt.Models;

public enum CompositionKind
{
    Invalid,
    BaseOnly,
    Precomposed,
    NfcSequence,
    RawSequence,
    SpecialRule
}

public sealed class CompositionResult
{
    private CompositionResult(
        bool isValid,
        string? error,
        string output,
        string rawSequence,
        string nfc,
        string nfd,
        CompositionKind kind,
        SpecialCompositionRule? appliedRule,
        IReadOnlyList<int> codePoints)
    {
        IsValid = isValid;
        Error = error;
        Output = output;
        RawSequence = rawSequence;
        Nfc = nfc;
        Nfd = nfd;
        Kind = kind;
        AppliedRule = appliedRule;
        CodePoints = codePoints;
    }

    public bool IsValid { get; }

    public string? Error { get; }

    public string Output { get; }

    public string RawSequence { get; }

    public string Nfc { get; }

    public string Nfd { get; }

    public CompositionKind Kind { get; }

    public SpecialCompositionRule? AppliedRule { get; }

    public IReadOnlyList<int> CodePoints { get; }

    public static CompositionResult Success(
        string output,
        string rawSequence,
        string nfc,
        CompositionKind kind,
        SpecialCompositionRule? appliedRule = null)
    {
        var codePoints = output
            .EnumerateRunes()
            .Select(rune => rune.Value)
            .ToArray();

        return new CompositionResult(
            true,
            null,
            output,
            rawSequence,
            nfc,
            output.Normalize(NormalizationForm.FormD),
            kind,
            appliedRule,
            codePoints);
    }

    public static CompositionResult Failure(string error)
        => new(
            false,
            error,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            CompositionKind.Invalid,
            null,
            Array.Empty<int>());
}
