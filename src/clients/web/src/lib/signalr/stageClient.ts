import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
} from '@microsoft/signalr';
import {
  CLIENT_METHODS,
  HUB_METHODS,
  STAGE_HUB_URL,
  type HubErrorDto,
  type HubMethod,
  type HubRole,
  type JoinAck,
  type JoinRequest,
  type PingPayload,
  type PlaybackStateDto,
  type Pong,
  type QueueAck,
  type QueueDelta,
  type QueueItemState,
  type RoomSnapshot,
  type StageCapabilities,
  type StageConnectionStatus,
  type DownloadFailedDto,
  type DownloadProgressDto,
  type SongReadyDto,
  type CountdownDto,
  type YouTubeQueueRequest,
  type YouTubeSearchResultDto,
} from './protocol';
import { decideReconnectAction, shouldAcceptDelta } from './sync';

export interface StageClientHandlers {
  onJoinedRoom?: (snapshot: RoomSnapshot) => void;
  onQueueChanged?: (delta: QueueDelta) => void;
  onQueueItemStateChanged?: (queueItemId: string, state: QueueItemState) => void;
  onDownloadProgressChanged?: (progress: DownloadProgressDto) => void;
  onSongReady?: (ready: SongReadyDto) => void;
  onDownloadFailed?: (failed: DownloadFailedDto) => void;
  onPlaybackChanged?: (playback: PlaybackStateDto) => void;
  onCountdownTicked?: (tick: CountdownDto) => void;
  onParticipantJoined?: (displayName: string, guestCount: number) => void;
  onParticipantLeft?: (displayName: string, guestCount: number) => void;
  onHubError?: (error: HubErrorDto) => void;
  onRoomClosed?: (reason: string) => void;
  onStatusChanged?: (status: StageConnectionStatus) => void;
}

interface JoinOptions {
  roomCode: string;
  displayName: string;
  role: HubRole;
}

const RECONNECT_DELAYS_MS = [0, 1_000, 3_000, 5_000, 10_000];

/**
 * Typed SignalR proxy for `/hubs/stage`. No React coupling: the hook in `features/remote`
 * subscribes through {@link StageClientHandlers}.
 *
 * Reconnection is snapshot-based (docs/ARCHITECTURE.md §5.2): after an automatic reconnect the
 * client re-invokes `JoinRoom`, and the server's `JoinedRoom` push restores all state. Queue deltas
 * are accepted only while their monotonic sequence is contiguous; a gap triggers the same rejoin.
 */
export class StageClient {
  private readonly handlersRef: { current: StageClientHandlers };
  private connection: HubConnection | null = null;
  private joinOptions: JoinOptions | null = null;
  private sessionToken: string | null = null;
  private lastQueueSequence: number | null = null;
  private status: StageConnectionStatus = 'idle';
  private resyncing = false;

  constructor(handlers: StageClientHandlers = {}) {
    this.handlersRef = { current: handlers };
  }

  /** Replaces the event handlers without dropping the connection. */
  setHandlers(handlers: StageClientHandlers): void {
    this.handlersRef.current = handlers;
  }

  getStatus(): StageConnectionStatus {
    return this.status;
  }

  getSessionToken(): string | null {
    return this.sessionToken;
  }

  getLastQueueSequence(): number | null {
    return this.lastQueueSequence;
  }

  /** Connects (if needed) and joins a room. A successful join arms snapshot-based reconnection. */
  async join(roomCode: string, displayName: string, role: HubRole = 'guest'): Promise<JoinAck> {
    const connection = await this.ensureStarted();
    const request: JoinRequest = {
      roomCode: roomCode.trim().toUpperCase(),
      displayName: displayName.trim(),
      role,
    };

    const ack = await connection.invoke<JoinAck>(HUB_METHODS.JoinRoom, request);
    if (ack.success) {
      this.joinOptions = request;
      this.sessionToken = ack.sessionToken;
      // Do not reset lastQueueSequence here: the server always pushes JoinedRoom before the
      // JoinAck completes, and its handler seeds the sequence. Clearing it after the ack would
      // reopen the guard on the first post-resync delta.
    }

    return ack;
  }

  /**
   * Manually restarts a closed connection and re-joins the remembered room. Safe to call when no
   * room was joined (it only connects).
   */
  async restart(): Promise<void> {
    await this.ensureStarted();
    if (this.joinOptions) {
      await this.resync();
    }
  }

  async ping(): Promise<Pong> {
    // Ensure the connection first, then stamp t1 in the same synchronous stretch as the send:
    // SignalR buffers the frame synchronously inside `invoke`, so a main-thread stall either
    // happens before the stamp (harmless) or after the frame is already queued.
    const connection = await this.ensureStarted();
    const t1 = Date.now();
    return connection.invoke<Pong>(HUB_METHODS.Ping, { t1 } satisfies PingPayload);
  }

  async searchYouTube(query: string): Promise<YouTubeSearchResultDto[]> {
    return this.invoke<YouTubeSearchResultDto[]>(HUB_METHODS.SearchYouTube, query);
  }

  async enqueueLibrarySong(songId: string): Promise<QueueAck> {
    return this.invoke<QueueAck>(HUB_METHODS.EnqueueLibrarySong, songId);
  }

  async enqueueYouTubeSong(request: YouTubeQueueRequest): Promise<QueueAck> {
    return this.invoke<QueueAck>(HUB_METHODS.EnqueueYouTubeSong, request);
  }

  async removeQueueItem(queueItemId: string): Promise<void> {
    await this.invoke<void>(HUB_METHODS.RemoveQueueItem, queueItemId);
  }

  async moveQueueItem(queueItemId: string, newPosition: number): Promise<void> {
    await this.invoke<void>(HUB_METHODS.MoveQueueItem, queueItemId, newPosition);
  }

  async startPlayback(): Promise<void> {
    await this.invoke<void>(HUB_METHODS.StartPlayback);
  }

  async pausePlayback(): Promise<void> {
    await this.invoke<void>(HUB_METHODS.PausePlayback);
  }

  async resumePlayback(): Promise<void> {
    await this.invoke<void>(HUB_METHODS.ResumePlayback);
  }

  async skipSong(): Promise<void> {
    await this.invoke<void>(HUB_METHODS.SkipSong);
  }

  async restartSong(): Promise<void> {
    await this.invoke<void>(HUB_METHODS.RestartSong);
  }

  async reportSongEnded(queueItemId: string): Promise<void> {
    await this.invoke<void>(HUB_METHODS.ReportSongEnded, queueItemId);
  }

  async setGuideVocalVolume(volume: number): Promise<void> {
    await this.invoke<void>(HUB_METHODS.SetGuideVocalVolume, volume);
  }

  async updateStageCapabilities(capabilities: StageCapabilities): Promise<void> {
    await this.invoke<void>(HUB_METHODS.UpdateStageCapabilities, capabilities);
  }

  /** Stops the connection and forgets the joined room. */
  async dispose(): Promise<void> {
    const connection = this.connection;
    this.connection = null;
    this.joinOptions = null;
    this.sessionToken = null;
    this.lastQueueSequence = null;
    this.setStatus('idle');

    if (connection) {
      await connection.stop().catch(() => undefined);
    }
  }

  private async invoke<T>(method: HubMethod, ...args: unknown[]): Promise<T> {
    const connection = await this.ensureStarted();
    return connection.invoke<T>(method, ...args);
  }

  private async ensureStarted(): Promise<HubConnection> {
    const connection = this.connection ?? this.createConnection();
    if (connection.state === HubConnectionState.Disconnected) {
      this.setStatus('connecting');
      try {
        await connection.start();
        this.setStatus('connected');
      } catch (error) {
        this.setStatus('disconnected');
        throw error;
      }
    }

    return connection;
  }

  private createConnection(): HubConnection {
    const connection = new HubConnectionBuilder()
      .withUrl(STAGE_HUB_URL)
      .withAutomaticReconnect(RECONNECT_DELAYS_MS)
      .configureLogging(LogLevel.None)
      .build();

    connection.on(CLIENT_METHODS.JoinedRoom, (snapshot: RoomSnapshot) => {
      // The snapshot carries the authoritative sequence; seed the guard from it so a reconnect
      // continues the same sequence instead of resetting to "unknown".
      this.lastQueueSequence = snapshot.queueSequence;
      this.handlersRef.current.onJoinedRoom?.(snapshot);
    });

    connection.on(CLIENT_METHODS.QueueChanged, (delta: QueueDelta) => {
      if (!shouldAcceptDelta(this.lastQueueSequence, delta.sequence)) {
        void this.resync();
        return;
      }

      this.lastQueueSequence = delta.sequence;
      this.handlersRef.current.onQueueChanged?.(delta);
    });

    connection.on(
      CLIENT_METHODS.QueueItemStateChanged,
      (queueItemId: string, state: QueueItemState) => {
        this.handlersRef.current.onQueueItemStateChanged?.(queueItemId, state);
      },
    );

    connection.on(
      CLIENT_METHODS.DownloadProgressChanged,
      (progress: DownloadProgressDto) => {
        this.handlersRef.current.onDownloadProgressChanged?.(progress);
      },
    );

    connection.on(CLIENT_METHODS.SongReady, (ready: SongReadyDto) => {
      this.handlersRef.current.onSongReady?.(ready);
    });

    connection.on(CLIENT_METHODS.DownloadFailed, (failed: DownloadFailedDto) => {
      this.handlersRef.current.onDownloadFailed?.(failed);
    });

    connection.on(CLIENT_METHODS.PlaybackChanged, (playback: PlaybackStateDto) => {
      this.handlersRef.current.onPlaybackChanged?.(playback);
    });

    connection.on(CLIENT_METHODS.CountdownTicked, (tick: CountdownDto) => {
      this.handlersRef.current.onCountdownTicked?.(tick);
    });

    connection.on(
      CLIENT_METHODS.ParticipantJoined,
      (displayName: string, guestCount: number) => {
        this.handlersRef.current.onParticipantJoined?.(displayName, guestCount);
      },
    );

    connection.on(
      CLIENT_METHODS.ParticipantLeft,
      (displayName: string, guestCount: number) => {
        this.handlersRef.current.onParticipantLeft?.(displayName, guestCount);
      },
    );

    connection.on(CLIENT_METHODS.HubError, (error: HubErrorDto) => {
      this.handlersRef.current.onHubError?.(error);
    });

    connection.on(CLIENT_METHODS.RoomClosed, (reason: string) => {
      this.handlersRef.current.onRoomClosed?.(reason);
    });

    connection.onreconnecting(() => {
      this.setStatus('reconnecting');
    });

    connection.onreconnected(() => {
      this.setStatus('connected');
      if (decideReconnectAction(this.joinOptions !== null, 'connected') === 'rejoin') {
        void this.resync();
      }
    });

    connection.onclose(() => {
      this.setStatus('disconnected');
    });

    this.connection = connection;
    return connection;
  }

  /** Idempotent full-state resync; the server answers with a new `JoinedRoom` snapshot. */
  private async resync(): Promise<void> {
    if (!this.joinOptions || this.resyncing) {
      return;
    }

    this.resyncing = true;
    try {
      const ack = await this.connection?.invoke<JoinAck>(HUB_METHODS.JoinRoom, this.joinOptions);
      if (ack?.success) {
        this.sessionToken = ack.sessionToken;
      }
    } catch {
      // Best effort: if the connection is still unstable, SignalR's reconnect loop or the next
      // user action retries. The snapshot path remains the only state restore.
    } finally {
      this.resyncing = false;
    }
  }

  private setStatus(status: StageConnectionStatus): void {
    if (this.status === status) {
      return;
    }

    this.status = status;
    this.handlersRef.current.onStatusChanged?.(status);
  }
}
