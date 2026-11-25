namespace Karaokay.Domain;

/// <summary>
/// One entry in a room's queue (docs/ARCHITECTURE.md §4.2/§5.1). Positions are dense within a
/// room; reordering swaps and renumbers rather than leaving gaps.
/// </summary>
public class QueueItem
{
    /// <summary>Queue entry identity.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning room.</summary>
    public Guid RoomId { get; set; }

    /// <summary>Requested song.</summary>
    public Guid SongId { get; set; }

    /// <summary>Dense zero-based ordering within the room.</summary>
    public int Position { get; set; }

    /// <summary>Display name of the guest who queued the entry.</summary>
    public string RequestedByName { get; set; } = "";

    /// <summary>Current lifecycle state.</summary>
    public QueueItemState State { get; set; }

    /// <summary>Soft ownership id used for remove rights, when known.</summary>
    public Guid? SessionTokenId { get; set; }

    /// <summary>Enqueue timestamp (UTC).</summary>
    public DateTime RequestedAtUtc { get; set; }

    /// <summary>Owning room navigation.</summary>
    public PartyRoom Room { get; set; } = null!;

    /// <summary>Requested song navigation.</summary>
    public Song Song { get; set; } = null!;
}
