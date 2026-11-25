using System.Text.RegularExpressions;

namespace Karaokay.Contracts;

/// <summary>
/// Single validation point for YouTube video ids (docs/ARCHITECTURE.md §7, Slice 5). A video id is
/// always exactly eleven characters from the URL-safe base64 alphabet; anything else — especially a
/// value that could be parsed by yt-dlp as a command-line flag — is rejected before it reaches the
/// process arguments.
/// </summary>
public static partial class VideoIdValidator
{
    /// <summary>Canonical YouTube video id length.</summary>
    public const int Length = 11;

    /// <summary>True only for exactly eleven URL-safe base64 characters.</summary>
    public static bool IsValid(string? videoId) =>
        videoId is { Length: Length } && VideoIdPattern().IsMatch(videoId);

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)]
    private static partial Regex VideoIdPattern();
}
