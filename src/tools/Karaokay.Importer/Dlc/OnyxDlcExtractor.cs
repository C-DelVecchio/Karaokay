using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CliWrap;

namespace Karaokay.Importer.Dlc;

/// <summary>Why an input could not be turned into one or more Phase Shift song folders.</summary>
internal enum OnyxFailureKind
{
    NonZeroExit,
    Timeout,
    ProducedNothing,
    LayoutDrift,
}

/// <summary>One failed input. <see cref="Diagnostic"/> is the user-facing message body.</summary>
internal sealed record OnyxInputFailure(
    string InputPath,
    OnyxFailureKind Kind,
    int? ExitCode,
    string Diagnostic);

/// <summary>Result of extracting all detected containers in one work directory.</summary>
internal sealed record OnyxExtractionResult(
    IReadOnlyList<string> SongFolders,
    IReadOnlyList<OnyxInputFailure> Failures);

/// <summary>
/// The only place in the repository that knows Onyx's two-step command line and its project YAML.
/// Onyx 20261111 has no one-shot converter: <c>import</c> creates a project, a <c>ps</c> target is
/// injected, and <c>build --target ps</c> exports the Phase Shift folder. See
/// <c>docs/specs/rb-dlc-onyx-prestage.md</c> §4.3/§5.
/// </summary>
internal sealed partial class OnyxDlcExtractor
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);

    private const string TimeoutEnvironmentVariable = "KARAOKAY_ONYX_TIMEOUT";
    private const int MaxCapturedCharacters = 64 * 1024;
    private const int MaxDiagnosticLines = 20;
    private const int MaxDiscoveryDepth = 3;

    private readonly OnyxLocation _onyx;
    private readonly TimeSpan _timeout;
    private readonly Action<string>? _log;

    public OnyxDlcExtractor(OnyxLocation onyx, TimeSpan? timeout = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(onyx);
        _onyx = onyx;
        _timeout = timeout is { } requested && requested > TimeSpan.Zero
            ? requested
            : ResolveConfiguredTimeout();
        _log = log;
    }

    public TimeSpan Timeout => _timeout;

    /// <summary>
    /// Extracts every input sequentially into <paramref name="workDirectory"/>. The caller owns the
    /// whole work directory (project trees, <c>gen-*</c> build folders and <c>out/</c>) and deletes
    /// it on success and failure; this method never imports anything itself.
    /// </summary>
    public async Task<OnyxExtractionResult> ExtractAsync(
        IReadOnlyList<DlcContainer> inputs,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectory);

        Directory.CreateDirectory(workDirectory);
        var outRoot = Path.Combine(workDirectory, "out");
        Directory.CreateDirectory(outRoot);

        var songFolders = new List<string>();
        var failures = new List<OnyxInputFailure>();

        for (var index = 0; index < inputs.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var input = inputs[index];
            // Onyx is started with the temp work directory as CWD, so a relative input path would
            // resolve there. Always hand Onyx an absolute path.
            var absoluteInputPath = Path.GetFullPath(input.Path);
            var projectDirectory = Path.Combine(
                workDirectory,
                string.Create(CultureInfo.InvariantCulture, $"project-{index:D3}"));
            var outputDirectory = Path.Combine(
                outRoot,
                string.Create(CultureInfo.InvariantCulture, $"song-{index:D3}"));

            var elapsed = Stopwatch.StartNew();

            // ONE timeout budget per input covers BOTH steps (§4.3/§6.3). A fresh budget per step
            // would silently allow 2× the documented limit.
            using var inputTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            inputTimeout.CancelAfter(_timeout);
            var stepToken = inputTimeout.Token;

            // Step 1: import into Onyx's project format. --to is mandatory: without it Onyx writes
            // "<input>_import" next to the user's copyrighted source file.
            var import = await RunAsync(
                    ["import", absoluteInputPath, "--to", projectDirectory],
                    workDirectory,
                    stepToken,
                    cancellationToken)
                .ConfigureAwait(false);

            if (import.TimedOut || import.ExitCode != 0)
            {
                failures.Add(BuildProcessFailure(input, import, elapsed.Elapsed));
                continue;
            }

            var yamlPath = Path.Combine(projectDirectory, "song.yml");
            if (!File.Exists(yamlPath))
            {
                failures.Add(BuildLayoutDriftFailure(
                    input,
                    $"Onyx's import produced no song.yml in '{projectDirectory}'"));
                continue;
            }

            try
            {
                InjectPhaseShiftTarget(yamlPath);
            }
            catch (InvalidOperationException exception)
            {
                failures.Add(BuildLayoutDriftFailure(input, exception.Message));
                continue;
            }

            // Step 2: export the Phase Shift folder. No extension on --to means "folder".
            var build = await RunAsync(
                    ["build", yamlPath, "--target", "ps", "--to", outputDirectory],
                    workDirectory,
                    stepToken,
                    cancellationToken)
                .ConfigureAwait(false);

            elapsed.Stop();

            if (build.TimedOut || build.ExitCode != 0)
            {
                failures.Add(BuildProcessFailure(input, build, elapsed.Elapsed));
                continue;
            }

            var discovered = DiscoverSongFolders(outputDirectory);
            if (discovered.Count == 0)
            {
                failures.Add(new OnyxInputFailure(
                    input.Path,
                    OnyxFailureKind.ProducedNothing,
                    build.ExitCode,
                    BuildProducedNothingDiagnostic(input, build.StandardError)));
                continue;
            }

            songFolders.AddRange(discovered);
        }

        songFolders.Sort(StringComparer.Ordinal);
        return new OnyxExtractionResult(songFolders, failures);
    }

    /// <summary>
    /// Appends <c>ps:</c> / <c>game: ps</c> / <c>audio-format: ogg</c> under the project's
    /// top-level <c>targets:</c> key. Idempotent; refuses to guess when a future Onyx writes a
    /// different layout.
    /// </summary>
    internal static void InjectPhaseShiftTarget(string songYamlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(songYamlPath);

        var text = File.ReadAllText(songYamlPath);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        var targetsIndex = Array.FindIndex(lines, line =>
            string.Equals(line.Trim(), "targets:", StringComparison.Ordinal)
            && line.Length == line.TrimStart().Length);
        if (targetsIndex < 0)
        {
            throw new InvalidOperationException("song.yml has no top-level `targets:` key");
        }

        for (var lineIndex = targetsIndex + 1; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (line.Length == 0)
            {
                continue;
            }

            if (!char.IsWhiteSpace(line[0]))
            {
                throw new InvalidOperationException(
                    "song.yml has a top-level key after `targets:`; refusing to guess where to append the ps target");
            }

            if (string.Equals(line.Trim(), "ps:", StringComparison.Ordinal))
            {
                return;
            }
        }

        if (!text.EndsWith('\n'))
        {
            text += newline;
        }

        text += string.Create(
            CultureInfo.InvariantCulture,
            $"  ps:{newline}    game: ps{newline}    audio-format: ogg{newline}");

        File.WriteAllText(songYamlPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Finds every directory containing <c>song.ini</c> under a build output path.</summary>
    internal static List<string> DiscoverSongFolders(string root)
    {
        var results = new List<string>();
        if (!Directory.Exists(root))
        {
            return results;
        }

        if (File.Exists(Path.Combine(root, "song.ini")))
        {
            results.Add(root);
            return results;
        }

        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (directory, depth) = queue.Dequeue();
            if (depth >= MaxDiscoveryDepth)
            {
                continue;
            }

            List<string> children;
            try
            {
                children = Directory.EnumerateDirectories(directory).ToList();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                if (File.Exists(Path.Combine(child, "song.ini")))
                {
                    results.Add(child);
                    continue;
                }

                queue.Enqueue((child, depth + 1));
            }
        }

        results.Sort(StringComparer.Ordinal);
        return results;
    }

    private static TimeSpan ResolveConfiguredTimeout()
    {
        var value = Environment.GetEnvironmentVariable(TimeoutEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(value)
            && TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            && parsed > TimeSpan.Zero)
        {
            return parsed;
        }

        return DefaultTimeout;
    }

    private async Task<ProcessOutcome> RunAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken stepToken,
        CancellationToken cancellationToken)
    {
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();

        try
        {
            var result = await Cli.Wrap(_onyx.ExecutablePath)
                .WithArguments(arguments)
                .WithWorkingDirectory(workingDirectory)
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(PipeTarget.ToStringBuilder(standardOutput))
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(standardError))
                .ExecuteAsync(stepToken)
                .ConfigureAwait(false);

            var outcome = new ProcessOutcome(
                result.ExitCode,
                Cap(standardOutput.ToString()),
                Cap(standardError.ToString()),
                TimedOut: false,
                StartError: null);
            _log?.Invoke(string.Create(
                CultureInfo.InvariantCulture,
                $"onyx {string.Join(' ', arguments)} -> exit {result.ExitCode}"));
            return outcome;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log?.Invoke("onyx invocation timed out and was killed");
            return new ProcessOutcome(
                -1,
                Cap(standardOutput.ToString()),
                Cap(standardError.ToString()),
                TimedOut: true,
                StartError: null);
        }
        catch (Win32Exception exception)
        {
            return new ProcessOutcome(
                -1,
                Cap(standardOutput.ToString()),
                Cap(standardError.ToString()),
                TimedOut: false,
                StartError: exception.Message);
        }
    }

    private OnyxInputFailure BuildProcessFailure(DlcContainer input, ProcessOutcome outcome, TimeSpan elapsed)
    {
        if (outcome.TimedOut)
        {
            return new OnyxInputFailure(
                input.Path,
                OnyxFailureKind.Timeout,
                null,
                BuildTimeoutDiagnostic(input));
        }

        return new OnyxInputFailure(
            input.Path,
            OnyxFailureKind.NonZeroExit,
            outcome.StartError is null ? outcome.ExitCode : null,
            BuildNonZeroDiagnostic(input, outcome, elapsed));
    }

    private string BuildNonZeroDiagnostic(DlcContainer input, ProcessOutcome outcome, TimeSpan elapsed)
    {
        var builder = new StringBuilder();
        if (outcome.StartError is not null)
        {
            builder.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"Onyx could not be started ('{_onyx.ExecutablePath}'): {outcome.StartError}"));
        }
        else
        {
            builder.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"Onyx failed to extract '{input.Path}' (exit code {outcome.ExitCode}) " +
                $"after {elapsed.TotalSeconds:0.0} s."));
        }

        builder.AppendLine();
        AppendTail(builder, outcome.StandardError, "Onyx said (last lines of stderr):");
        builder.AppendLine("       Onyx output was discarded; nothing was imported. Re-run `onyx --help` to check this");
        builder.Append("       build's command line if the message looks like a usage error.");

        if (input.IsPs3 && MentionsLicenceProblem(outcome.StandardError))
        {
            builder.AppendLine();
            builder.AppendLine("       Hint: PS3 packages are encrypted with a per-content RAP licence. Put the matching");
            builder.Append("       onyx-resources/raps/<CONTENTID>.RAP next to the Onyx executable and re-run.");
        }

        return builder.ToString();
    }

    private string BuildTimeoutDiagnostic(DlcContainer input)
    {
        var builder = new StringBuilder();
        builder.Append(string.Create(
            CultureInfo.InvariantCulture,
            $"Onyx did not finish extracting '{input.Path}' within {FormatTimeout(_timeout)} and was stopped."));
        builder.AppendLine();
        builder.AppendLine("       Large multi-song packs can take longer. Raise the limit with");
        builder.Append("       KARAOKAY_ONYX_TIMEOUT=00:30:00 and re-run. Onyx output was discarded.");
        return builder.ToString();
    }

    private static string BuildProducedNothingDiagnostic(DlcContainer input, string standardError)
    {
        var builder = new StringBuilder();
        builder.Append(string.Create(
            CultureInfo.InvariantCulture,
            $"Onyx reported success for '{input.Path}' but produced no song folder."));
        builder.AppendLine();
        AppendTail(
            builder,
            standardError,
            "Expected a folder containing song.ini under the build output. Onyx said (last lines):");
        builder.Append("       Nothing was imported from this input.");
        return builder.ToString();
    }

    private static OnyxInputFailure BuildLayoutDriftFailure(DlcContainer input, string reason)
    {
        var builder = new StringBuilder();
        builder.Append(string.Create(
            CultureInfo.InvariantCulture,
            $"Onyx produced an unexpected project layout for '{input.Path}': {reason}."));
        builder.AppendLine();
        builder.Append("       Onyx output was discarded; nothing was imported.");
        return new OnyxInputFailure(input.Path, OnyxFailureKind.LayoutDrift, null, builder.ToString());
    }

    private static void AppendTail(StringBuilder builder, string text, string heading)
    {
        var lines = Sanitize(text)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tail = lines.Length <= MaxDiagnosticLines
            ? lines
            : lines[^MaxDiagnosticLines..];

        if (tail.Length == 0)
        {
            builder.AppendLine(string.Create(CultureInfo.InvariantCulture, $"       {heading} (no stderr output)"));
            return;
        }

        builder.AppendLine(string.Create(CultureInfo.InvariantCulture, $"       {heading}"));
        foreach (var line in tail)
        {
            builder.AppendLine(string.Create(CultureInfo.InvariantCulture, $"         | {line}"));
        }
    }

    private static bool MentionsLicenceProblem(string text) =>
        text.Contains("rap", StringComparison.OrdinalIgnoreCase)
        || text.Contains("license", StringComparison.OrdinalIgnoreCase)
        || text.Contains("licence", StringComparison.OrdinalIgnoreCase)
        || text.Contains("decrypt", StringComparison.OrdinalIgnoreCase)
        || text.Contains("key", StringComparison.OrdinalIgnoreCase);

    private static string FormatTimeout(TimeSpan timeout) =>
        timeout.TotalMinutes >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{timeout.TotalMinutes:0.#} minutes")
            : string.Create(CultureInfo.InvariantCulture, $"{timeout.TotalSeconds:0.#} seconds");

    private static string Cap(string text) =>
        text.Length <= MaxCapturedCharacters ? text : text[^MaxCapturedCharacters..];

    private static string Sanitize(string text)
    {
        var withoutAnsi = AnsiEscapeRegex().Replace(text, string.Empty);
        var builder = new StringBuilder(withoutAnsi.Length);
        foreach (var character in withoutAnsi)
        {
            if (character is '\n' or '\t' || !char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscapeRegex();

    private sealed record ProcessOutcome(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool TimedOut,
        string? StartError);
}
