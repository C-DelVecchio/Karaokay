# Phone remote (Slice 3)

Guest-facing phone UI served at `/remote` (and `/remote?code=XXXX` from the stage QR code).

- `JoinScreen` — four-letter code + display name, prefilled from the QR link.
- `LobbyView` — room code, QR, guest count, playback state, Library/Queue tabs.
- `LibraryTab` — `GET /api/songs` → `EnqueueLibrarySong`.
- `QueueList` — live queue with `MoveQueueItem` (up/down) and `RemoveQueueItem`.
- `PlaybackControls` — Start/Pause/Resume/Skip.
- `useStageClient` — React binding over `lib/signalr/stageClient`.

The full `RoomSnapshot` from `JoinedRoom` is the single source of truth; queue deltas are
sequence-guarded in `lib/signalr/sync.ts` and a gap triggers an idempotent re-join.
