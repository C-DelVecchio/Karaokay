# ADR-007: Slice 4 and Slice 5 landed out of order — YouTube ingestion accepted early, playback authority still owed

- **Status:** Accepted
- **Date:** 2026-11-25
- **Deciders:** Project owner, Architect
- **Context level:** Delivery ordering / Slice 4–5 scope
- **Related:** [ARCHITECTURE.md §5.1](../ARCHITECTURE.md#51-unified-state-machine-per-queue-item--room-playback) (single-writer playback and idempotent end reports), [ARCHITECTURE.md §5.3](../ARCHITECTURE.md#53-clock-sync-frame-accurate-lyricsvideo-without-webrtc) (Ping/Pong clock sync), [ARCHITECTURE.md §7](../ARCHITECTURE.md#7-phased-vertical-slice-implementation-roadmap) (Slice 4 and Slice 5 definitions of done), [slice-3 review](../review/slice-3-review.md) (M4/M5), [slice-4 review](../review/slice-4-review.md) (implementation verdict and open backlog), [ADR-001](001-single-replica-signalr-azure.md) (replica and cold-restart assumptions)

## Context

Commit `324da8a` is titled `feat(slice-4): YouTube search, yt-dlp download worker, stage video mode & guest YouTube UI`. Its contents implement the roadmap's **Slice 5** scope:

- `yt-dlp`-backed YouTube search with no API key;
- a download worker with a bounded job channel, retries, cache-hit short-circuiting, and progress events;
- `Song`/`DownloadJob` persistence and a migration;
- `GET/POST /api/youtube` plus a range-enabled `/api/songs/{id}/video`;
- hub `SearchYouTube`/`EnqueueYouTubeSong` and the `SongReady`/`DownloadFailed`/`DownloadProgressChanged` client events;
- the guest `YouTubeTab`, queue progress badges, and a stage `<video>` surface routed through the existing Web Audio graph.

The roadmap's documented **Slice 4** deliverables were not implemented by that commit:

1. a single-writer `PlaybackAuthority` hosted service with the duration watchdog;
2. `Ping`/`Pong` clock sync with the 8-sample min-RTT best-of-window offset filter (250 ms RTT rejection, ≥2-sample confidence gate, receipt-anchor fallback);
3. authoritative stage playback driven by the host — Slice 3 review **M5**;
4. cold-start rehydration of playing rooms from Postgres — Slice 3 review **M4**;
5. duplicate `ReportSongEnded` idempotency against the watchdog race. The current hub already ignores a report whose queue-item id is not current, but that check exists without the authority/watchdog pair the documented rule is written against.

The slice number in the commit title therefore describes the wrong deliverable. The documentation also still said YouTube ingestion was unbuilt, Slice 4 was next, and `DownloadJob` was planned. That mismatch is exactly the kind of blueprint drift ADR-005 exists to prevent: the repository had two different descriptions of what had shipped.

The project owner's decision is to **keep the original roadmap and definitions of done unchanged**. Renumbering the slices would hide the missed deliverables behind a title change and would invalidate references throughout the blueprint, reviews, and learning guides.

## Decision

1. **The original spec stands.** `docs/ARCHITECTURE.md` v1.2 keeps Slice 4 as playback authority, clock sync, and browser video mode, and Slice 5 as zero-key YouTube ingestion. No definition of done is rewritten, relaxed, or renumbered.
2. **The YouTube work is accepted as the documented Slice 5 deliverable, completed early.** It shipped in commit `324da8a`, ahead of Slice 4, and is recorded as complete in `docs/PROJECT_STATE.md`. At the reviewed commit `324da8a`, the review found majors still being fixed (video sync against unsynchronized wall clocks, request-path backpressure, unauthenticated/unthrottled `yt-dlp` spawning, and strict H.264/AAC enforcement); the follow-up change has landed them, and they are tracked in the Slice 4 review. They do not retroactively make the Slice 5 scope unbuilt.
3. **Documented Slice 4 is still owed and remains open.** The missing deliverables are marked **PARTIALLY COMPLETE / OPEN** and are planned next: `PlaybackAuthority` + watchdog, `Ping`/`Pong` + offset filter, authoritative stage playback (M5), cold-start rehydration (M4), and duplicate-end idempotency. The stage video surface that does exist consumes `PlaybackStateDto`; at `324da8a` it computed its expected position from the local wall clock, and the follow-up has since landed the §5.3 `Ping`/`Pong` offset filter for that stage video path. It remains a partial surface rather than the full synchronized playback path Slice 4 specifies: authoritative audio calibration and phone countdown rings remain on the state checklist.
4. **Slice 3 review items M4 and M5 remain open.** They were not closed by the mislabeled commit and must not be marked resolved merely because video playback now reads room state.
5. **Record the ordering mistake in the review trail.** [`docs/review/slice-4-review.md`](../review/slice-4-review.md) records the verdict, in-flight fixes, and the backlog that is not being fixed in the documentation follow-up.

This ADR is a **delivery-order correction, not new architecture**. It changes no contract, no wire shape, no roadmap text, and no code.

## Consequences

**Positive**

- The state page, the review record, and the roadmap now agree on what shipped and what did not.
- The YouTube work is not thrown away or mislabeled as Slice 4; it is credited as early Slice 5 delivery.
- The Slice 4 definition of done keeps its full strength. The project cannot claim authoritative playback or clock sync until the authority, watchdog, calibration, rehydration, and idempotency tests actually exist.
- Future reviewers can see the mistake and its resolution in one place instead of inferring it from a commit title.

**Negative / Accepted risks**

- The git history still contains a commit titled `feat(slice-4)` that implements Slice 5. Accepted: rewriting published history is worse than an ADR and a corrected state document, and the commit message itself describes the YouTube packages accurately.
- The project now has completed Slice 5 work ahead of partial Slice 4 work. Accepted: vertical slices are delivery units, not a promise that every earlier slice is finished; the state page makes the out-of-order status explicit.
- The stage can currently show a YouTube video while the authoritative playback layer is still missing. Accepted for now: the video surface is useful review evidence and the synchronization gap is recorded as a major, not presented as complete.

**Governance**

- `docs/ARCHITECTURE.md` remains v1.2 with its original Slice 4/5 text. If a future change alters a contract, a count, or an enforcement mechanism, ADR-005 still requires a new ADR or a blueprint version bump with a version-history entry.
- Status claims belong in `docs/PROJECT_STATE.md` and the review records, not in silent edits to the roadmap.
- Do not mark Slice 4 complete, and do not renumber YouTube as Slice 4, without a superseding ADR that explicitly retires the documented definition of done.

## Alternatives considered

| Alternative | Why rejected |
|---|---|
| Renumber the delivered YouTube work as Slice 4 and move playback authority to Slice 5 | Rewrites the roadmap, invalidates every Slice 4/5 reference in the blueprint and reviews, and hides the missed definition of done behind a label change. |
| Leave the commit title and the stale docs as they were | Reintroduces two descriptions of the shipped state; the next contributor would build against a false roadmap. |
| Revert `324da8a` and re-land it later as Slice 5 | Discards sound, tested work solely to satisfy commit ordering; the delivery-order mistake is better recorded than rewritten. |
| Declare Slice 4 complete because a video surface and a `PlaybackChanged` consumer exist | The documented Slice 4 requires a single writer, watchdog, clock calibration, rehydration, and duplicate-end protection. None of those exist, so the claim would be false. |
| Weaken the Slice 4 definition of done to match what shipped | Turns a missed deliverable into a permanent quality regression and makes the remaining synchronization work invisible. |

## Validation plan

- `docs/PROJECT_STATE.md` marks Slice 5 complete-early, Slice 4 partial/open with an explicit exists-versus-missing checklist, and keeps Slice 3 M4/M5 open.
- `docs/review/slice-4-review.md` records the SHIP-WITH-MAJORS verdict, the in-flight fixes, the open minor/nit backlog, and the verification blind spots.
- The ADR text itself changes no code, configuration, CI, or generated artifact; the accompanying code follow-up is recorded in [`docs/review/slice-4-review.md`](../review/slice-4-review.md).
- The next Slice 4 implementation must satisfy the unchanged tests in `docs/ARCHITECTURE.md` §7: authoritative start/pause/resume/skip, watchdog firing, clock offset reporting, ±300 ms video self-correction, and duplicate `ReportSongEnded` protection.
