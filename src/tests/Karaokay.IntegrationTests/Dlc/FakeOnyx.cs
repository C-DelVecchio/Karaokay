using System.Globalization;

namespace Karaokay.IntegrationTests;

/// <summary>
/// Generates a tiny POSIX shell stand-in for the Onyx executable. It records the exact argv of both
/// steps and simulates the project/build shapes the extractor expects, without any DLC content.
/// </summary>
internal static class FakeOnyx
{
    public static string Create(
        string directory,
        string mode,
        out string logPath,
        out string captureDirectory,
        string? preparedSongDirectory = null)
    {
        Directory.CreateDirectory(directory);
        logPath = Path.Combine(directory, "argv.log");
        captureDirectory = Path.Combine(directory, "captured");
        Directory.CreateDirectory(captureDirectory);

        var scriptPath = Path.Combine(directory, "fake-onyx.sh");
        var script = $$"""
            #!/bin/sh
            printf '%s\n' "$*" >> "{{logPath}}"
            mode='{{mode}}'
            prepared='{{preparedSongDirectory ?? string.Empty}}'
            if [ "$1" = "import" ]; then
              case "$mode" in
                fail-import) echo 'fake import exploded' >&2; exit 7 ;;
                fail-ps3) echo 'fake import exploded: RAP licence missing' >&2; exit 5 ;;
                fail-first) case "$2" in *a-fail*) echo 'fake import exploded for a-fail' >&2; exit 7 ;; *) mkdir -p "$4"; printf 'targets:\n  rb3:\n    game: rb3\n' > "$4/song.yml" ;; esac ;;
                ansi-flood) printf '\033[31mboom\033[0m\n' >&2; i=0; while [ $i -lt 30 ]; do printf '\033[33m%s\033[0m\n' 'xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx' >&2; i=$((i+1)); done; exit 9 ;;
                slow) sleep 0.3; mkdir -p "$4"; printf 'targets:\n  rb3:\n    game: rb3\n' > "$4/song.yml" ;;
                drift) mkdir -p "$4"; printf 'targets:\n  rb3:\n    game: rb3\ntail: yes\n' > "$4/song.yml" ;;
                hang) sleep 60 ;;
                *) mkdir -p "$4"; printf 'targets:\n  rb3:\n    game: rb3\n' > "$4/song.yml" ;;
              esac
              exit 0
            fi
            if [ "$1" = "build" ]; then
              case "$mode" in
                fail-build) echo 'fake build exploded' >&2; exit 3 ;;
                hang) sleep 60 ;;
                slow) sleep 0.3; mkdir -p "$6"; printf '[song]\nname = Slow Song\nartist = Slow Artist\n' > "$6/song.ini"; : > "$6/notes.mid"; : > "$6/guitar.ogg" ;;
                nothing) mkdir -p "$6" ;;
                nested) mkdir -p "$6/nested"; printf '[song]\nname = Fake Song\nartist = Fake Artist\n' > "$6/nested/song.ini" ;;
                two-folders) mkdir -p "$6/a-good"; cp -R "$prepared/." "$6/a-good/"; mkdir -p "$6/b-bad"; printf '[song]\nname = Bad Song\nartist = Bad Artist\n' > "$6/b-bad/song.ini"; : > "$6/b-bad/notes.mid" ;;
                fail-first) mkdir -p "$6"; cp -R "$prepared/." "$6/" ;;
                *) cp "$2" "{{captureDirectory}}/song.yml"; mkdir -p "$6"; printf '[song]\nname = Fake Song\nartist = Fake Artist\n' > "$6/song.ini"; : > "$6/notes.mid"; : > "$6/guitar.ogg" ;;
              esac
              exit 0
            fi
            exit 2
            """;

        File.WriteAllText(scriptPath, script);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return scriptPath;
    }

    public static string MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    public static string WriteExecutable(string path, string content = "#!/bin/sh\nexit 0\n")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return MakeExecutable(path);
    }

    public static List<string> ReadLog(string logPath) =>
        File.Exists(logPath)
            ? File.ReadAllLines(logPath).Where(line => line.Length > 0).ToList()
            : [];

    public static string Describe(int exitCode, string output) =>
        string.Create(CultureInfo.InvariantCulture, $"exit={exitCode}\n{output}");
}
