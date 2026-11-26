using System.Collections.Concurrent;
using Karaokay.Application;
using Karaokay.Contracts.Hubs;
using Karaokay.Domain;
using PlaybackStatus = Karaokay.Domain.PlaybackStatus;
using QueueItemState = Karaokay.Domain.QueueItemState;

namespace Karaokay.Api.Features;

/// <summary>Join roles understood by the stage hub (docs/ARCHITECTURE.md §4.3).</summary>
public static class RoomRoles
{
    /// <summary>TV/browser stage connection.</summary>
    public const string Stage = "stage";

    /// <summary>Phone remote connection.</summary>
    public const string Guest = "guest";

    /// <summary>Case-insensitive role validation.</summary>
    public static bool IsValid(string? role) =>
        string.Equals(role, Stage, StringComparison.OrdinalIgnoreCase)
        || string.Equals(role, Guest, StringComparison.OrdinalIgnoreCase);

    /// <summary>Canonical lower-case role.</summary>
    public static string Normalize(string role) => role.ToLowerInvariant();
}

/// <summary>A live room participant (one SignalR connection).</summary>
public sealed record RoomParticipant(
    string ConnectionId,
    string DisplayName,
    string Role,
    string SessionToken,
    Guid SessionTokenId)
{
    /// <summary>True when this connection may invoke stage-only hub methods.</summary>
    public bool IsStage => string.Equals(Role, RoomRoles.Stage, StringComparison.Ordinal);

    /// <summary>True for phone remotes.</summary>
    public bool IsGuest => string.Equals(Role, RoomRoles.Guest, StringComparison.Ordinal);
}

/// <summary>Binds a SignalR connection to the room/participant it joined as.</summary>
public sealed record ConnectionMembership(string RoomCode, RoomParticipant Participant);

/// <summary>Mutable in-memory queue row for a live room.</summary>
public sealed class RoomQueueEntry
{
    /// <summary>Queue item identity (mirrors <see cref="QueueItem.Id"/>).</summary>
    public Guid Id { get; init; }

    /// <summary>Requested song identity.</summary>
    public Guid SongId { get; init; }

    /// <summary>Dense zero-based position within the room queue.</summary>
    public int Position { get; set; }

    /// <summary>Display name of the guest who queued it.</summary>
    public string RequestedByName { get; init; } = "";

    /// <summary>Current lifecycle state.</summary>
    public QueueItemState State { get; set; }

    /// <summary>Soft ownership token id, when known.</summary>
    public Guid? SessionTokenId { get; init; }

    /// <summary>Enqueue timestamp (UTC).</summary>
    public DateTime RequestedAtUtc { get; init; }

    /// <summary>Resolved song projection for playback broadcasts (not persisted here).</summary>
    public SongDto? Song { get; init; }
}

/// <summary>
/// Authoritative in-memory state for one live room (docs/ARCHITECTURE.md §4.2/§5.1). The database
/// is the cold-restart fallback; this object is the hot path. Mutations are serialized with
/// <see cref="CommandLock"/> (single-writer shape) and reads take <see cref="SyncRoot"/>.
/// </summary>
public sealed class RoomState
{
    private long _queueSequence;

    internal RoomState(Guid id, string code, DateTime createdAtUtc)
    {
        Id = id;
        Code = code;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>Room identity.</summary>
    public Guid Id { get; }

    /// <summary>Four-letter join code (upper case).</summary>
    public string Code { get; }

    /// <summary>Creation timestamp (UTC).</summary>
    public DateTime CreatedAtUtc { get; }

    /// <summary>Serializes all room commands so state transitions and broadcasts stay ordered.</summary>
    public SemaphoreSlim CommandLock { get; } = new(1, 1);

    /// <summary>Guards the mutable collections and playback fields for snapshot reads.</summary>
    public object SyncRoot { get; } = new();

    /// <summary>Current playback status.</summary>
    public PlaybackStatus Status { get; internal set; }

    /// <summary>Currently playing queue item, when any.</summary>
    public Guid? CurrentQueueItemId { get; internal set; }

    /// <summary>Wall-clock instant when playback began at position zero (UTC).</summary>
    public DateTime? PlaybackStartedAtUtc { get; internal set; }

    /// <summary>Position preserved while paused.</summary>
    public long PausedAtPositionMs { get; internal set; }

    /// <summary>Guide-vocal bus level, 0..1.</summary>
    public double GuideVocalVolume { get; internal set; } = 0.6;

    /// <summary>Beats remaining in the count-in.</summary>
    public int CountdownRemainingBeats { get; internal set; }

    /// <summary>
    /// True while a count-in restored from persisted state (cold restart) has not yet had its beat
    /// cursor re-created. The authority uses this to resume at the next due beat instead of
    /// replaying beats the previous process already broadcast (§5.1 rule 3).
    /// </summary>
    internal bool CountdownAwaitingResume { get; set; }

    /// <summary>Last stage capability report, if any.</summary>
    public StageCapabilities? StageCapabilities { get; internal set; }

    /// <summary>Monotonic per-room queue delta sequence; starts at zero, first delta is one.</summary>
    public long QueueSequence => Interlocked.Read(ref _queueSequence);

    internal List<RoomQueueEntry> QueueEntries { get; } = [];

    internal Dictionary<string, RoomParticipant> ParticipantsByConnection { get; } =
        new(StringComparer.Ordinal);

    /// <summary>Snapshot copy of the queue ordered by position.</summary>
    public IReadOnlyList<RoomQueueEntry> Queue
    {
        get
        {
            lock (SyncRoot)
            {
                return [.. QueueEntries.OrderBy(entry => entry.Position)];
            }
        }
    }

    /// <summary>Snapshot copy of the participants.</summary>
    public IReadOnlyList<RoomParticipant> Participants
    {
        get
        {
            lock (SyncRoot)
            {
                return [.. ParticipantsByConnection.Values];
            }
        }
    }

    /// <summary>Number of connected phone remotes.</summary>
    public int GuestCount
    {
        get
        {
            lock (SyncRoot)
            {
                return ParticipantsByConnection.Values.Count(participant => participant.IsGuest);
            }
        }
    }

    /// <summary>Advances and returns the next monotonic queue sequence.</summary>
    public long NextQueueSequence() => Interlocked.Increment(ref _queueSequence);

    /// <summary>
    /// Restores the persisted queue sequence during cold-start rehydration so new deltas stay
    /// above anything clients observed before the restart (docs/ARCHITECTURE.md §5.2).
    /// </summary>
    internal void RestoreQueueSequence(long sequence) => Interlocked.Exchange(ref _queueSequence, sequence);

    /// <summary>Finds a queue entry by id.</summary>
    public RoomQueueEntry? FindQueueEntry(Guid queueItemId)
    {
        lock (SyncRoot)
        {
            return QueueEntries.FirstOrDefault(entry => entry.Id == queueItemId);
        }
    }

    /// <summary>Registers or replaces a participant for its connection id.</summary>
    public void UpsertParticipant(RoomParticipant participant)
    {
        lock (SyncRoot)
        {
            ParticipantsByConnection[participant.ConnectionId] = participant;
        }
    }

    /// <summary>Removes a participant and returns it, or <c>null</c> when it was not present.</summary>
    public RoomParticipant? RemoveParticipant(string connectionId)
    {
        lock (SyncRoot)
        {
            if (ParticipantsByConnection.Remove(connectionId, out var participant))
            {
                return participant;
            }

            return null;
        }
    }

    /// <summary>Finds a participant by connection id.</summary>
    public RoomParticipant? FindParticipant(string connectionId)
    {
        lock (SyncRoot)
        {
            return ParticipantsByConnection.GetValueOrDefault(connectionId);
        }
    }

    /// <summary>
    /// Finds the participant holding a session token id. REST callers that joined over SignalR can
    /// present the token in a header; the endpoint resolves it to this participant's ownership id.
    /// </summary>
    public RoomParticipant? FindParticipantBySessionToken(Guid sessionTokenId)
    {
        lock (SyncRoot)
        {
            return ParticipantsByConnection.Values
                .FirstOrDefault(participant => participant.SessionTokenId == sessionTokenId);
        }
    }
}

/// <summary>
/// Singleton registry of live rooms and connection→room memberships (docs/ARCHITECTURE.md §4.2).
/// Room codes are reserved here before the database row is written; the unique index on
/// <see cref="PartyRoom.Code"/> remains the cross-process collision backstop.
/// </summary>
public sealed class RoomRegistry
{
    private readonly ConcurrentDictionary<string, RoomState> _rooms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConnectionMembership> _connections =
        new(StringComparer.Ordinal);

    /// <summary>Codes currently reserved in this process.</summary>
    public IReadOnlyCollection<string> ActiveCodes => [.. _rooms.Keys];

    /// <summary>Validates the four-letter code format (the 23-letter alphabet, upper case).</summary>
    public static bool IsValidCode(string? code) => RoomCodeGenerator.IsValidCode(code);

    /// <summary>Reserves a code for a newly created room.</summary>
    public bool TryAdd(RoomState room) => _rooms.TryAdd(room.Code, room);

    /// <summary>Finds a live room by code.</summary>
    public RoomState? Find(string code) =>
        _rooms.GetValueOrDefault(code.ToUpperInvariant());

    /// <summary>
    /// Finds the live room that owns a queue entry. Used by the download worker to recover the
    /// broadcast group from a persisted <c>DownloadJob.QueueItemId</c>.
    /// </summary>
    public RoomState? FindByQueueItemId(Guid queueItemId)
    {
        foreach (var room in _rooms.Values)
        {
            if (room.FindQueueEntry(queueItemId) is not null)
            {
                return room;
            }
        }

        return null;
    }

    /// <summary>Removes a room (used when database persistence proves the code collided).</summary>
    public bool Remove(string code) => _rooms.TryRemove(code, out _);

    /// <summary>Binds a connection to its room membership.</summary>
    public void SetMembership(ConnectionMembership membership) =>
        _connections[membership.Participant.ConnectionId] = membership;

    /// <summary>Finds the room membership for a connection.</summary>
    public ConnectionMembership? FindMembership(string connectionId) =>
        _connections.GetValueOrDefault(connectionId);

    /// <summary>Removes and returns a connection membership.</summary>
    public ConnectionMembership? RemoveMembership(string connectionId) =>
        _connections.Remove(connectionId, out var membership) ? membership : null;
}
