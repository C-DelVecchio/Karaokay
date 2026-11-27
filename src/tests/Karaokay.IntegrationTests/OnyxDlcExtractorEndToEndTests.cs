using System.Globalization;
using System.Text.RegularExpressions;
using Karaokay.Importer.Commands;

namespace Karaokay.IntegrationTests;

/// <summary>
/// The real round trip: the two copyrighted CON files in <c>media/rb-ingest</c> are decrypted and
/// exported by Onyx, then imported into the canonical three-file layout. Skipped when Onyx or the
/// local (gitignored) DLC files are absent. Output lives under <c>/tmp/opencode</c> and is never
/// committed.
/// </summary>
[Collection(ImporterConsoleFixture.Name)]
public sealed class OnyxDlcExtractorEndToEndTests
{
    [SkippableFact]
    public async Task ImportingTheRealIngestFolderExtractsAndImportsBothSongs()
    {
        OnyxGuard.SkipIfUnavailable();

        var ingestDirectory = Path.Combine(RepoPaths.RepoRoot, "media", "rb-ingest");
        var first = Path.Combine(ingestDirectory, "a-ha - Take on Me");
        var second = Path.Combine(ingestDirectory, "Bob Marley and the Wailers - Buffalo Soldi");
        Skip.If(
            !File.Exists(first) || !File.Exists(second),
            "The local media/rb-ingest DLC files are not present.");

        var outRoot = Path.Combine("/tmp", "opencode", "onyx-e2e");
        if (Directory.Exists(outRoot))
        {
            Directory.Delete(outRoot, recursive: true);
        }

        var (exitCode, output) = await RunImporterAsync([ingestDirectory, "--out", outRoot]);

        Assert.Equal(ImporterCli.ExitSuccess, exitCode);
        Assert.Contains("import summary: imported 2 of 2 songs from", output, StringComparison.Ordinal);

        // Same loudness expectation ImporterEndToEndTests uses for the baked instrumental.
        var loudness = Regex.Matches(
                output,
                @"instrumental loudness:\s*(-?\d+(?:\.\d+)?)\s*LUFS",
                RegexOptions.CultureInvariant)
            .Select(match => double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToList();
        Assert.Equal(2, loudness.Count);
        Assert.All(loudness, value => Assert.InRange(value, -17.0, -15.0));

        foreach (var songId in new[] { "take-on-me", "buffalo-soldier" })
        {
            var directory = Path.Combine(outRoot, songId);
            Assert.True(Directory.Exists(directory), $"Missing output directory '{directory}'.\n{output}");
            Assert.True(File.Exists(Path.Combine(directory, "instrumental.opus")));
            Assert.True(File.Exists(Path.Combine(directory, "vocals.opus")));
            Assert.True(File.Exists(Path.Combine(directory, "lyrics.json")));
            Assert.Equal(ImporterCli.ExitSuccess, VerifyCommand.Execute(directory));
        }
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
