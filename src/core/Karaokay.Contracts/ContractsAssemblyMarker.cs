namespace Karaokay.Contracts;

/// <summary>
/// Assembly marker for DTOs shared with clients and the canonical
/// <c>lyrics.json</c> types. This project is the single source of truth for
/// wire shapes; it must not reference other Karaokay projects or NuGet
/// packages, and architecture tests enforce that rule.
/// </summary>
public static class ContractsAssemblyMarker
{
}
