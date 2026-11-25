namespace Karaokay.Infrastructure.Persistence;

/// <summary>
/// Provider-specific raw SQL fragments used by the EF model (docs/ARCHITECTURE.md §4.2). PostgreSQL
/// quotes identifiers with <c>"</c>, SQL Server with <c>[...]</c>; keeping the fragments in one
/// pure, unit-testable place is what lets both providers migrate from the same entity graph.
/// </summary>
public static class ProviderIndexFilters
{
    /// <summary>Filtered unique index predicate for <c>Song.VideoId</c> on PostgreSQL.</summary>
    public const string NpgsqlVideoId = "\"VideoId\" IS NOT NULL";

    /// <summary>Filtered unique index predicate for <c>Song.VideoId</c> on SQL Server.</summary>
    public const string SqlServerVideoId = "[VideoId] IS NOT NULL";

    /// <summary>
    /// Returns the <c>Song.VideoId</c> filtered-index predicate for an EF Core provider name
    /// (for example <c>"Npgsql.EntityFrameworkCore.PostgreSQL"</c>). Unknown providers get the
    /// SQL-standard quoted form, which matches the Npgsql/PostgreSQL target.
    /// </summary>
    public static string VideoIdFilterFor(string? providerName)
    {
        if (providerName is null)
        {
            return NpgsqlVideoId;
        }

        if (providerName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            return SqlServerVideoId;
        }

        return NpgsqlVideoId;
    }
}
