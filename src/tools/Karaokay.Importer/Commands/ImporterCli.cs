namespace Karaokay.Importer.Commands;

/// <summary>Hand-rolled argument dispatch for the importer CLI (no external parser dependency).</summary>
internal static class ImporterCli
{
    /// <summary>Exit code for a successfully completed command.</summary>
    public const int ExitSuccess = 0;

    /// <summary>Exit code for a failed command.</summary>
    public const int ExitFailure = 1;

    /// <summary>Exit code for invalid command-line usage.</summary>
    public const int ExitUsage = 2;

    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            PrintUsage();
            return ExitUsage;
        }

        var command = args[0];
        var rest = args[1..];

        switch (command)
        {
            case "scan":
                if (rest.Length != 1)
                {
                    return UsageError("scan expects exactly one <root> directory.");
                }

                return ScanCommand.Execute(rest[0]);

            case "import":
                return await ImportCommand.ExecuteAsync(rest, CancellationToken.None).ConfigureAwait(false);

            case "verify":
                if (rest.Length != 1)
                {
                    return UsageError("verify expects exactly one <dir> containing the baked song files.");
                }

                return VerifyCommand.Execute(rest[0]);

            case "--help":
            case "-h":
            case "help":
                PrintUsage();
                return ExitSuccess;

            default:
                return UsageError($"unknown command '{command}'.");
        }
    }

    public static void PrintUsage()
    {
        Console.Error.WriteLine(
            """
            Karaokay.Importer — ingest extracted Rock Band song folders.

            Usage:
              scan <root>                          Recursively list importable song folders and
                                                   encrypted containers (requires Onyx).
              import <dir|file> [--out <outRoot>] [--onyx <path>]
                                                   Bake stems and write lyrics.json. Encrypted Rock
                                                   Band DLC is extracted with Onyx first (tested
                                                   release 20261111; its CLI is undocumented and may
                                                   drift). --onyx is the Onyx executable (else
                                                   KARAOKAY_ONYX_PATH, PATH, or the repository
                                                   .tools/ convention).
                                                   PS3 DLC also needs RAP files at
                                                   onyx-resources/raps/<CONTENTID>.RAP; Xbox
                                                   CON/LIVE needs none.
                                                   Default out root: <dir>/karaokay-out.
              verify <dir>                         Validate a baked song folder (3 files).

            Exit codes: 0 = success, 1 = failure, 2 = usage error.
            """);
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        PrintUsage();
        return ExitUsage;
    }
}
