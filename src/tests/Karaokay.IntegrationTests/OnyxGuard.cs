using Karaokay.Importer.Dlc;

namespace Karaokay.IntegrationTests;

/// <summary>
/// Onyx is an optional external tool, so tests skip (never fail) when it is absent. Resolution uses
/// the same locator precedence as the product, including the repository-local
/// <c>.tools/squashfs-root/usr/bin/onyx</c> convenience, so the guarded tests run on this machine
/// without any environment setup.
/// </summary>
internal static class OnyxGuard
{
    private static readonly Lazy<(bool Available, string? Executable)> Resolved = new(Detect);

    public static bool IsAvailable => Resolved.Value.Available;

    public static string? ExecutablePath => Resolved.Value.Executable;

    /// <summary>Skips the current test when Onyx cannot be resolved. Call from a [SkippableFact].</summary>
    public static void SkipIfUnavailable() =>
        Skip.If(
            !IsAvailable,
            "Onyx not found (--onyx / KARAOKAY_ONYX_PATH / PATH / .tools); skipping the Onyx integration test.");

    private static (bool Available, string? Executable) Detect() =>
        OnyxLocator.TryResolve((string?)null, out var location, out _)
            ? (true, location.ExecutablePath)
            : (false, null);
}
