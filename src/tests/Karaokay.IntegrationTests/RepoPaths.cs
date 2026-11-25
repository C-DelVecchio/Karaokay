namespace Karaokay.IntegrationTests;

/// <summary>
/// Locates repository files from the test output directory by walking up until a directory
/// containing <c>karaokay.sln</c> is found (mirrors the UnitTests helper).
/// </summary>
internal static class RepoPaths
{
    private static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);

    public static string RepoRoot => RepositoryRoot.Value;

    /// <summary>Bundled public-domain demo tracks, used as the API media root in tests.</summary>
    public static string DemoMediaRoot => Path.Combine(
        RepoRoot,
        "src",
        "clients",
        "web",
        "public",
        "demo-tracks");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "karaokay.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root (a directory containing karaokay.sln) above '{AppContext.BaseDirectory}'.");
    }
}
