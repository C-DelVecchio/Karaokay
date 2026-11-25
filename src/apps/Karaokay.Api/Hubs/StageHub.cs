using Karaokay.Api.Features;
using Karaokay.Contracts.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Karaokay.Api.Hubs;

/// <summary>
/// The single SignalR stage hub at <c>/hubs/stage</c> (docs/ARCHITECTURE.md §4.3/§5.1). A hub
/// instance is created per invocation, so role/room tracking lives in the singleton
/// <see cref="RoomRegistry"/>, keyed by connection id. Joins are idempotent full-state resyncs.
/// </summary>
public sealed partial class StageHub : Hub<IStageHubClient>, IStageHub
{
    private const int MaxDisplayNameLength = 64;

    private static readonly IReadOnlyList<YouTubeSearchResultDto> EmptySearchResults = [];

    private readonly RoomRegistry _registry;
    private readonly RoomService _rooms;
    private readonly QueueService _queue;
    private readonly FeatureOptions _features;
    private readonly ILogger<StageHub> _logger;

    public StageHub(
        RoomRegistry registry,
        RoomService rooms,
        QueueService queue,
        IOptions<FeatureOptions> features,
        ILogger<StageHub> logger)
    {
        _registry = registry;
        _rooms = rooms;
        _queue = queue;
        _features = features.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<JoinAck> JoinRoom(JoinRequest request)
    {
        if (request is null)
        {
            return new JoinAck(false, null, HubErrorCodes.InvalidRequest);
        }

        var code = (request.RoomCode ?? string.Empty).Trim().ToUpperInvariant();
        if (!RoomRegistry.IsValidCode(code))
        {
            return new JoinAck(false, null, HubErrorCodes.InvalidRequest);
        }

        if (!RoomRoles.IsValid(request.Role))
        {
            return new JoinAck(false, null, HubErrorCodes.InvalidRequest);
        }

        var room = _rooms.Find(code);
        if (room is null)
        {
            return new JoinAck(false, null, HubErrorCodes.RoomNotFound);
        }

        await DetachFromPreviousRoomAsync(code).ConfigureAwait(false);
        room.RemoveParticipant(Context.ConnectionId);

        var displayName = NormalizeDisplayName(request.DisplayName);
        var role = RoomRoles.Normalize(request.Role);
        var sessionToken = Guid.NewGuid();
        var participant = new RoomParticipant(
            Context.ConnectionId,
            displayName,
            role,
            sessionToken.ToString("N"),
            sessionToken);

        room.UpsertParticipant(participant);
        _registry.SetMembership(new ConnectionMembership(room.Code, participant));

        await Groups.AddToGroupAsync(Context.ConnectionId, room.Code).ConfigureAwait(false);

        // Join and rejoin take exactly the same path: always a full snapshot, never delta replay.
        await Clients.Caller.JoinedRoom(QueueService.BuildSnapshot(room)).ConfigureAwait(false);
        await Clients.OthersInGroup(room.Code)
            .ParticipantJoined(displayName, room.GuestCount)
            .ConfigureAwait(false);

        LogJoined(_logger, room.Code, Context.ConnectionId, role);
        return new JoinAck(true, participant.SessionToken, null);
    }

    /// <inheritdoc />
    public async Task<Pong> Ping(PingPayload payload)
    {
        await RequireRoomAsync().ConfigureAwait(false);
        return new Pong(payload?.T1 ?? 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<YouTubeSearchResultDto>> SearchYouTube(string query)
    {
        await RequireRoomAsync().ConfigureAwait(false);

        // Slice 5 wires yt-dlp search behind Features:EnableYouTubeIngestion. Until then the
        // honest stub is an empty result list in both flag states.
        LogSearchStub(_logger, query ?? string.Empty, _features.EnableYouTubeIngestion);
        return EmptySearchResults;
    }

    /// <inheritdoc />
    public async Task<QueueAck> EnqueueLibrarySong(Guid songId)
    {
        var (room, participant) = await RequireRoomAsync().ConfigureAwait(false);
        var ack = await _queue.EnqueueLibrarySongAsync(room, participant, songId, CancellationToken.None)
            .ConfigureAwait(false);

        if (!ack.Success)
        {
            await SendErrorAsync(ack.Reason ?? HubErrorCodes.NotFound, "The requested song was not found.")
                .ConfigureAwait(false);
        }

        return ack;
    }

    /// <inheritdoc />
    public async Task<QueueAck> EnqueueYouTubeSong(YouTubeQueueRequest request)
    {
        await RequireRoomAsync().ConfigureAwait(false);

        if (request is null)
        {
            await SendErrorAsync(
                    HubErrorCodes.InvalidRequest,
                    "A YouTube queue request is required.")
                .ConfigureAwait(false);
            throw new HubException("A YouTube queue request is required.");
        }

        if (!_features.EnableYouTubeIngestion)
        {
            await SendErrorAsync(
                    HubErrorCodes.YouTubeDisabled,
                    "YouTube ingestion is disabled on this server.")
                .ConfigureAwait(false);
            return new QueueAck(false, Guid.Empty, HubErrorCodes.YouTubeDisabled);
        }

        // Ingestion itself lands in Slice 5; refuse cleanly rather than enqueue a phantom item.
        await SendErrorAsync(
                HubErrorCodes.NotImplemented,
                "YouTube ingestion is enabled but the download worker is not part of this build.")
            .ConfigureAwait(false);
        return new QueueAck(false, Guid.Empty, HubErrorCodes.NotImplemented);
    }

    /// <inheritdoc />
    public async Task RemoveQueueItem(Guid queueItemId)
    {
        var (room, _) = await RequireRoomAsync().ConfigureAwait(false);
        if (!await _queue.RemoveQueueItemAsync(room, queueItemId, CancellationToken.None).ConfigureAwait(false))
        {
            await SendErrorAsync(HubErrorCodes.NotFound, "That queue item no longer exists.")
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task MoveQueueItem(Guid queueItemId, int newPosition)
    {
        var (room, _) = await RequireRoomAsync().ConfigureAwait(false);
        if (!await _queue.MoveQueueItemAsync(room, queueItemId, newPosition, CancellationToken.None)
                .ConfigureAwait(false))
        {
            await SendErrorAsync(HubErrorCodes.NotFound, "That queue item no longer exists.")
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task StartPlayback()
    {
        var (room, _) = await RequireRoomAsync().ConfigureAwait(false);
        await _queue.StartPlaybackAsync(room, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PausePlayback()
    {
        var (room, _) = await RequireRoomAsync().ConfigureAwait(false);
        await _queue.PausePlaybackAsync(room, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ResumePlayback()
    {
        var (room, _) = await RequireRoomAsync().ConfigureAwait(false);
        await _queue.ResumePlaybackAsync(room, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SkipSong()
    {
        var (room, _) = await RequireRoomAsync().ConfigureAwait(false);
        await _queue.SkipSongAsync(room, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RestartSong()
    {
        var (room, _) = await RequireRoomAsync().ConfigureAwait(false);
        await _queue.RestartSongAsync(room, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReportSongEnded(Guid queueItemId)
    {
        var (room, _) = await RequireStageAsync().ConfigureAwait(false);

        // Idempotent by construction: anything but the current item is silently ignored so a
        // duplicate stage report and the future watchdog can never double-advance (§5.1 rule 1).
        await _queue.ReportSongEndedAsync(room, queueItemId, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetGuideVocalVolume(double volume)
    {
        var (room, _) = await RequireRoomAsync().ConfigureAwait(false);
        await _queue.SetGuideVocalVolumeAsync(room, volume, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateStageCapabilities(StageCapabilities caps)
    {
        var (room, _) = await RequireStageAsync().ConfigureAwait(false);

        if (caps is null)
        {
            await SendErrorAsync(
                    HubErrorCodes.InvalidRequest,
                    "Stage capabilities are required.")
                .ConfigureAwait(false);
            throw new HubException("Stage capabilities are required.");
        }

        lock (room.SyncRoot)
        {
            room.StageCapabilities = caps;
        }

        LogCapabilities(_logger, room.Code, caps.AudioLatencyEstimateMs);
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var membership = _registry.RemoveMembership(Context.ConnectionId);
        if (membership is not null)
        {
            var room = _registry.Find(membership.RoomCode);
            var left = room?.RemoveParticipant(Context.ConnectionId);
            if (room is not null && left is not null)
            {
                await Clients.Group(room.Code)
                    .ParticipantLeft(left.DisplayName, room.GuestCount)
                    .ConfigureAwait(false);
                LogLeft(_logger, room.Code, left.DisplayName);
            }
        }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    private async Task<(RoomState Room, RoomParticipant Participant)> RequireRoomAsync()
    {
        var membership = _registry.FindMembership(Context.ConnectionId);
        var room = membership is null ? null : _registry.Find(membership.RoomCode);
        if (membership is null || room is null)
        {
            await SendErrorAsync(HubErrorCodes.NotJoined, "Join a room before invoking room commands.")
                .ConfigureAwait(false);
            throw new HubException("Join a room before invoking room commands.");
        }

        return (room, membership.Participant);
    }

    private async Task<(RoomState Room, RoomParticipant Participant)> RequireStageAsync()
    {
        var (room, participant) = await RequireRoomAsync().ConfigureAwait(false);
        if (!participant.IsStage)
        {
            await SendErrorAsync(
                    HubErrorCodes.ForbiddenRole,
                    "This operation is only available to the stage.")
                .ConfigureAwait(false);
            throw new HubException("This operation is only available to the stage.");
        }

        return (room, participant);
    }

    private Task SendErrorAsync(string code, string message) =>
        Clients.Caller.HubError(new HubErrorDto(code, message));

    private async Task DetachFromPreviousRoomAsync(string newCode)
    {
        var previous = _registry.RemoveMembership(Context.ConnectionId);
        if (previous is null || string.Equals(previous.RoomCode, newCode, StringComparison.Ordinal))
        {
            return;
        }

        var previousRoom = _registry.Find(previous.RoomCode);
        var left = previousRoom?.RemoveParticipant(Context.ConnectionId);
        if (previousRoom is not null && left is not null)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, previousRoom.Code).ConfigureAwait(false);
            await Clients.Group(previousRoom.Code)
                .ParticipantLeft(left.DisplayName, previousRoom.GuestCount)
                .ConfigureAwait(false);
        }
    }

    private static string NormalizeDisplayName(string? displayName)
    {
        var trimmed = displayName?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return "Guest";
        }

        return trimmed.Length <= MaxDisplayNameLength
            ? trimmed
            : trimmed[..MaxDisplayNameLength];
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connection {ConnectionId} joined room {RoomCode} as {Role}")]
    private static partial void LogJoined(ILogger logger, string roomCode, string connectionId, string role);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SearchYouTube stub invoked for '{Query}' (ingestion enabled: {Enabled})")]
    private static partial void LogSearchStub(ILogger logger, string query, bool enabled);

    [LoggerMessage(Level = LogLevel.Information, Message = "Room {RoomCode} stage latency estimate is {LatencyMs} ms")]
    private static partial void LogCapabilities(ILogger logger, string roomCode, int latencyMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "Participant {DisplayName} left room {RoomCode}")]
    private static partial void LogLeft(ILogger logger, string roomCode, string displayName);
}
