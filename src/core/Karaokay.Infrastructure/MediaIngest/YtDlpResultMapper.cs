using System.Globalization;
using System.Text.Json;
using Karaokay.Contracts;
using Karaokay.Contracts.Hubs;

namespace Karaokay.Infrastructure.MediaIngest;

/// <summary>
/// Pure mapper for yt-dlp's collapsed <c>--flat-playlist --dump-single-json</c> search output
/// (docs/ARCHITECTURE.md §7, Slice 5). Kept separate from <see cref="YtDlpClient"/> so the
/// parsing rules can be tested against recorded fixtures without starting a process.
/// </summary>
internal static class YtDlpResultMapper
{
    /// <summary>Maps the JSON document into typed DTOs, dropping entries with invalid video ids.</summary>
    public static IReadOnlyList<YouTubeSearchResultDto> MapSearchResults(string json, int limit)
    {
        if (string.IsNullOrWhiteSpace(json) || limit <= 0)
        {
            return [];
        }

        using var document = ParseDocument(json);
        var root = document.RootElement;

        var entries = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("entries", out var playlist)
            && playlist.ValueKind == JsonValueKind.Array
                ? playlist.EnumerateArray().ToArray()
                : [root];

        var results = new List<YouTubeSearchResultDto>(Math.Min(entries.Length, limit));
        foreach (var entry in entries)
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = ReadString(entry, "id");
            if (!VideoIdValidator.IsValid(id))
            {
                continue;
            }

            var title = ReadString(entry, "title") ?? id!;
            var channel = ReadString(entry, "channel")
                ?? ReadString(entry, "uploader")
                ?? ReadString(entry, "creator")
                ?? string.Empty;

            results.Add(new YouTubeSearchResultDto(
                id!,
                title,
                channel,
                ReadNumber(entry, "duration"),
                ReadThumbnail(entry, id!)));

            if (results.Count >= limit)
            {
                break;
            }
        }

        return results;
    }

    /// <summary>
    /// Parses yt-dlp's JSON, converting a syntax error into <see cref="YouTubeServiceException"/>
    /// so REST returns a clean 502 and the hub pushes a <c>HubError</c> instead of faulting the
    /// invocation with a raw <see cref="JsonException"/>.
    /// </summary>
    private static JsonDocument ParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new YouTubeServiceException(
                "yt-dlp returned malformed JSON; the YouTube results could not be parsed.",
                exception);
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static double ReadNumber(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var numeric))
        {
            return numeric;
        }

        return property.ValueKind == JsonValueKind.String
            && double.TryParse(
                property.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : 0;
    }

    private static string ReadThumbnail(JsonElement element, string videoId)
    {
        if (element.TryGetProperty("thumbnails", out var thumbnails)
            && thumbnails.ValueKind == JsonValueKind.Array)
        {
            string? last = null;
            foreach (var thumbnail in thumbnails.EnumerateArray())
            {
                var url = ReadString(thumbnail, "url");
                if (!string.IsNullOrWhiteSpace(url))
                {
                    last = url;
                }
            }

            if (last is not null)
            {
                return last;
            }
        }

        return $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";
    }
}
