namespace Karaokay.Api.Features;

/// <summary>
/// Runtime feature flags (docs/ARCHITECTURE.md §1.2 C3). The public demo disables YouTube
/// ingestion so the pipeline has no storage/bandwidth exposure; self-host enables it in Slice 5.
/// </summary>
public sealed class FeatureOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Features";

    /// <summary>When false, search and YouTube enqueue paths return a documented stub/error.</summary>
    public bool EnableYouTubeIngestion { get; set; }
}
