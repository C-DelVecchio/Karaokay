import type { components } from '../api/schema';

/**
 * Compile-checked TypeScript mirror of `Karaokay.Contracts.Hubs`
 * (docs/ARCHITECTURE.md §4.3). SignalR's default JSON hub protocol preserves PascalCase method
 * names and camel-cases DTO property names, so every literal and interface here uses that exact
 * wire shape. `Karaokay.UnitTests/SignalRContractTests` regex-parses the method maps below and
 * compares them with the C# interfaces.
 *
 * Keep one method name per line: the contract test is deliberately a simple text scan.
 */

/** Same-origin hub endpoint; Vite proxies it to the API in dev/preview. */
export const STAGE_HUB_URL = '/hubs/stage';

/**
 * Optional REST header carrying the hub session token from `JoinAck` (mirrors
 * `Karaokay.Contracts.YouTubeHttpHeaders.SessionToken`). Enqueue uses it to attribute a queue row
 * to a joined guest without a hub invocation.
 */
export const SESSION_TOKEN_HEADER = 'X-Karaokay-Session-Token';

/** Room-code alphabet shared with `Karaokay.Application.RoomCodeGenerator` (no I/O/Q). */
export const ROOM_CODE_ALPHABET = 'ABCDEFGHJKLMNPRSTUVWXYZ';
export const ROOM_CODE_LENGTH = 4;

/** True for exactly four characters from the 23-letter alphabet (upper case only). */
export function isValidRoomCode(code: string): boolean {
  if (code.length !== ROOM_CODE_LENGTH) {
    return false;
  }

  for (const character of code) {
    if (!ROOM_CODE_ALPHABET.includes(character)) {
      return false;
    }
  }

  return true;
}

/** Client → server method names, exactly matching `IStageHub`. */
export const HUB_METHODS = {
  JoinRoom: 'JoinRoom',
  Ping: 'Ping',
  SearchYouTube: 'SearchYouTube',
  EnqueueLibrarySong: 'EnqueueLibrarySong',
  EnqueueYouTubeSong: 'EnqueueYouTubeSong',
  RemoveQueueItem: 'RemoveQueueItem',
  MoveQueueItem: 'MoveQueueItem',
  StartPlayback: 'StartPlayback',
  PausePlayback: 'PausePlayback',
  ResumePlayback: 'ResumePlayback',
  SkipSong: 'SkipSong',
  RestartSong: 'RestartSong',
  ReportSongEnded: 'ReportSongEnded',
  SetGuideVocalVolume: 'SetGuideVocalVolume',
  UpdateStageCapabilities: 'UpdateStageCapabilities',
} as const;

export type HubMethod = (typeof HUB_METHODS)[keyof typeof HUB_METHODS];

/** Ordered list for contract tests and diagnostics. */
export const HUB_METHOD_NAMES: readonly HubMethod[] = Object.values(HUB_METHODS);

/** Server → client method names, exactly matching `IStageHubClient`. */
export const CLIENT_METHODS = {
  JoinedRoom: 'JoinedRoom',
  ParticipantJoined: 'ParticipantJoined',
  ParticipantLeft: 'ParticipantLeft',
  QueueChanged: 'QueueChanged',
  QueueItemStateChanged: 'QueueItemStateChanged',
  DownloadProgressChanged: 'DownloadProgressChanged',
  SongReady: 'SongReady',
  DownloadFailed: 'DownloadFailed',
  PlaybackChanged: 'PlaybackChanged',
  CountdownTicked: 'CountdownTicked',
  LyricsLoaded: 'LyricsLoaded',
  SearchCompleted: 'SearchCompleted',
  RoomClosed: 'RoomClosed',
  HubError: 'HubError',
} as const;

export type ClientMethod = (typeof CLIENT_METHODS)[keyof typeof CLIENT_METHODS];

/** Ordered list for contract tests and diagnostics. */
export const CLIENT_METHOD_NAMES: readonly ClientMethod[] = Object.values(CLIENT_METHODS);

// -------------------------------------------------------------------------------------------------
// Wire enums (numeric, identical to Karaokay.Contracts.Hubs.WireEnums).
// -------------------------------------------------------------------------------------------------

export const MediaSource = {
  Multitrack: 0,
  YouTube: 1,
} as const;

export type MediaSource = (typeof MediaSource)[keyof typeof MediaSource];

export const QueueItemState = {
  Queued: 0,
  Downloading: 1,
  Ready: 2,
  Failed: 3,
  Playing: 4,
  Ended: 5,
} as const;

export type QueueItemState = (typeof QueueItemState)[keyof typeof QueueItemState];

export const PlaybackStatus = {
  Idle: 0,
  AwaitingDownload: 1,
  Countdown: 2,
  Playing: 3,
  Paused: 4,
  SongEnded: 5,
} as const;

export type PlaybackStatus = (typeof PlaybackStatus)[keyof typeof PlaybackStatus];

export const DownloadStage = {
  Queued: 0,
  Resolving: 1,
  Downloading: 2,
  Transcoding: 3,
  Completed: 4,
  Failed: 5,
} as const;

export type DownloadStage = (typeof DownloadStage)[keyof typeof DownloadStage];

/** Well-known `HubErrorDto.code` values (mirrors `Karaokay.Contracts.Hubs.HubErrorCodes`). */
export const HUB_ERROR_CODES = {
  ForbiddenRole: 'ForbiddenRole',
  RoomNotFound: 'RoomNotFound',
  NotJoined: 'NotJoined',
  InvalidRequest: 'InvalidRequest',
  YouTubeDisabled: 'YouTubeDisabled',
  NotFound: 'NotFound',
  DownloadUnavailable: 'DownloadUnavailable',
  QueueFull: 'QueueFull',
  NotImplemented: 'NotImplemented',
} as const;

export type HubErrorCode = (typeof HUB_ERROR_CODES)[keyof typeof HUB_ERROR_CODES];

// -------------------------------------------------------------------------------------------------
// REST types come from the generated OpenAPI schema (no hand duplication).
// -------------------------------------------------------------------------------------------------

export type SongSummaryDto = components['schemas']['SongSummaryDto'];
export type CreateRoomResponse = components['schemas']['CreateRoomResponse'];

// -------------------------------------------------------------------------------------------------
// SignalR DTOs. Hub enums are strict numeric unions and hub integers are real JSON numbers, while
// the OpenAPI generator widens those to `number`/`number | string` for HTTP query-string safety.
// The hub-specific projections therefore mirror the C# records explicitly.
// -------------------------------------------------------------------------------------------------

export type HubRole = 'stage' | 'guest';

export interface SongDto {
  id: string;
  title: string;
  artist: string | null;
  durationMs: number;
  source: MediaSource;
  thumbnailUrl: string | null;
}

export interface JoinRequest {
  roomCode: string;
  displayName: string;
  role: HubRole;
}

export interface JoinAck {
  success: boolean;
  sessionToken: string | null;
  reason: string | null;
}

export interface QueueItemDto {
  id: string;
  songId: string;
  position: number;
  requestedByName: string;
  state: QueueItemState;
}

export interface PlaybackStateDto {
  status: PlaybackStatus;
  currentQueueItemId: string | null;
  currentSong: SongDto | null;
  positionMs: number;
  timeAnchorUtc: string;
  guideVocalVolume: number;
  countdownRemainingBeats: number | null;
}

export interface RoomSnapshot {
  roomCode: string;
  status: PlaybackStatus;
  playback: PlaybackStateDto;
  queue: QueueItemDto[];
  guestCount: number;
  queueSequence: number;
}

export interface QueueDelta {
  added: QueueItemDto[];
  removed: string[];
  updated: QueueItemDto[];
  sequence: number;
}

export interface HubErrorDto {
  code: string;
  message: string;
  details?: string | null;
}

export interface PingPayload {
  t1: number;
}

export interface Pong {
  t1: number;
  t2: number;
}

export interface QueueAck {
  success: boolean;
  queueItemId: string;
  reason: string | null;
}

export interface YouTubeQueueRequest {
  videoId: string;
  title: string;
  channel: string;
  durationSeconds: number;
  thumbnailUrl: string | null;
}

export interface YouTubeSearchResultDto {
  videoId: string;
  title: string;
  channel: string;
  durationSeconds: number;
  thumbnailUrl: string | null;
}

export interface StageCapabilities {
  audioLatencyEstimateMs: number;
}

export interface DownloadProgressDto {
  queueItemId: string;
  songId: string;
  stage: DownloadStage;
  percent: number;
  bytesPerSec: number;
  etaSec: number;
}

export interface SongReadyDto {
  queueItemId: string;
  songId: string;
}

export interface DownloadFailedDto {
  queueItemId: string;
  songId: string;
  reason: string;
}

export interface CountdownDto {
  queueItemId: string;
  remainingBeats: number;
  beatMs: number;
}

export interface LyricsDto {
  url: string;
  sha256: string;
}

/** Stable command names for `RoomCommand.type` (mirrors `RoomCommandTypes`). */
export const RoomCommandTypes = {
  EnqueueLibrarySong: 'EnqueueLibrarySong',
  EnqueueYouTubeSong: 'EnqueueYouTubeSong',
  RemoveQueueItem: 'RemoveQueueItem',
  MoveQueueItem: 'MoveQueueItem',
  StartPlayback: 'StartPlayback',
  PausePlayback: 'PausePlayback',
  ResumePlayback: 'ResumePlayback',
  SkipSong: 'SkipSong',
  RestartSong: 'RestartSong',
  ReportSongEnded: 'ReportSongEnded',
  SetGuideVocalVolume: 'SetGuideVocalVolume',
  UpdateStageCapabilities: 'UpdateStageCapabilities',
} as const;

export type RoomCommandType = (typeof RoomCommandTypes)[keyof typeof RoomCommandTypes];

/**
 * Mirror of the C# `RoomCommand` record (docs/ARCHITECTURE.md §5.1 rule 6). Every nullable C#
 * property stays a required-but-nullable field so a serialized default instance has the same
 * property set on both sides.
 */
export interface RoomCommand {
  type: string;
  roomCode: string;
  connectionId: string | null;
  queueItemId: string | null;
  songId: string | null;
  newPosition: number | null;
  volume: number | null;
  audioLatencyEstimateMs: number | null;
}

/** Connection lifecycle exposed by `StageClient`. */
export type StageConnectionStatus =
  | 'idle'
  | 'connecting'
  | 'connected'
  | 'reconnecting'
  | 'disconnected';
