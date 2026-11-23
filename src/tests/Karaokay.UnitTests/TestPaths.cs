namespace Karaokay.UnitTests;

/// <summary>
/// Locates repository files from the test output directory by walking up until a directory
/// containing <c>karaokay.sln</c> is found. Tests never rely on the current working directory.
/// </summary>
internal static class TestPaths
{
    private static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);

    /// <summary>Absolute path of the repository root (the folder containing <c>karaokay.sln</c>).</summary>
    public static string RepoRoot => RepositoryRoot.Value;

    /// <summary>Directory holding the committed golden <c>lyrics.json</c> fixtures.</summary>
    public static string GoldenDirectory => Path.Combine(
        RepoRoot,
        "src",
        "tests",
        "Karaokay.UnitTests",
        "fixtures",
        "golden");

    /// <summary>Directory holding the committed demo tracks served by the web client.</summary>
    public static string DemoTracksDirectory => Path.Combine(
        RepoRoot,
        "src",
        "clients",
        "web",
        "public",
        "demo-tracks");

    /// <summary>All demo slugs, in generation order.</summary>
    public static IReadOnlyList<string> DemoTrackSlugs =>
    [
        "synth-twinkle-variations",
        "synth-spoken-telegram",
        "synth-countdown-canyon",
    ];

    public static string GoldenLyricsPath(string slug) =>
        Path.Combine(GoldenDirectory, slug + ".json");

    public static string DemoTrackDirectory(string slug) =>
        Path.Combine(DemoTracksDirectory, slug);

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
