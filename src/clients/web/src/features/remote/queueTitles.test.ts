import { describe, expect, it } from 'vitest';
import { queueTitlesFromSongs, resolveQueueItemTitle } from './queueTitles';

const songId = '22222222-2222-4222-8222-222222222222';

function titles(...entries: Array<[string, string]>): Map<string, string> {
  return new Map(entries);
}

describe('resolveQueueItemTitle', () => {
  it('prefers the queue DTO title over the songs snapshot', () => {
    // The regression: a newly enqueued YouTube song is not in `/api/songs` yet.
    expect(resolveQueueItemTitle(
      { songId, title: 'Video Song (Karaoke)' },
      titles([songId, 'Stale Snapshot Title']),
    )).toBe('Video Song (Karaoke)');

    expect(resolveQueueItemTitle({ songId, title: 'Video Song (Karaoke)' }, titles()))
      .toBe('Video Song (Karaoke)');
  });

  it('falls back to the songs snapshot when the DTO title is absent or blank', () => {
    const snapshot = titles([songId, 'Snapshot Title']);

    expect(resolveQueueItemTitle({ songId }, snapshot)).toBe('Snapshot Title');
    expect(resolveQueueItemTitle({ songId, title: '' }, snapshot)).toBe('Snapshot Title');
    expect(resolveQueueItemTitle({ songId, title: '   ' }, snapshot)).toBe('Snapshot Title');
  });

  it('treats the legacy Song placeholder as missing', () => {
    expect(resolveQueueItemTitle(
      { songId, title: 'Song 22222222' },
      titles([songId, 'Snapshot Title']),
    )).toBe('Snapshot Title');

    // Without a snapshot the generated placeholder for the actual song id wins, never the
    // placeholder string the DTO happened to carry (stage "Up next" uses the same resolver).
    expect(resolveQueueItemTitle({ songId, title: 'Song deadbeef' }, titles()))
      .toBe('Song 22222222');
  });

  it('uses the Song id placeholder only as the last resort', () => {
    expect(resolveQueueItemTitle({ songId }, titles())).toBe('Song 22222222');
    expect(resolveQueueItemTitle({ songId, title: '' }, titles())).toBe('Song 22222222');
    expect(resolveQueueItemTitle({ songId, title: 'Song 22222222' }, titles()))
      .toBe('Song 22222222');
  });
});

describe('queueTitlesFromSongs', () => {
  it('joins by song id and enqueue id, defaulting a missing title', () => {
    const map = queueTitlesFromSongs([
      { id: songId, title: 'Snapshot Title' },
      { id: '33333333-3333-4333-8333-333333333333', enqueueId: 'enqueue-1', title: null },
    ]);

    expect(map.get(songId)).toBe('Snapshot Title');
    expect(map.get('enqueue-1')).toBe('Untitled');
  });
});
