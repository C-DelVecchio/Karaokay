import { describe, expect, it } from 'vitest';
import {
  CLIENT_METHOD_NAMES,
  CLIENT_METHODS,
  HUB_METHOD_NAMES,
  HUB_METHODS,
  RoomCommandTypes,
  STAGE_HUB_URL,
  isValidRoomCode,
} from './protocol';

const EXPECTED_HUB_METHODS = [
  'JoinRoom',
  'Ping',
  'SearchYouTube',
  'EnqueueLibrarySong',
  'EnqueueYouTubeSong',
  'RemoveQueueItem',
  'MoveQueueItem',
  'StartPlayback',
  'PausePlayback',
  'ResumePlayback',
  'SkipSong',
  'RestartSong',
  'ReportSongEnded',
  'SetGuideVocalVolume',
  'UpdateStageCapabilities',
] as const;

const EXPECTED_CLIENT_METHODS = [
  'JoinedRoom',
  'ParticipantJoined',
  'ParticipantLeft',
  'QueueChanged',
  'QueueItemStateChanged',
  'DownloadProgressChanged',
  'PlaybackChanged',
  'CountdownTicked',
  'LyricsLoaded',
  'SearchCompleted',
  'RoomClosed',
  'HubError',
] as const;

describe('SignalR protocol mirror', () => {
  it('uses the same-origin hub endpoint', () => {
    expect(STAGE_HUB_URL).toBe('/hubs/stage');
  });

  it('lists every client-to-server method exactly once', () => {
    expect(HUB_METHOD_NAMES).toEqual([...EXPECTED_HUB_METHODS]);
    expect(new Set(HUB_METHOD_NAMES).size).toBe(HUB_METHOD_NAMES.length);
    for (const name of EXPECTED_HUB_METHODS) {
      expect(HUB_METHODS[name]).toBe(name);
    }
  });

  it('lists every server-to-client method exactly once', () => {
    expect(CLIENT_METHOD_NAMES).toEqual([...EXPECTED_CLIENT_METHODS]);
    expect(new Set(CLIENT_METHOD_NAMES).size).toBe(CLIENT_METHOD_NAMES.length);
    for (const name of EXPECTED_CLIENT_METHODS) {
      expect(CLIENT_METHODS[name]).toBe(name);
    }
  });

  it('keeps the room command names stable', () => {
    expect(Object.values(RoomCommandTypes)).toEqual([
      'EnqueueLibrarySong',
      'EnqueueYouTubeSong',
      'RemoveQueueItem',
      'MoveQueueItem',
      'StartPlayback',
      'PausePlayback',
      'ResumePlayback',
      'SkipSong',
      'RestartSong',
      'ReportSongEnded',
      'SetGuideVocalVolume',
      'UpdateStageCapabilities',
    ]);
  });

  it('validates four-letter codes against the 23-letter alphabet', () => {
    expect(isValidRoomCode('ABCD')).toBe(true);
    expect(isValidRoomCode('HJKL')).toBe(true);
    expect(isValidRoomCode('ABC')).toBe(false);
    expect(isValidRoomCode('ABCDE')).toBe(false);
    expect(isValidRoomCode('ABCI')).toBe(false);
    expect(isValidRoomCode('ABCO')).toBe(false);
    expect(isValidRoomCode('ABCQ')).toBe(false);
    expect(isValidRoomCode('ABCD1')).toBe(false);
    expect(isValidRoomCode('abcd')).toBe(false);
  });
});
