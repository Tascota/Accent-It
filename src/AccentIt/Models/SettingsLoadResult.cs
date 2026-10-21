namespace AccentIt.Models;

public sealed record SettingsIssue(string Location, string Message)
{
    public override string ToString()
        => string.IsNullOrWhiteSpace(Location) ? Message : $"{Location}: {Message}";
}

public sealed class SettingsLoadResult
{
    public AppSettings? Settings { get; init; }

    public IReadOnlyList<SettingsIssue> Issues { get; init; } = Array.Empty<SettingsIssue>();

    public string? SourcePath { get; init; }

    public bool UsedFallback { get; init; }

    public bool CreatedDefaultFile { get; init; }

    public bool IsSuccess => Settings is not null;
}
