namespace Karaokay.Api.Features;

/// <summary>Source-generated startup log messages (CA1848-compliant).</summary>
internal static partial class StartupLog
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Applying {PendingCount} pending Karaokay database migration(s)")]
    public static partial void ApplyingMigrations(ILogger logger, int pendingCount);
}
