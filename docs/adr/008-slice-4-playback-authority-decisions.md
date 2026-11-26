# ADR-008: Slice 4 playback authority — bounded command channel, fixed count-in beat, persisted queue sequence, and blueprint filename reconciliation

- **Status:** Accepted
- **Date:** 2026-11-25
- **Deciders:** Project owner, Architect
- **Context level:** Slice 4 implementation / playback wire contract
- **Related:** [ARCHITECTURE.md §5.1](../ARCHITECTURE.md#51-unified-state-machine-per-queue-item--room-playback) (single-writer playback, count-in rules, idempotent end reports), [ARCHITECTURE.md §5.3](../ARCHITECTURE.md#53-clock-sync-frame-accurate-lyricsvideo-without-webrtc) (audio-as-ground-truth alignment, nudge thresholds), [ARCHITECTURE.md §7](../ARCHITECTURE.md#7-phased-vertical-slice-implementation-roadmap) (Slice 4 definition of done), [ADR-007](007-slice-4-5-deliverable-ordering.md) (the early-Slice-5 ordering correction this slice completes), [slice-3 review](../review/slice-3-review.md) (M4 rehydration, M5 stage wiring), [slice-4 review](../review/slice-4-review.md) (completion follow-up)

## Context

[ADR-007](007-slice-4-5-deliverable-ordering.md) recorded that commit `324da8a` implemented the documented Slice 5 (YouTube ingestion) under a `slice-4` title and that the documented Slice 4 — playback authority, clock sync, authoritative stage playback, cold-start rehydration, and duplicate-end idempotency — was still owed. The Slice 4 implementation session has now landed that work, and the repository needed this record so the written state matches the code.

The blueprint specifies the *behaviour* of Slice 4 in §5.1 and §5.3 but leaves several implementation choices open, and two of its planned web filenames turned out not to match what was built:

1. **Count-in beat source.** §5.1 requires an authority-owned four-beat count-in before a playing room, and §5.1 rule 5 says the stage renders the precise dots while phones show a compact ring. The blueprint does not say whether the room's beat duration is derived from `lyrics.json`'s tempo map, from the song gap metadata, or fixed. `lyrics.json` gaps already carry `countdownBeats`/`beatMs` per song, and the authoritative per-song tempo lives there.
2. **Command acknowledgement.** §5.1 requires hub callers to observe persisted-then-broadcast state, but does not define whether a hub method returns when the command is queued or when the authority has applied it. A queued-only return would let a phone Start and immediately read a stale snapshot.
3. **Cold-start sequence source.** Rehydration (Slice 3 review M4) must restore the monotonic queue sequence so reconnecting clients do not see deltas rewind. The sequence existed only in memory (`RoomState.NextQueueSequence`); nothing persisted it.
4. **Testable time.** The watchdog and count-in are wall-clock behaviours. Reading `DateTime.UtcNow` directly inside the service would force sleep-based tests.
5. **File naming.** The blueprint's Slice 4 file list names `useServerClock.ts` and `YouTubeVideoSurface.tsx`. Neither file was ever created.

## Decision

1. **The authority owns the two-phase start with a fixed 500 ms count-in beat.** `PlaybackAuthority` moves a started room to `Countdown` anchored at "now" with four remaining beats and broadcasts `CountdownTicked(queueItemId, remainingBeats, beatMs: 500)` per beat (4 → 1), then auto-transitions to `Playing` with a **fresh** `PlaybackStartedAtUtc` anchor. The beat duration is deliberately fixed at **500 ms** in `Features/PlaybackTiming.cs` (`CountdownBeats = 4`, `CountdownBeatMs = 500`, a 2 s window): the authoritative per-song tempo lives in `lyrics.json`, and a per-tick media read would couple the authority to file I/O for no audible gain. The stage renders the precise dots from its own `lyrics.json` (§5.1 rules 3 and 5); phones render the mini countdown ring from `CountdownTicked`. Pause during the count-in parks the room at `Paused`/position 0; Resume starts a fresh anchor; Skip cancels the count-in and advances; Restart re-enters the count-in; a duplicate Start is ignored.
2. **Hub playback calls use a `RoomCommand` + `TaskCompletionSource` envelope.** The bounded `Channel` (capacity **256**, `BoundedChannelFullMode.Wait`, single reader) carries `PlaybackCommand(RoomCommand, TaskCompletionSource)`. `StageHub`'s `StartPlayback`, `PausePlayback`, `ResumePlayback`, `SkipSong`, `RestartSong`, `ReportSongEnded`, `SetGuideVocalVolume`, and `RemoveQueueItem` await the completion the consume loop signals after persisting and broadcasting, so a caller has **read-your-writes** semantics without ever mutating playback state itself. Every transition runs under the room `CommandLock` and persists before broadcast (§5.1 rule 6). Enqueue and move stay on their direct path; removal moved behind the channel because deleting the current item also resets playback to `Idle`, and the command result carries the not-found signal for the hub.
3. **Persist `PartyRoom.QueueSequence` with write-ahead sequencing and restore it on rehydration.** The sequence is minted **before** the queue/tracked state is persisted (`RoomState.NextQueueSequence` in the queue-service persistence path), so the database can never store a lower sequence than a client has already seen. Migration `20261125160000_AddRoomQueueSequence` adds `Rooms.QueueSequence bigint NOT NULL DEFAULT 0`. `RoomRegistry.RestoreQueueSequence` installs the stored value when `RoomRehydrator` rebuilds a room, so reconnect deltas continue above the last pre-restart sequence.
4. **Inject `TimeProvider` for anchor and watchdog determinism.** `PlaybackAuthority` takes an internal `TimeProvider` + tick-interval seam (default `TimeProvider.System`, 100 ms) and `QueueService` takes an injectable `TimeProvider` for position anchors. Unit tests drive anchors and fire the watchdog with a fake clock instead of sleeping. The watchdog ticks every 100 ms, considers only `Playing` rooms, and ends a room through the **same guarded end path** as a stage report once `PlaybackStartedAtUtc + Song.DurationMs + 5 s` passes. One shared guard — `queueItemId == CurrentQueueItemId` under `CommandLock` — makes a duplicate stage report and a watchdog firing after that report mutually exclusive, so the queue can never double-advance.
5. **Reconcile the blueprint filenames with what shipped.** The clock-sync implementation is `src/clients/web/src/lib/signalr/clockSync.ts` (Ping/Pong sampler, 8-sample median offset filter, server-now correction); `useServerClock.ts` never existed. The browser video surface is `useStageVideo.ts`, with `StageShell.tsx` hosting the actual `<video>` element routed through `AudioContext.createMediaElementSource`; `YouTubeVideoSurface.tsx` never existed. The planned `playbackController.ts` did land under that name, as `features/stage/playbackController.ts` (`StagePlaybackController` + `useStagePlayback`). §7 of the blueprint points here rather than rewriting the definition of done.

These are **implementation decisions inside the unchanged Slice 4 scope**, not a relaxation of it. The definition of done, the §5.1 state machine, the §5.3 thresholds, and the test matrix in `ARCHITECTURE.md` §7 keep their full strength.

## Consequences

**Positive**

- Hub callers cannot observe a partially applied playback command: the channel envelope turns "fire and hope" into a completion the caller awaits, which is what makes the phone remote's immediate post-command state honest.
- One writer serializes every playback transition, so persistence order and broadcast order are the same order, and the duplicate-end race has a single guard rather than two competing checks.
- A fixed count-in beat keeps the authority independent of media parsing, while the stage still renders song-accurate dots from `lyrics.json`; phones get a ring from the broadcast contract.
- Cold restarts keep the queue, playback anchor, guide-vocal level, and monotonic sequence because both the sequence and the playback fields are persisted. A dangling cursor resets to `Idle`, and stale duplicate `Playing` rows are demoted rather than restored as multiple playing songs.
- `TimeProvider` keeps the watchdog and count-in tests fast and deterministic; no test sleeps for five seconds.
- The blueprint no longer implies files that do not exist, so a reader looking for `useServerClock.ts` or `YouTubeVideoSurface.tsx` is redirected to the real modules.

**Negative / Accepted risks**

- Playback commands can back up to 256 entries if a client floods the hub; the channel then waits rather than dropping. Accepted: commands are tiny and per-room serialized, and the bound exists only to stop unbounded growth.
- The count-in beat is fixed at 500 ms even for songs whose `lyrics.json` tempo differs. Accepted: the room count-in is a party cue, not a musical subdivision; the stage draws the precise per-song dots, and a per-tick file read was judged unjustified.
- Rehydration adds a startup query over open rooms with their queues and songs. Accepted for the party-scale room count; it runs once, after migrations and demo seeding, and is a no-op without a connection string.
- The database now carries `QueueSequence` and the playback fields as authority-owned state, so a manual row edit can produce a sequence or anchor that the authority did not mint. Accepted: the database is not the live writer; `RestoreQueueSequence` is the only consumer.
- Web and Android stages still render from their own audio element as ground truth; only the web stage exists today, so the shared decision fixtures — not this ADR — remain the cross-client contract.

**Governance**

- `docs/ARCHITECTURE.md` keeps v1.2 and its Slice 4 specification and definition of done. This ADR records the implementation choices and the filename corrections; it does not retire any requirement.
- Slice 3 review **M2** (stage role is client-declared) and **M3** (rooms are never garbage-collected) remain open and must close before the Slice 6 public demo. Slice 4 did not address them and this ADR does not claim otherwise.
- The `clockSync.ts` no-reschedule nit recorded in the Slice 4 review remains open unless the review's follow-up says otherwise.

## Alternatives considered

| Alternative | Why rejected |
|---|---|
| Derive the room count-in beat from each song's `lyrics.json` tempo at Start | Couples the authority to media I/O on every tick or Start, for a cue whose precision is rendered by the stage anyway. The blueprint puts per-song tempo in `lyrics.json`; the room beat is a fixed operational cue. |
| Let hub playback methods mutate room state directly, as the Slice 3 subset did | Reintroduces multiple writers and the interleaving the single-writer rule exists to prevent; the duplicate-end race would need per-call-site guards. |
| Return from hub methods as soon as the command is queued | Breaks read-your-writes: a phone could Start and then read a snapshot that still says `Idle`, and tests could not distinguish queued from applied. |
| Persist the queue sequence after the mutation (read-back) | A crash between broadcast and persist could store a lower sequence than clients already saw, producing rewound deltas after rehydration. Write-ahead minting removes that window. |
| Keep `DateTime.UtcNow` and test with sleeps | Slow, flaky, and unable to exercise the five-second watchdog without real waiting. Injectable `TimeProvider` is the standard .NET seam. |
| Rewrite the blueprint's Slice 4 file list in place without an ADR | ADR-005 requires a recorded decision for blueprint corrections; a silent rename would look like a typo and lose the reason the planned files never existed. |
| Mark Slice 4 complete without rehydration or duplicate-end protection | Those are explicit Slice 4 requirements and Slice 3 review M4; claiming completion would make the checklist false. |

## Validation plan

- `PlaybackAuthorityTests` cover the two-phase Start broadcast and `CountdownTicked` contract, pause/resume position preservation, the watchdog, duplicate-end idempotency, and the count-in interactions (pause/resume/skip/restart/duplicate Start) against an injected clock. The ±30 ms pause/resume bound is asserted in the Docker-gated integration suite, not here.
- `RoomStateMapperTests` and `RoomRehydrationQueryTests` cover cold-start mapping, position re-densification, dangling-cursor reset to `Idle`, stale duplicate `Playing` demotion, and the single `AsNoTracking` query shape.
- The five new Slice 4 integration tests run in CI against Testcontainers PostgreSQL: Start → two-phase broadcast, pause/resume within ±30 ms, watchdog firing, duplicate-end non-advance, and cold-restart rehydration. They skip locally without Docker like the rest of the PostgreSQL suite.
- `e2e/playback-authority.spec.ts` drives Start/Pause/Resume/Skip from the phone and asserts convergence on the stage, including a mid-song stage join aligned under a +40 ms clock bias standing in for RTT/2. `e2e/youtube.spec.ts` asserts a +300 ms injected video drift is hard-nudged back within the §5.3 threshold.
- `docs/PROJECT_STATE.md` §2.5/§7 mark Slice 4 complete and keep M2/M3 open; `docs/review/slice-4-review.md` records this completion follow-up.
