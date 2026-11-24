namespace Karaokay.Api.Features;

/// <summary>
/// Media library configuration. The single <see cref="MediaRoot"/> key lives at the root of
/// <c>appsettings.json</c>; relative paths are resolved against the API content root so the
/// self-host volume layout (<c>/media/library</c>, docs/ARCHITECTURE.md §infra) works unchanged.
/// </summary>
public sealed class MediaLibraryOptions
{
    /// <summary>Configured media root; may be relative to the content root.</summary>
    public string MediaRoot { get; set; } = "media/library";
}
