using System.Globalization;
using Karaokay.SyntheticGen;

return await SyntheticGenCli.RunAsync(args).ConfigureAwait(false);

/// <summary>Hand-rolled argument dispatch for the deterministic demo-song generator.</summary>
internal static class SyntheticGenCli
{
    private const int ExitSuccess = 0;
    private const int ExitFailure = 1;
    private const int ExitUsage = 2;

    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var seed = SyntheticSongGenerator.DefaultSeed;
        var outDirectory = SyntheticSongGenerator.DefaultOutDirectory;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seed":
                    if (i + 1 >= args.Length
                        || !int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out seed))
                    {
                        return UsageError("--seed expects an integer value.");
                    }

                    i++;
                    break;

                case "--out":
                    if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        return UsageError("--out expects a non-empty directory path.");
                    }

                    outDirectory = args[i + 1];
                    i++;
                    break;

                case "--help":
                case "-h":
                    PrintUsage();
                    return ExitSuccess;

                default:
                    return UsageError(Invariant($"unknown argument '{args[i]}'."));
            }
        }

        try
        {
            var songs = await SyntheticSongGenerator.GenerateAsync(seed, outDirectory).ConfigureAwait(false);
            PrintResult(seed, Path.GetFullPath(outDirectory), songs);
            return ExitSuccess;
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(Invariant($"error: {exception.Message}"));
            return ExitFailure;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine(Invariant($"error: {exception.Message}"));
            return ExitFailure;
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine(Invariant($"error: {exception.Message}"));
            return ExitFailure;
        }
    }

    private static void PrintResult(int seed, string outDirectory, IReadOnlyList<GeneratedSong> songs)
    {
        Console.WriteLine(Invariant($"Karaokay.SyntheticGen — seed {seed} → {outDirectory}"));
        foreach (var song in songs)
        {
            Console.WriteLine();
            Console.WriteLine(Invariant($"{song.Slug}  \"{song.Title}\" — {song.Artist}"));
            foreach (var file in song.Files)
            {
                Console.WriteLine(Invariant(
                    $"  {file.Name,-18} {SyntheticSongGenerator.FormatBytes(file.SizeBytes),9} bytes  sha256 {file.Sha256}"));
            }
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            """
            Karaokay.SyntheticGen — deterministic public-domain demo-song generator.

            Usage:
              Karaokay.SyntheticGen [--seed <int>] [--out <dir>]

              --seed <int>   Cosmetic RNG seed (default 42).
              --out <dir>    Output root; songs land in <dir>/<slug>/ (default demo-tracks).

            Exit codes: 0 = success, 1 = generation failure, 2 = usage error.
            """);
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine(Invariant($"error: {message}"));
        PrintUsage();
        return ExitUsage;
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
