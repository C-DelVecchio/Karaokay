using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Karaokay.Infrastructure.Persistence;

/// <summary>
/// Detects unique-constraint violations from either supported provider so the room-code retry
/// loop only regenerates on a genuine code collision (docs/ARCHITECTURE.md §4.2).
/// </summary>
public static class DbUpdateExceptionExtensions
{
    /// <summary>True when the exception chain contains a unique-index/constraint violation.</summary>
    public static bool IsUniqueConstraintViolation(this DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case PostgresException postgres
                    when string.Equals(
                        postgres.SqlState,
                        PostgresErrorCodes.UniqueViolation,
                        StringComparison.Ordinal):
                    return true;

                case SqlException sql when sql.Number is 2601 or 2627:
                    return true;
            }
        }

        return false;
    }
}
