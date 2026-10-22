using System.IO;
using AccentIt.Models;

namespace AccentIt.Services;

/// <summary>
/// 读取程序自带的符号目录。
/// 如需调整符号、字母路径或其他字符，直接修改 accents.default.jsonc。
/// </summary>
public sealed class AccentCatalogStore
{
    public const string CatalogFileName = "accents.default.jsonc";

    private readonly AccentCatalogLoader _loader;

    public AccentCatalogStore(string catalogPath, AccentCatalogLoader? loader = null)
    {
        if (string.IsNullOrWhiteSpace(catalogPath))
        {
            throw new ArgumentException("符号目录路径不能为空。", nameof(catalogPath));
        }

        CatalogPath = Path.GetFullPath(catalogPath);
        _loader = loader ?? new AccentCatalogLoader();
    }

    public string CatalogPath { get; }

    public static AccentCatalogStore CreateDefault()
    {
        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "Data");

        return new AccentCatalogStore(
            Path.Combine(dataDirectory, CatalogFileName));
    }

    public CatalogLoadResult LoadEffectiveCatalog()
    {
        if (!File.Exists(CatalogPath))
        {
            return new CatalogLoadResult
            {
                Issues = new[]
                {
                    new CatalogIssue(CatalogPath, "找不到符号目录 accents.default.jsonc。")
                },
                SourcePath = CatalogPath
            };
        }

        return _loader.LoadFromFile(CatalogPath);
    }
}
