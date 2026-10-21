namespace AccentIt.Models;

public sealed record CatalogIssue(string Location, string Message)
{
    public override string ToString()
        => string.IsNullOrWhiteSpace(Location) ? Message : $"{Location}: {Message}";
}

public sealed class CatalogLoadResult
{
    public AccentCatalog? Catalog { get; init; }

    public IReadOnlyList<CatalogIssue> Issues { get; init; } = Array.Empty<CatalogIssue>();

    public string? SourcePath { get; init; }

    public bool IsSuccess => Catalog is not null;
}
