using System.IO;
using AccentIt.Models;

namespace AccentIt.Services;

public sealed class AccentCatalogStore
{
    public const string SeedFileName = "accents.default.jsonc";
    public const string UserFileName = "accents.jsonc";

    private readonly AccentCatalogLoader _loader;

    public AccentCatalogStore(
        string seedPath,
        string userCatalogPath,
        AccentCatalogLoader? loader = null)
    {
        if (string.IsNullOrWhiteSpace(seedPath))
        {
            throw new ArgumentException("默认符号目录路径不能为空。", nameof(seedPath));
        }

        if (string.IsNullOrWhiteSpace(userCatalogPath))
        {
            throw new ArgumentException("用户符号目录路径不能为空。", nameof(userCatalogPath));
        }

        SeedPath = Path.GetFullPath(seedPath);
        UserCatalogPath = Path.GetFullPath(userCatalogPath);
        _loader = loader ?? new AccentCatalogLoader();
    }

    public string SeedPath { get; }

    public string UserCatalogPath { get; }

    public static AccentCatalogStore CreateDefault()
    {
        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "Data");

        return new AccentCatalogStore(
            Path.Combine(dataDirectory, SeedFileName),
            Path.Combine(dataDirectory, UserFileName));
    }

    public CatalogLoadResult LoadEffectiveCatalog()
    {
        if (!File.Exists(SeedPath))
        {
            return new CatalogLoadResult
            {
                Issues = new[]
                {
                    new CatalogIssue(SeedPath, "找不到程序自带的默认符号目录。")
                },
                SourcePath = SeedPath
            };
        }

        var issues = new List<CatalogIssue>();
        EnsureUserCatalogExists(issues);

        if (!File.Exists(UserCatalogPath))
        {
            var seedResult = _loader.LoadFromFile(SeedPath);
            issues.AddRange(seedResult.Issues);

            if (seedResult.Catalog is not null)
            {
                issues.Add(new CatalogIssue(
                    UserCatalogPath,
                    "无法创建可编辑的用户配置，已临时使用程序自带的默认配置。"));

                return new CatalogLoadResult
                {
                    Catalog = seedResult.Catalog,
                    Issues = issues,
                    SourcePath = SeedPath,
                    UsedFallback = true
                };
            }

            return new CatalogLoadResult
            {
                Issues = issues,
                SourcePath = SeedPath,
                UsedFallback = true
            };
        }

        var userResult = _loader.LoadFromFile(UserCatalogPath);

        if (userResult.Catalog is not null)
        {
            return userResult;
        }

        issues.AddRange(userResult.Issues);
        issues.Add(new CatalogIssue(
            UserCatalogPath,
            "用户配置无法使用，已回退到程序自带的默认配置。"));

        var fallbackResult = _loader.LoadFromFile(SeedPath);
        issues.AddRange(fallbackResult.Issues);

        return new CatalogLoadResult
        {
            Catalog = fallbackResult.Catalog,
            Issues = issues,
            SourcePath = fallbackResult.Catalog is null ? SeedPath : fallbackResult.SourcePath,
            UsedFallback = true
        };
    }

    private void EnsureUserCatalogExists(ICollection<CatalogIssue> issues)
    {
        if (File.Exists(UserCatalogPath))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(UserCatalogPath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(SeedPath, UserCatalogPath, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or ArgumentException)
        {
            if (File.Exists(UserCatalogPath))
            {
                return;
            }

            issues.Add(new CatalogIssue(
                UserCatalogPath,
                $"无法创建可编辑的用户配置：{ex.Message}"));
        }
    }
}
