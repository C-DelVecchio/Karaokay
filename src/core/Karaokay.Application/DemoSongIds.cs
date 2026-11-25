namespace Karaokay.Application;

/// <summary>
/// Stable ids for the bundled demo songs. Both the database seeder and the no-database filesystem
/// fallback use the same namespace + slug derivation, so a demo track has one id across modes
/// (docs/ARCHITECTURE.md §7, Slice 3).
/// </summary>
public static class DemoSongIds
{
    private static readonly Guid Namespace = DeterministicGuid.Create(
        DeterministicGuid.DnsNamespace,
        "karaokay.demo-songs");

    /// <summary>Deterministic UUIDv5 for a media-folder slug.</summary>
    public static Guid ForSlug(string slug) => DeterministicGuid.Create(Namespace, slug);
}
