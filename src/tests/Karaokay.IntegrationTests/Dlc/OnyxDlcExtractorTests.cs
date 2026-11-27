using Karaokay.Importer.Dlc;

namespace Karaokay.IntegrationTests;

/// <summary>
/// Pins OnyxDlcExtractor's two-step argv (both <c>--to</c> always present), the injected PS target,
/// exit-code interpretation, timeout kill, produced-nothing and layout-drift handling. A tiny POSIX
/// fake stands in for Onyx; no DLC bytes and no real Onyx are involved.
/// </summary>
public sealed class OnyxDlcExtractorTests
{
    private const string FakeInput = "/dlc/a-ha - Take on Me";

    [SkippableFact]
    public async Task BothStepsUseThePinnedArgumentsAndInjectTheTarget()
    {
        SkipIfNotPosix();
        using var run = await RunAsync("ok");

        Assert.Empty(run.Extraction.Failures);
        var log = FakeOnyx.ReadLog(run.LogPath);
        Assert.Equal(2, log.Count);
        Assert.Equal(
            $"import {FakeInput} --to {Path.Combine(run.WorkDirectory, "project-000")}",
            log[0]);
        Assert.Equal(
            $"build {Path.Combine(run.WorkDirectory, "project-000", "song.yml")} --target ps --to " +
            Path.Combine(run.WorkDirectory, "out", "song-000"),
            log[1]);
        Assert.Contains("--to", log[0], StringComparison.Ordinal);
        Assert.Contains("--to", log[1], StringComparison.Ordinal);
        Assert.DoesNotContain(".zip", log[1], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".sng", log[1], StringComparison.OrdinalIgnoreCase);

        var yaml = File.ReadAllText(Path.Combine(run.CaptureDirectory, "song.yml"));
        Assert.Contains("  ps:", yaml, StringComparison.Ordinal);
        Assert.Contains("    game: ps", yaml, StringComparison.Ordinal);
        Assert.Contains("    audio-format: ogg", yaml, StringComparison.Ordinal);

        Assert.Equal(Path.Combine(run.WorkDirectory, "out", "song-000"), Assert.Single(run.Extraction.SongFolders));
    }

    [SkippableFact]
    public async Task RelativeInputPathsAreResolvedBeforeOnyxRuns()
    {
        SkipIfNotPosix();

        using var temp = new TempDirectory("karaokay-onyx-relative-");
        var script = FakeOnyx.Create(
            Path.Combine(temp.Root, "fake"),
            "ok",
            out var logPath,
            out _);
        var workDirectory = Path.Combine(temp.Root, "work");
        var extractor = new OnyxDlcExtractor(new OnyxLocation(script, "test"), TimeSpan.FromMinutes(2));

        var extraction = await extractor.ExtractAsync(
            [new DlcContainer("relative-dlc-file", DlcContainerFormat.StfsCon, IsPs3: false)],
            workDirectory,
            CancellationToken.None);

        Assert.Empty(extraction.Failures);
        var importLine = FakeOnyx.ReadLog(logPath)[0];
        Assert.Contains(Path.GetFullPath("relative-dlc-file"), importLine, StringComparison.Ordinal);
        Assert.StartsWith("import /", importLine, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task OneTimeoutBudgetCoversBothSteps()
    {
        SkipIfNotPosix();
        // Each step sleeps 0.3 s; a per-step budget would let both pass, while the documented
        // per-input budget must time out during the second step.
        using var run = await RunAsync("slow", TimeSpan.FromMilliseconds(450));

        var failure = Assert.Single(run.Extraction.Failures);
        Assert.Equal(OnyxFailureKind.Timeout, failure.Kind);
        Assert.Empty(run.Extraction.SongFolders);
    }

    [SkippableFact]
    public async Task AnsiEscapesAreStrippedAndTheTailIsCapped()
    {
        SkipIfNotPosix();
        using var run = await RunAsync("ansi-flood");

        var failure = Assert.Single(run.Extraction.Failures);
        Assert.Equal(OnyxFailureKind.NonZeroExit, failure.Kind);
        Assert.Equal(9, failure.ExitCode);
        Assert.DoesNotContain('\u001b', failure.Diagnostic);
        Assert.DoesNotContain("[33m", failure.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("         | ", failure.Diagnostic, StringComparison.Ordinal);

        var tailLines = failure.Diagnostic
            .Split('\n')
            .Count(line => line.StartsWith("         | ", StringComparison.Ordinal));
        Assert.InRange(tailLines, 1, 20);
        Assert.True(failure.Diagnostic.Length < 10_000, "The diagnostic should be capped.");
    }

    [SkippableFact]
    public async Task NonZeroImportExitSurfacesTheStderrTail()
    {
        SkipIfNotPosix();
        using var run = await RunAsync("fail-import");

        var failure = Assert.Single(run.Extraction.Failures);
        Assert.Equal(OnyxFailureKind.NonZeroExit, failure.Kind);
        Assert.Equal(7, failure.ExitCode);
        Assert.Contains("exit code 7", failure.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("fake import exploded", failure.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("Onyx output was discarded", failure.Diagnostic, StringComparison.Ordinal);
        Assert.Empty(run.Extraction.SongFolders);
    }

    [SkippableFact]
    public async Task NonZeroBuildExitSurfacesTheStderrTail()
    {
        SkipIfNotPosix();
        using var run = await RunAsync("fail-build");

        var failure = Assert.Single(run.Extraction.Failures);
        Assert.Equal(OnyxFailureKind.NonZeroExit, failure.Kind);
        Assert.Equal(3, failure.ExitCode);
        Assert.Contains("fake build exploded", failure.Diagnostic, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task HungOnyxIsKilledAtTheTimeout()
    {
        SkipIfNotPosix();
        using var run = await RunAsync("hang", TimeSpan.FromMilliseconds(300));

        var failure = Assert.Single(run.Extraction.Failures);
        Assert.Equal(OnyxFailureKind.Timeout, failure.Kind);
        Assert.Contains("did not finish", failure.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("KARAOKAY_ONYX_TIMEOUT", failure.Diagnostic, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task SuccessfulBuildWithNoSongFolderIsProducedNothing()
    {
        SkipIfNotPosix();
        using var run = await RunAsync("nothing");

        var failure = Assert.Single(run.Extraction.Failures);
        Assert.Equal(OnyxFailureKind.ProducedNothing, failure.Kind);
        Assert.Contains("produced no song folder", failure.Diagnostic, StringComparison.Ordinal);
        Assert.Empty(run.Extraction.SongFolders);
    }

    [SkippableFact]
    public async Task UnexpectedYamlLayoutIsLayoutDrift()
    {
        SkipIfNotPosix();
        using var run = await RunAsync("drift");

        var failure = Assert.Single(run.Extraction.Failures);
        Assert.Equal(OnyxFailureKind.LayoutDrift, failure.Kind);
        Assert.Contains("top-level key after `targets:`", failure.Diagnostic, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task NestedSongFolderIsDiscovered()
    {
        SkipIfNotPosix();
        using var run = await RunAsync("nested");

        Assert.Empty(run.Extraction.Failures);
        var folder = Assert.Single(run.Extraction.SongFolders);
        Assert.EndsWith("nested", folder, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Ps3FailureGetsTheRapHint()
    {
        SkipIfNotPosix();
        using var run = await RunAsync("fail-ps3", isPs3: true);

        var failure = Assert.Single(run.Extraction.Failures);
        Assert.Contains("per-content RAP licence", failure.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("onyx-resources/raps", failure.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetInjectionIsIdempotent()
    {
        using var temp = new TempDirectory("karaokay-onyx-yaml-");
        var yamlPath = temp.Resolve("song.yml");
        File.WriteAllText(
            yamlPath,
            "global:\n  anim-tempo: slow\ntargets:\n  rb3:\n    game: rb3\n");

        OnyxDlcExtractor.InjectPhaseShiftTarget(yamlPath);
        OnyxDlcExtractor.InjectPhaseShiftTarget(yamlPath);

        var text = File.ReadAllText(yamlPath);
        Assert.Equal(1, CountOccurrences(text, "  ps:"));
        Assert.Equal(1, CountOccurrences(text, "audio-format: ogg"));
        Assert.Contains("game: ps", text, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static void SkipIfNotPosix() =>
        Skip.If(OperatingSystem.IsWindows(), "The fake Onyx shim is a POSIX shell script.");

    private static async Task<ExtractionRun> RunAsync(
        string mode,
        TimeSpan? timeout = null,
        bool isPs3 = false)
    {
        var temp = new TempDirectory("karaokay-onyx-extract-");
        var script = FakeOnyx.Create(
            Path.Combine(temp.Root, "fake"),
            mode,
            out var logPath,
            out var captureDirectory);
        var workDirectory = Path.Combine(temp.Root, "work");

        var extractor = new OnyxDlcExtractor(
            new OnyxLocation(script, "test"),
            timeout ?? TimeSpan.FromMinutes(2));
        var input = new DlcContainer(
            FakeInput,
            isPs3 ? DlcContainerFormat.Ps3Package : DlcContainerFormat.StfsCon,
            isPs3);
        var extraction = await extractor.ExtractAsync([input], workDirectory, CancellationToken.None);

        return new ExtractionRun(temp, workDirectory, logPath, captureDirectory, extraction);
    }

    private sealed class ExtractionRun : IDisposable
    {
        public ExtractionRun(
            TempDirectory temp,
            string workDirectory,
            string logPath,
            string captureDirectory,
            OnyxExtractionResult extraction)
        {
            Temp = temp;
            WorkDirectory = workDirectory;
            LogPath = logPath;
            CaptureDirectory = captureDirectory;
            Extraction = extraction;
        }

        public TempDirectory Temp { get; }

        public string WorkDirectory { get; }

        public string LogPath { get; }

        public string CaptureDirectory { get; }

        public OnyxExtractionResult Extraction { get; }

        public void Dispose() => Temp.Dispose();
    }
}
