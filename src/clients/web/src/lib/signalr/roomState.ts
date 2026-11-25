import { DownloadStage, QueueItemState } from './protocol';
import type {
  DownloadFailedDto,
  DownloadProgressDto,
  PlaybackStateDto,
  QueueDelta,
  QueueItemDto,
  RoomSnapshot,
  SongReadyDto,
} from './protocol';
import { shouldAcceptDelta } from './sync';

/**
 * Per-queue-item YouTube ingestion state. `SongReady`/`DownloadFailed`/`QueueItemStateChanged`
 * all fold into the same record so a queue row has one badge source of truth.
 */
export interface QueueDownloadState {
  stage: DownloadStage;
  percent: number;
  bytesPerSec: number;
  etaSec: number;
  ready: boolean;
  /** True when the item was already on disk when enqueued (no progress events ever arrive). */
  cached: boolean;
  failureReason: string | null;
}

/**
 * Pure client-side room state. `RoomSnapshot` is the single source of truth (join and rejoin take
 * the same path); deltas are applied only when their sequence is contiguous.
 *
 * `downloads` and `youtubeSongIds` are derived from transient push events rather than the
 * snapshot, so they are preserved across a rejoin and pruned when the queue shrinks.
 */
export interface RemoteRoomState {
  snapshot: RoomSnapshot | null;
  playback: PlaybackStateDto | null;
  queue: QueueItemDto[];
  guestCount: number;
  queueSequence: number | null;
  downloads: Record<string, QueueDownloadState>;
  youtubeSongIds: string[];
}

export function createEmptyRoomState(): RemoteRoomState {
  return {
    snapshot: null,
    playback: null,
    queue: [],
    guestCount: 0,
    queueSequence: null,
    downloads: {},
    youtubeSongIds: [],
  };
}

function sortQueue(queue: readonly QueueItemDto[]): QueueItemDto[] {
  return [...queue].sort((left, right) =>
    left.position !== right.position
      ? left.position - right.position
      : left.id.localeCompare(right.id));
}

function addSongId(songIds: readonly string[], songId: string): string[] {
  return songIds.includes(songId) ? [...songIds] : [...songIds, songId];
}

function pruneDownloads(
  downloads: Record<string, QueueDownloadState>,
  queue: readonly QueueItemDto[],
): Record<string, QueueDownloadState> {
  const live = new Set(queue.map((item) => item.id));
  const next: Record<string, QueueDownloadState> = {};
  for (const [queueItemId, download] of Object.entries(downloads)) {
    if (live.has(queueItemId)) {
      next[queueItemId] = download;
    }
  }

  return next;
}

/** Applies a full snapshot. Idempotent: applying the same snapshot twice is a no-op in value. */
export function applySnapshot(
  state: RemoteRoomState,
  snapshot: RoomSnapshot,
): RemoteRoomState {
  const queue = sortQueue(snapshot.queue);
  return {
    snapshot,
    playback: snapshot.playback,
    queue,
    guestCount: snapshot.guestCount,
    queueSequence: snapshot.queueSequence,
    downloads: pruneDownloads(state.downloads, queue),
    youtubeSongIds: state.youtubeSongIds,
  };
}

/** Applies a contiguous delta; a gap/duplicate leaves the state untouched (caller re-syncs). */
export function applyDelta(state: RemoteRoomState, delta: QueueDelta): RemoteRoomState {
  if (state.queueSequence !== null) {
    if (!shouldAcceptDelta(state.queueSequence, delta.sequence)) {
      return state;
    }
  }

  const removed = new Set(delta.removed);
  let queue = state.queue.filter((item) => !removed.has(item.id));

  for (const added of delta.added) {
    if (!queue.some((item) => item.id === added.id)) {
      queue.push(added);
    }
  }

  for (const updated of delta.updated) {
    const index = queue.findIndex((item) => item.id === updated.id);
    if (index >= 0) {
      queue[index] = updated;
    } else {
      queue.push(updated);
    }
  }

  queue = sortQueue(queue);

  return {
    ...state,
    queue,
    queueSequence: delta.sequence,
    downloads: pruneDownloads(state.downloads, queue),
    snapshot: state.snapshot
      ? { ...state.snapshot, queue, queueSequence: delta.sequence }
      : null,
  };
}

/** Applies an authoritative playback projection (position/status/volume/current song). */
export function applyPlayback(
  state: RemoteRoomState,
  playback: PlaybackStateDto,
): RemoteRoomState {
  return {
    ...state,
    playback,
    snapshot: state.snapshot
      ? { ...state.snapshot, status: playback.status, playback }
      : null,
  };
}

/** Applies the latest participant count from `ParticipantJoined`/`ParticipantLeft`. */
export function applyGuestCount(
  state: RemoteRoomState,
  guestCount: number,
): RemoteRoomState {
  if (state.guestCount === guestCount) {
    return state;
  }

  return {
    ...state,
    guestCount,
    snapshot: state.snapshot ? { ...state.snapshot, guestCount } : null,
  };
}

/** Replaces one queue row's lifecycle state without consuming a queue sequence. */
export function applyQueueItemState(
  state: RemoteRoomState,
  queueItemId: string,
  nextState: QueueItemState,
): RemoteRoomState {
  const queue = state.queue.map((item) =>
    item.id === queueItemId && item.state !== nextState
      ? { ...item, state: nextState }
      : item);

  let downloads = state.downloads;
  if (nextState === QueueItemState.Ready) {
    const previous = downloads[queueItemId];
    downloads = {
      ...downloads,
      [queueItemId]: previous
        ? { ...previous, ready: true, failureReason: null, stage: DownloadStage.Completed }
        // A cache hit is enqueued Ready before any progress event exists.
        : {
            stage: DownloadStage.Completed,
            percent: 100,
            bytesPerSec: 0,
            etaSec: 0,
            ready: true,
            cached: true,
            failureReason: null,
          },
    };
  } else if (nextState === QueueItemState.Failed) {
    const previous = downloads[queueItemId];
    downloads = {
      ...downloads,
      [queueItemId]: {
        stage: DownloadStage.Failed,
        percent: previous?.percent ?? 0,
        bytesPerSec: previous?.bytesPerSec ?? 0,
        etaSec: previous?.etaSec ?? 0,
        ready: false,
        cached: false,
        failureReason: previous?.failureReason ?? 'Download failed.',
      },
    };
  }

  return {
    ...state,
    queue,
    downloads,
    snapshot: state.snapshot ? { ...state.snapshot, queue } : null,
  };
}

function queueStateForProgress(stage: DownloadStage): QueueItemState {
  switch (stage) {
    case DownloadStage.Queued:
      return QueueItemState.Queued;
    case DownloadStage.Completed:
      return QueueItemState.Ready;
    case DownloadStage.Failed:
      return QueueItemState.Failed;
    default:
      return QueueItemState.Downloading;
  }
}

/** Applies a live `DownloadProgressChanged` push. */
export function applyDownloadProgress(
  state: RemoteRoomState,
  progress: DownloadProgressDto,
): RemoteRoomState {
  const percent = Number.isFinite(progress.percent)
    ? Math.min(100, Math.max(0, progress.percent))
    : 0;
  const previous = state.downloads[progress.queueItemId];
  const completed = progress.stage === DownloadStage.Completed;
  const failed = progress.stage === DownloadStage.Failed;

  const nextDownload: QueueDownloadState = {
    stage: progress.stage,
    percent,
    bytesPerSec: Number.isFinite(progress.bytesPerSec) ? progress.bytesPerSec : 0,
    etaSec: Number.isFinite(progress.etaSec) ? progress.etaSec : 0,
    ready: failed ? false : completed || previous?.ready === true,
    cached: false,
    failureReason: failed ? previous?.failureReason ?? null : null,
  };

  let nextState: RemoteRoomState = {
    ...state,
    downloads: { ...state.downloads, [progress.queueItemId]: nextDownload },
    youtubeSongIds: addSongId(state.youtubeSongIds, progress.songId),
  };

  // A queued/progress push must not downgrade an item the queue already advanced to Playing.
  const item = nextState.queue.find((candidate) => candidate.id === progress.queueItemId);
  if (item) {
    if (completed || failed || item.state === QueueItemState.Queued
      || item.state === QueueItemState.Downloading) {
      nextState = applyQueueItemState(
        nextState,
        progress.queueItemId,
        queueStateForProgress(progress.stage),
      );
    }
  }

  return nextState;
}

/** Applies `SongReady`: the file exists and the row can start immediately. */
export function applySongReady(
  state: RemoteRoomState,
  ready: SongReadyDto,
): RemoteRoomState {
  const previous = state.downloads[ready.queueItemId];
  const withDownload: RemoteRoomState = {
    ...state,
    downloads: {
      ...state.downloads,
      [ready.queueItemId]: {
        stage: DownloadStage.Completed,
        percent: 100,
        bytesPerSec: previous?.bytesPerSec ?? 0,
        etaSec: 0,
        ready: true,
        cached: false,
        failureReason: null,
      },
    },
    youtubeSongIds: addSongId(state.youtubeSongIds, ready.songId),
  };

  return applyQueueItemState(withDownload, ready.queueItemId, QueueItemState.Ready);
}

/** Applies `DownloadFailed`: keeps the reason so the row can show it beside Retry. */
export function applyDownloadFailed(
  state: RemoteRoomState,
  failed: DownloadFailedDto,
): RemoteRoomState {
  const previous = state.downloads[failed.queueItemId];
  const reason = failed.reason?.trim() || 'Download failed.';
  const withDownload: RemoteRoomState = {
    ...state,
    downloads: {
      ...state.downloads,
      [failed.queueItemId]: {
        stage: DownloadStage.Failed,
        percent: previous?.percent ?? 0,
        bytesPerSec: previous?.bytesPerSec ?? 0,
        etaSec: 0,
        ready: false,
        cached: false,
        failureReason: reason,
      },
    },
    youtubeSongIds: addSongId(state.youtubeSongIds, failed.songId),
  };

  return applyQueueItemState(withDownload, failed.queueItemId, QueueItemState.Failed);
}
