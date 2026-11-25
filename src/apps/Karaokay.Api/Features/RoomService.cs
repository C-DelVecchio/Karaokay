using Karaokay.Application;
using Karaokay.Domain;
using Karaokay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Karaokay.Api.Features;

/// <summary>
/// Creates live rooms, reserving the four-letter code in the <see cref="RoomRegistry"/> and then
/// persisting the <see cref="PartyRoom"/> row (docs/ARCHITECTURE.md §4.2). The database unique
/// index is the backstop: a unique violation removes the in-memory reservation and the next code
/// is generated. Without a configured database (tests, bare local runs) rooms are in-memory only.
/// </summary>
public sealed partial class RoomService
{
    private readonly RoomRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RoomService> _logger;

    public RoomService(
        RoomRegistry registry,
        IServiceScopeFactory scopeFactory,
        ILogger<RoomService> logger)
    {
        _registry = registry;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Finds a live room by code.</summary>
    public RoomState? Find(string code) => _registry.Find(code);

    /// <summary>
    /// Creates a room with a code that is unused in this process and (when persistence is on) in
    /// the database. Retries on unique violations; throws when the code space is exhausted.
    /// </summary>
    public async Task<RoomState> CreateRoomAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < RoomCodeGenerator.MaxAttempts; attempt++)
        {
            var generator = new RoomCodeGenerator(activeCodes: _registry.ActiveCodes);
            var code = generator.GenerateNewCode();
            var room = new RoomState(Guid.NewGuid(), code, DateTime.UtcNow);

            if (!_registry.TryAdd(room))
            {
                continue;
            }

            try
            {
                await PersistRoomAsync(room, cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException exception) when (exception.IsUniqueConstraintViolation())
            {
                // The unique index on Rooms.Code: another process or a stale row owns the code.
                // Release the reservation and try a fresh code.
                _registry.Remove(code);
                LogCodeCollision(_logger, code, exception);
                continue;
            }
            catch
            {
                _registry.Remove(code);
                throw;
            }

            LogRoomCreated(_logger, code);
            return room;
        }

        throw new InvalidOperationException(
            $"Could not create a room with a free {RoomCodeGenerator.CodeLength}-character code " +
            $"after {RoomCodeGenerator.MaxAttempts} attempts.");
    }

    private async Task PersistRoomAsync(RoomState room, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetService<KaraokayDbContext>();
        if (database is null)
        {
            return;
        }

        database.Rooms.Add(new PartyRoom
        {
            Id = room.Id,
            Code = room.Code,
            CreatedAtUtc = room.CreatedAtUtc,
            Status = PlaybackStatus.Idle,
            GuideVocalVolume = room.GuideVocalVolume,
        });

        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created party room {RoomCode}")]
    private static partial void LogRoomCreated(ILogger logger, string roomCode);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Room code {RoomCode} collided with the database unique index; regenerating")]
    private static partial void LogCodeCollision(ILogger logger, string roomCode, Exception exception);
}
