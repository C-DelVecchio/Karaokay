using Karaokay.Domain;
using Karaokay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Karaokay.Api.Features;

/// <summary>
/// Cold-start rehydration (docs/ARCHITECTURE.md §5.1 rule 6, §5.2; Slice 3 review M4). After the
/// migrations/seed hook runs, every non-closed <see cref="Karaokay.Domain.PartyRoom"/> is loaded
/// with its ordered queue and joined songs and its <see cref="RoomState"/> is rebuilt in the
/// singleton <see cref="RoomRegistry"/>, so guests reconnect to the same queue, sequence, playback
/// anchor, and guide-vocal level. Without a configured database this is a no-op.
/// </summary>
public sealed partial class RoomRehydrator
{
    private readonly RoomRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RoomRehydrator> _logger;

    public RoomRehydrator(
        RoomRegistry registry,
        IServiceScopeFactory scopeFactory,
        ILogger<RoomRehydrator> logger)
    {
        _registry = registry;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Rebuilds every non-closed room and returns how many were restored.</summary>
    public async Task<int> RehydrateAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetService<KaraokayDbContext>();
        if (database is null)
        {
            return 0;
        }

        var rooms = await OpenRoomsWithQueue(database.Rooms)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var restored = 0;
        var skipped = 0;

        foreach (var row in rooms)
        {
            var state = RoomStateMapper.Map(row);
            if (_registry.TryAdd(state))
            {
                restored++;
            }
            else
            {
                // Another process (or an already-live room) owns the code; never clobber it.
                skipped++;
            }
        }

        LogRehydrated(_logger, restored, skipped);
        return restored;
    }

    /// <summary>
    /// The exact cold-start query: every open room with its ordered queue and joined songs.
    /// Factored out so the provider translation can be asserted without a database.
    /// </summary>
    internal static IQueryable<PartyRoom> OpenRoomsWithQueue(IQueryable<PartyRoom> rooms)
    {
        ArgumentNullException.ThrowIfNull(rooms);

        return rooms
            .AsNoTracking()
            .Where(room => room.ClosedAtUtc == null)
            .Include(room => room.Queue.OrderBy(item => item.Position))
            .ThenInclude(item => item.Song);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Rehydrated {RestoredCount} room(s) from the database ({SkippedCount} already live)")]
    private static partial void LogRehydrated(ILogger logger, int restoredCount, int skippedCount);
}
