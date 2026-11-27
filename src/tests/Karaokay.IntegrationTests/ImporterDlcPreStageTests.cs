using System.Globalization;
using System.Text;
using CliWrap;
using Karaokay.Importer.Commands;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;

namespace Karaokay.IntegrationTests;

/// <summary>
/// The import pre-stage's user-facing behaviour: encrypted DLC is either extracted through Onyx and
/// imported, or diagnosed with the §6.1 message when Onyx is missing. Failed extraction never leaves
/// output in <c>--out</c> or a <c>karaokay-onyx-*</c> work directory behind.
/// </summary>
[Collection(ImporterConsoleFixture.Name)]
public sealed class ImporterDlcPreStageTests
{
    [Fact]
    public async Task DirectoryWithTwoLooseContainersExplainsHowToInstallOnyx()
    {
        using var temp = new TempDirectory("karaokay-dlc-import-");
        var outRoot = temp.Resolve("out");
        var missingOnyx = temp.Resolve("missing-onyx");
        DlcTestSupport.CopyFixture("stfs-con.bin", temp.Resolve("a-ha - Take on Me"));
        DlcTestSupport.CopyFixture("stfs-con.bin", temp.Resolve("Bob Marley and the Wailers - Buffalo Soldi"));

        var (exitCode, output) = await RunImporterAsync(
            [temp.Root, "--out", outRoot, "--onyx", missingOnyx]);

        Assert.Equal(ImporterCli.ExitFailure, exitCode);
        Assert.StartsWith("error:", output.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("encrypted Rock Band DLC detected in", output, StringComparison.Ordinal);
        Assert.Contains("but Onyx was not found", output, StringComparison.Ordinal);
        Assert.Contains("Detected: 2 containers (STFS CON x2)", output, StringComparison.Ordinal);
        Assert.Contains("Searched:", output, StringComparison.Ordinal);
        Assert.Contains($"--onyx '{missingOnyx}' (not found)", output, StringComparison.Ordinal);
        Assert.Contains("https://github.com/mtolly/onyx/releases", output, StringComparison.Ordinal);
        Assert.Contains("libfuse2", output, StringComparison.Ordinal);
        Assert.Contains("KARAOKAY_ONYX_PATH", output, StringComparison.Ordinal);
        Assert.DoesNotContain("song.ini not found", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(outRoot));
    }

    [Fact]
    public async Task ASingleContainerFileIsAcceptedAsInput()
    {
        using var temp = new TempDirectory("karaokay-dlc-import-file-");
        var outRoot = temp.Resolve("out");
        var containerPath = temp.Resolve("a-ha - Take on Me");
        DlcTestSupport.CopyFixture("stfs-con.bin", containerPath);

        var (exitCode, output) = await RunImporterAsync(
            [containerPath, "--out", outRoot, "--onyx", temp.Resolve("missing-onyx")]);

        Assert.Equal(ImporterCli.ExitFailure, exitCode);
        Assert.StartsWith("error:", output.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("encrypted Rock Band DLC detected in", output, StringComparison.Ordinal);
        Assert.Contains("Detected: 1 container (STFS CON)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("song directory not found", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(outRoot));
    }

    [Fact]
    public async Task AnUnknownFileListsTheExpectedInputsIncludingOpus()
    {
        using var temp = new TempDirectory("karaokay-dlc-import-unknown-");
        var outRoot = temp.Resolve("out");
        var notAContainer = temp.Resolve("whatever.ogg");
        DlcTestSupport.CopyFixture("plain-ogg.bin", notAContainer);

        var (exitCode, output) = await RunImporterAsync([notAContainer, "--out", outRoot]);

        Assert.Equal(ImporterCli.ExitFailure, exitCode);
        Assert.StartsWith("error:", output.TrimStart(), StringComparison.Ordinal);
        Assert.Contains(
            "is not a song folder and not a container Karaokay recognizes",
            output,
            StringComparison.Ordinal);
        Assert.Contains("an STFS package (Xbox 360 Rock Band DLC, CON/LIVE/PIRS)", output, StringComparison.Ordinal);
        Assert.Contains("*.ogg/*.oga/*.opus stems", output, StringComparison.Ordinal);
        Assert.Contains("docs/specs/rb-dlc-onyx-prestage.md", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(outRoot));
    }

    [Fact]
    public async Task AnEmptyDirectoryFailsWithTheNoSongFolderMessage()
    {
        using var temp = new TempDirectory("karaokay-dlc-import-empty-");
        var outRoot = temp.Resolve("out");

        var (exitCode, output) = await RunImporterAsync([temp.Root, "--out", outRoot]);

        Assert.Equal(ImporterCli.ExitFailure, exitCode);
        Assert.StartsWith("error:", output.TrimStart(), StringComparison.Ordinal);
        Assert.Contains(
            "no song folder or encrypted Rock Band container found in:",
            output,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(outRoot));
    }

    [SkippableFact]
    public async Task OnyxFailureIsSurfacedAndLeavesNoOutputOrWorkDirectory()
    {
        Skip.If(OperatingSystem.IsWindows(), "The fake Onyx shim is a POSIX shell script.");

        using var temp = new TempDirectory("karaokay-dlc-import-onyx-fail-");
        var outRoot = temp.Resolve("out");
        var input = temp.Resolve("a-ha - Take on Me");
        DlcTestSupport.CopyFixture("stfs-con.bin", input);
        var script = FakeOnyx.Create(
            Path.Combine(temp.Root, "fake"),
            "fail-import",
            out _,
            out _);

        var before = CountWorkDirectories();
        var (exitCode, output) = await RunImporterAsync(
            [input, "--out", outRoot, "--onyx", script]);
        var after = CountWorkDirectories();

        Assert.Equal(ImporterCli.ExitFailure, exitCode);
        Assert.Contains("error: Onyx failed to extract", output, StringComparison.Ordinal);
        Assert.Contains("exit code 7", output, StringComparison.Ordinal);
        Assert.Contains("fake import exploded", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(outRoot));
        Assert.Equal(before, after);
    }

    [SkippableFact]
    public async Task AFakeExtractionFlowsIntoThePerSongImportAndReportsTheSummary()
    {
        Skip.If(OperatingSystem.IsWindows(), "The fake Onyx shim is a POSIX shell script.");

        using var temp = new TempDirectory("karaokay-dlc-import-onyx-ok-");
        var outRoot = temp.Resolve("out");
        var input = temp.Resolve("a-ha - Take on Me");
        DlcTestSupport.CopyFixture("stfs-con.bin", input);
        var script = FakeOnyx.Create(
            Path.Combine(temp.Root, "fake"),
            "ok",
            out _,
            out _);

        var (exitCode, output) = await RunImporterAsync(
            [input, "--out", outRoot, "--onyx", script]);

        // The fake's folder has a deliberately invalid empty MIDI, so the per-song import fails; the
        // point here is that extraction ran, the unchanged import path was reached, and the summary
        // reports the failure without aborting the command early.
        Assert.Equal(ImporterCli.ExitFailure, exitCode);
        Assert.Contains("extracting 1 container(s) with Onyx (--onyx:", output, StringComparison.Ordinal);
        Assert.Contains("import summary: imported 0 of 1 songs from", output, StringComparison.Ordinal);
        Assert.Contains("error: 0 of 1 songs imported.", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(outRoot));
    }

    [SkippableFact]
    public async Task FirstInputFailureStillImportsTheSecondSong()
    {
        Skip.If(OperatingSystem.IsWindows(), "The fake Onyx shim is a POSIX shell script.");
        FfmpegGuard.Require();

        using var temp = new TempDirectory("karaokay-dlc-import-multi-");
        var outRoot = temp.Resolve("out");
        DlcTestSupport.CopyFixture("stfs-con.bin", temp.Resolve("a-fail"));
        DlcTestSupport.CopyFixture("stfs-con.bin", temp.Resolve("b-ok"));
        var prepared = await CreatePreparedSongAsync(temp.Root);
        var script = FakeOnyx.Create(
            Path.Combine(temp.Root, "fake"),
            "fail-first",
            out _,
            out _,
            prepared);

        var (exitCode, output) = await RunImporterAsync(
            [temp.Root, "--out", outRoot, "--onyx", script]);

        Assert.Equal(ImporterCli.ExitFailure, exitCode);
        Assert.Contains("error: Onyx failed to extract", output, StringComparison.Ordinal);
        Assert.Contains("a-fail", output, StringComparison.Ordinal);
        Assert.Contains("import summary: imported 1 of 1 songs from", output, StringComparison.Ordinal);
        Assert.Contains("1 of 2 inputs failed to extract", output, StringComparison.Ordinal);

        var songDirectory = Path.Combine(outRoot, "prepared-song");
        Assert.True(File.Exists(Path.Combine(songDirectory, "instrumental.opus")), output);
        Assert.True(File.Exists(Path.Combine(songDirectory, "vocals.opus")), output);
        Assert.True(File.Exists(Path.Combine(songDirectory, "lyrics.json")), output);
    }

    [SkippableFact]
    public async Task OneFoldersImportFailureDoesNotAbortTheOther()
    {
        Skip.If(OperatingSystem.IsWindows(), "The fake Onyx shim is a POSIX shell script.");
        FfmpegGuard.Require();

        using var temp = new TempDirectory("karaokay-dlc-import-partial-");
        var outRoot = temp.Resolve("out");
        DlcTestSupport.CopyFixture("stfs-con.bin", temp.Resolve("a-ok"));
        var prepared = await CreatePreparedSongAsync(temp.Root);
        var script = FakeOnyx.Create(
            Path.Combine(temp.Root, "fake"),
            "two-folders",
            out _,
            out _,
            prepared);

        var (exitCode, output) = await RunImporterAsync(
            [temp.Root, "--out", outRoot, "--onyx", script]);

        Assert.Equal(ImporterCli.ExitFailure, exitCode);
        Assert.Contains("import summary: imported 1 of 2 songs from", output, StringComparison.Ordinal);
        Assert.Contains("error: 1 of 2 songs failed; the successful songs were kept.", output, StringComparison.Ordinal);

        var songDirectory = Path.Combine(outRoot, "prepared-song");
        Assert.True(File.Exists(Path.Combine(songDirectory, "instrumental.opus")), output);
        Assert.True(File.Exists(Path.Combine(songDirectory, "lyrics.json")), output);
        Assert.False(Directory.Exists(Path.Combine(outRoot, "bad-song")));
    }

    private static async Task<string> CreatePreparedSongAsync(string parent)
    {
        var directory = Path.Combine(parent, "prepared-song");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "song.ini"),
            """
            [song]
            name = Prepared Song
            artist = Prepared Artist
            """);
        WriteMinimalNotesMidi(Path.Combine(directory, "notes.mid"));
        await CreateSineStemAsync(Path.Combine(directory, "guitar.ogg"));
        return directory;
    }

    private static void WriteMinimalNotesMidi(string path)
    {
        var midi = new MidiFile
        {
            TimeDivision = new TicksPerQuarterNoteTimeDivision(480),
        };

        var conductor = new TrackChunk();
        conductor.Events.Add(new SequenceTrackNameEvent("Karaokay"));
        conductor.Events.Add(new SetTempoEvent(500_000));
        midi.Chunks.Add(conductor);

        var vocalTrack = new TrackChunk();
        vocalTrack.Events.Add(new SequenceTrackNameEvent("PART VOCALS"));
        vocalTrack.Events.Add(new LyricEvent("la"));
        vocalTrack.Events.Add(new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100));
        vocalTrack.Events.Add(new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0) { DeltaTime = 960 });
        midi.Chunks.Add(vocalTrack);

        midi.Write(path);
    }

    private static async Task CreateSineStemAsync(string path)
    {
        var standardError = new StringBuilder();
        var result = await Cli.Wrap("ffmpeg")
            .WithArguments(arguments => arguments
                .Add("-hide_banner")
                .Add("-nostdin")
                .Add("-y")
                .Add("-f").Add("lavfi")
                .Add("-i").Add("sine=frequency=220:duration=1")
                .Add("-c:a").Add("libvorbis")
                .Add("-q:a").Add("4")
                .Add(path))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(standardError))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync()
            .ConfigureAwait(false);

        Assert.True(
            result.ExitCode == 0,
            $"ffmpeg failed to create the sine stem '{path}' (exit {result.ExitCode}): {standardError}");
    }

    private static int CountWorkDirectories() =>
        Directory.Exists(Path.GetTempPath())
            ? Directory.GetDirectories(Path.GetTempPath(), "karaokay-onyx-*").Length
            : 0;

    private static async Task<(int ExitCode, string Output)> RunImporterAsync(string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(writer);
            Console.SetError(writer);
            var exitCode = await ImportCommand.ExecuteAsync(args, CancellationToken.None);
            return (exitCode, writer.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
