using System.Globalization;
using Karaokay.Importer.Commands;

namespace Karaokay.IntegrationTests;

/// <summary>
/// Increment 1 of the Onyx pre-stage: encrypted DLC must be diagnosed, not reported as a missing
/// <c>song.ini</c>. The message names the container kinds, gives the manual Onyx recipe and never
/// writes a partial output directory.
/// </summary>
[Collection(ImporterConsoleFixture.Name)]
public sealed class ImporterDlcPreStageTests
{
    [Fact]
    public async Task DirectoryWithTwoLooseContainersExplainsTheOnyxStep()
    {
        using var temp = new TempDirectory("karaokay-dlc-import-");
        var outRoot = temp.Resolve("out");
        DlcTestSupport.CopyFixture("stfs-con.bin", temp.Resolve("a-ha - Take on Me"));
        DlcTestSupport.CopyFixture("stfs-con.bin", temp.Resolve("Bob Marley and the Wailers - Buffalo Soldi"));

        var (exitCode, output) = await RunImporterAsync([temp.Root, "--out", outRoot]);

        Assert.Equal(ImporterCli.ExitFailure, exitCode);
        Assert.StartsWith("error:", output.TrimStart(), StringComparison.Ordinal);
        Assert.Contains(
            "contains 2 encrypted Rock Band containers (STFS CON x2)",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "dotnet run --project src/tools/Karaokay.Importer -- import <exported-folder> --out <library>",
            output,
            StringComparison.Ordinal);
        Assert.Contains("https://github.com/mtolly/onyx/releases", output, StringComparison.Ordinal);
        Assert.Contains("docs/specs/rb-dlc-onyx-prestage.md", output, StringComparison.Ordinal);
        Assert.Contains("docs/adr/010-onyx-subprocess-dlc-extraction.md", output, StringComparison.Ordinal);
        Assert.Contains("Automatic extraction lands with the Onyx pre-stage", output, StringComparison.Ordinal);
        Assert.DoesNotContain("song.ini not found", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(outRoot));
    }

    [Fact]
    public async Task ASingleContainerFileIsAcceptedAsInputAndExplained()
    {
        using var temp = new TempDirectory("karaokay-dlc-import-file-");
        var outRoot = temp.Resolve("out");
        var containerPath = temp.Resolve("a-ha - Take on Me");
        DlcTestSupport.CopyFixture("stfs-con.bin", containerPath);

        var (exitCode, output) = await RunImporterAsync([containerPath, "--out", outRoot]);

        Assert.Equal(ImporterCli.ExitFailure, exitCode);
        Assert.StartsWith("error:", output.TrimStart(), StringComparison.Ordinal);
        Assert.Contains(
            "'" + containerPath + "' is an encrypted Rock Band container (STFS CON).",
            output,
            StringComparison.Ordinal);
        Assert.Contains("Convert it with Onyx", output, StringComparison.Ordinal);
        Assert.Contains(
            "dotnet run --project src/tools/Karaokay.Importer -- import <exported-folder> --out <library>",
            output,
            StringComparison.Ordinal);
        Assert.DoesNotContain("song directory not found", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(outRoot));
    }

    [Fact]
    public async Task AnUnknownFileListsTheExpectedInputs()
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
