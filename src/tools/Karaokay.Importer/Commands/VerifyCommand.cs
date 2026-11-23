using System.Globalization;
using System.Text;
using Karaokay.Contracts;

namespace Karaokay.Importer.Commands;

/// <summary>
/// <c>verify &lt;dir&gt;</c> — validates a baked song folder: the three contract files exist and are
/// non-empty, <c>lyrics.json</c> deserializes and passes structural validation, and re-serializing
/// the parsed document reproduces the file byte-for-byte.
/// </summary>
internal static class VerifyCommand
{
    public static int Execute(string directory)
    {
        if (!Directory.Exists(directory))
        {
            Console.Error.WriteLine($"error: song directory not found: {directory}");
            return ImporterCli.ExitFailure;
        }

        var allPassed = true;

        allPassed &= CheckFile(Path.Combine(directory, "instrumental.opus"), "instrumental.opus");
        allPassed &= CheckFile(Path.Combine(directory, "vocals.opus"), "vocals.opus");

        var lyricsPath = Path.Combine(directory, "lyrics.json");
        if (!CheckFile(lyricsPath, "lyrics.json"))
        {
            return Summary(allPassed);
        }

        LyricsDocument? document;
        try
        {
            document = LyricsJson.DeserializeFile(lyricsPath);
        }
        catch (IOException exception)
        {
            Report(false, "lyrics.json is readable", exception.Message);
            return ImporterCli.ExitFailure;
        }

        allPassed &= Report(
            document is not null,
            "lyrics.json deserializes",
            document is null ? "invalid lyrics document" : "ok");
        if (document is null)
        {
            return Summary(allPassed);
        }

        var violations = LyricsJson.Validate(document);
        allPassed &= Report(
            violations.Count == 0,
            "lyrics.json structural validation",
            violations.Count == 0
                ? "no violations"
                : string.Create(CultureInfo.InvariantCulture, $"{violations.Count} violation(s)"));
        foreach (var violation in violations)
        {
            Console.WriteLine($"       - {violation}");
        }

        var expectedBytes = Encoding.UTF8.GetBytes(LyricsJson.Serialize(document));
        var actualBytes = File.ReadAllBytes(lyricsPath);
        var identical = expectedBytes.AsSpan().SequenceEqual(actualBytes);
        allPassed &= Report(
            identical,
            "lyrics.json roundtrip is byte-identical",
            identical
                ? string.Create(CultureInfo.InvariantCulture, $"{actualBytes.Length} bytes")
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"on disk {actualBytes.Length} bytes, re-serialized {expectedBytes.Length} bytes"));

        return Summary(allPassed);
    }

    private static bool CheckFile(string path, string label)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return Report(false, label, "file is missing");
        }

        if (info.Length == 0)
        {
            return Report(false, label, "file is empty");
        }

        return Report(
            true,
            label,
            string.Create(CultureInfo.InvariantCulture, $"{info.Length} bytes"));
    }

    private static bool Report(bool passed, string label, string detail)
    {
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {label}: {detail}");
        return passed;
    }

    private static int Summary(bool allPassed)
    {
        Console.WriteLine(allPassed ? "verify: all checks passed" : "verify: one or more checks failed");
        return allPassed ? ImporterCli.ExitSuccess : ImporterCli.ExitFailure;
    }
}
