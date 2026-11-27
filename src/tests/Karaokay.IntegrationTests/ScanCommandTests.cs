using System.Globalization;
using Karaokay.Importer.Commands;

namespace Karaokay.IntegrationTests;

/// <summary>
/// <c>scan</c> must classify encrypted Rock Band containers (including loose files directly in the
/// scanned root, the <c>media/rb-ingest</c> shape) and must stop advertising vocals-only folders
/// that <c>import</c> refuses (Slice 1 review item 6).
/// </summary>
[Collection(ImporterConsoleFixture.Name)]
public sealed class ScanCommandTests
{
    [Fact]
    public void TwoLooseContainersAtTheRootPrintOneRequiresOnyxLine()
    {
        using var temp = new TempDirectory("karaokay-scan-root-");
        DlcTestSupport.CopyFixture("stfs-con.bin", temp.Resolve("a-ha - Take on Me"));
        DlcTestSupport.CopyFixture("stfs-con.bin", temp.Resolve("Bob Marley and the Wailers - Buffalo Soldi"));
        File.WriteAllText(temp.Resolve("a-ha - Take on Me:Zone.Identifier"), "[ZoneTransfer]");

        var (exitCode, output) = RunScan(temp.Root);

        Assert.Equal(ImporterCli.ExitSuccess, exitCode);
        var line = Assert.Single(NonEmptyLines(output));
        Assert.Equal(". | requires Onyx — 2 encrypted containers (STFS CON x2)", line);
        Assert.DoesNotContain("no songs found", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ASubdirectoryHoldingOneContainerGetsItsOwnRow()
    {
        using var temp = new TempDirectory("karaokay-scan-pack-");
        var packDirectory = temp.Resolve("Take on Me (pack)");
        Directory.CreateDirectory(packDirectory);
        DlcTestSupport.CopyFixture("stfs-con.bin", Path.Combine(packDirectory, "dlc"));

        var (exitCode, output) = RunScan(temp.Root);

        Assert.Equal(ImporterCli.ExitSuccess, exitCode);
        var line = Assert.Single(NonEmptyLines(output));
        Assert.Equal("Take on Me (pack) | requires Onyx — 1 encrypted container (STFS CON)", line);
    }

    [Fact]
    public void PlainSongFoldersKeepTheirExistingLineFormat()
    {
        using var temp = new TempDirectory("karaokay-scan-plain-");
        DlcTestSupport.WriteSongFolder(temp.Root, "song", withVocals: true);

        var (exitCode, output) = RunScan(temp.Root);

        Assert.Equal(ImporterCli.ExitSuccess, exitCode);
        var line = Assert.Single(NonEmptyLines(output));
        Assert.Equal(
            "song | Test Song | Test Artist | delay=250 ms | midi=notes.mid " +
            "| instrumental=[guitar.ogg] | vocals=[vocals.ogg]",
            line);
    }

    [Fact]
    public void VocalsOnlyFolderIsNoLongerAdvertisedAsImportable()
    {
        using var temp = new TempDirectory("karaokay-scan-vocals-");
        DlcTestSupport.WriteSongFolder(
            temp.Root,
            "only-vocals",
            withInstrumental: false,
            withVocals: true);

        var (exitCode, output) = RunScan(temp.Root);

        Assert.Equal(ImporterCli.ExitSuccess, exitCode);
        Assert.DoesNotContain("only-vocals", output, StringComparison.Ordinal);
        Assert.Contains("no songs found", output, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyRootStillPrintsNoSongsFound()
    {
        using var temp = new TempDirectory("karaokay-scan-empty-");

        var (exitCode, output) = RunScan(temp.Root);

        Assert.Equal(ImporterCli.ExitSuccess, exitCode);
        Assert.Equal("no songs found", Assert.Single(NonEmptyLines(output)));
    }

    [Fact]
    public void PlainSongsAndRootContainersAreBothReported()
    {
        using var temp = new TempDirectory("karaokay-scan-mixed-");
        DlcTestSupport.WriteSongFolder(temp.Root, "song");
        DlcTestSupport.CopyFixture("stfs-con.bin", temp.Resolve("a-ha - Take on Me"));
        DlcTestSupport.CopyFixture("stfs-con.bin", temp.Resolve("Bob Marley and the Wailers - Buffalo Soldi"));

        var (exitCode, output) = RunScan(temp.Root);

        Assert.Equal(ImporterCli.ExitSuccess, exitCode);
        var lines = NonEmptyLines(output);
        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, line => line.StartsWith("song | Test Song", StringComparison.Ordinal));
        Assert.Contains(
            lines,
            line => line == ". | requires Onyx — 2 encrypted containers (STFS CON x2)");
    }

    private static (int ExitCode, string Output) RunScan(string root)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(writer);
            Console.SetError(writer);
            var exitCode = ScanCommand.Execute(root);
            return (exitCode, writer.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static List<string> NonEmptyLines(string output) =>
        output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
}
