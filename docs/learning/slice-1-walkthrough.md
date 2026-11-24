# Slice 1 Code Walkthrough — a guided tour

This is a guided read of everything commit `3f998e1` ("feat(slice-1): scaffold solution, synthetic
demo generator, MIDI parser, and importer CLI") added to Karaokay. You know C# and ASP.NET; you may
not know MIDI, audio DSP, or why this repo is wired the way it is. The goal is to explain **why each
piece exists**, so you could rebuild or safely change it.

Read §1–§2 for the shape, §3–§6 for the core transform (MIDI → contract), §7–§9 for the two front
ends and room codes, §10–§12 for the tests, determinism checklist, and experiments, §13 for what
follows. Paths are relative to the repo root; line references are from that commit; commands assume
the repo root. Two tests shell out to `ffmpeg`. On this commit `dotnet test` reports **44 passed**
(28 unit, 5 integration, 11 architecture).

---

## 1. What Slice 1 is

Karaoke needs three things before it can draw a pixel: a **timeline** (when each syllable starts and
ends, which syllables form a word, which are spoken, where lines end, where countdown gaps are), an
**instrumental**, and a **guide vocal**. Copyright forbids committing real recordings or charts, and
the YouTube path arrives in Slice 5. So Slice 1 builds two independent pipelines that converge on one
contract:

* **SyntheticGen** manufactures public-domain demo songs from a seed: a MIDI chart plus synthesized
  PCM encoded to Opus, giving the repo deterministic, legally distributable content and golden
  fixtures *today*.
* **Importer** ingests an extracted Rock Band folder (`song.ini`, `notes.mid`, `*.ogg`) and produces
  the same files, so real content can be added later without touching the renderer.

```
  EXTRACTED RB FOLDER                     SEED (default 42)
  song.ini  notes.mid  *.ogg                 │
      │         │        │         SyntheticSongs.Build(seed)
      │         │        │                   │
      │         │        │        ┌──────────┴─────────┐
      │         │        │   MidiScoreComposer   AudioStemBaker
      │         │        │        │                   │
      │         │        │     song.mid      PCM → ffmpeg/libopus
      ▼         ▼        ▼        ▼                   ▼
 SongIniReader  MidiLyricsParser ◀─ read back ─┐  instrumental.opus
      │             │      ▲                   │  vocals.opus
      ▼             ▼      └───────────────────┘
  delay (ms)    lyrics.json ◀─ LyricsJson.Serialize (one serializer)
      │             ▲
      └─ StemMixer ─┘  (importer only; ffprobe fixes durationMs)
```

Both paths into `lyrics.json` use **the same parser and serializer**. That is the central decision:
one implementation of "MIDI ticks → contract milliseconds", exercised by the generator, which writes
its MIDI to disk and parses the bytes back (§7.6).

| File | Role | Produced by | Consumed by (target) |
|---|---|---|---|
| `instrumental.opus` | bed track, loudness-normalized | SyntheticGen / StemMixer | web/Android stage |
| `vocals.opus` | guide vocal | SyntheticGen / StemMixer (or 1 s silence) | stage audio, crossfade |
| `lyrics.json` | the timeline contract | parser + `LyricsJson` | renderer, tests, `verify` |

Synthetic songs also keep `song.mid`: not part of playback, but the provenance of `lyrics.json` and
the input the golden test re-parses. Without it, a golden test could only compare JSON to JSON.
`RoomCodeGenerator` (§9) is the first piece of the party-room feature; `Api`, `AppHost`, and
`ServiceDefaults` are shells that make the dependency graph visible and enforceable.

## 2. The solution skeleton: how the build graph teaches architecture

### 2.1 One props file, twelve projects

`Directory.Build.props` is auto-imported by every project beneath it (no nested overrides):

```xml
<Project>

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>

</Project>
```

| Property | What it does | Why here |
|---|---|---|
| `TargetFramework=net10.0` | one TFM for apps, tools, tests | tests reference `Exe` tools; no `#if`; code needs .NET 9+ JSON knobs (`IndentCharacter`, `NewLine`, `IndentSize`) and C# 14 |
| `LangVersion=latest` | newest compiler features | collection expressions, `record struct`, pattern combinators |
| `Nullable=enable` | annotations everywhere | contracts distinguish absent from empty (`SongIni.Charter`, `LyricsDocument?`) |
| `ImplicitUsings=enable` | SDK default usings | `TestPaths.cs` and `SyntheticSongs.cs` have no `using` lines |
| `AnalysisLevel=latest-recommended` | recommended CAxxxx set for the TFM | culture, dead-code, and API-misuse analyzers actually run |
| `TreatWarningsAsErrors=true` | warnings are errors | CI cannot accumulate warning debt; green means zero warnings |
| `InvariantGlobalization=true` | no ICU/NLS culture data | determinism and smaller containers |

**What `InvariantGlobalization` really does.** It writes `System.Globalization.Invariant=true` into
each app's `runtimeconfig.json`. The runtime then does not load ICU (Linux) or NLS (Windows);
`CurrentCulture` behaves as invariant, and casing, collation, number parsing, and date formatting use
invariant rules. Output no longer depends on the machine's `LANG`, locale, or ICU version — essential
when fixtures are compared **byte for byte** in CI — and containers need no `libicu`.

It is a seatbelt, not the driving. The code still passes `CultureInfo.InvariantCulture` explicitly
for every format/parse (`string.Create(CultureInfo.InvariantCulture, …)`,
`double.TryParse(…, CultureInfo.InvariantCulture, …)`), uses `StringComparer.Ordinal` for
identifiers, and calls `ToLowerInvariant()`. It is also a one-way door: a future locale-aware UI
would need an `InvariantGlobalization=false` override on `Api`. For a party game whose visible
strings are titles and lyrics, that is a good trade.

Note: `.editorconfig` style rules are IDE analyzers. `EnforceCodeStyleInBuild` is not set, so they do
not fail `dotnet build`; the compiler/analyzer settings above are what CI enforces.

### 2.2 Central Package Management

`Directory.Packages.props` turns on CPM and pins every version once:

```xml
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>

  <ItemGroup>
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="4.0.1" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.12.0" />
    <PackageVersion Include="NetArchTest.Rules" Version="1.3.2" />
    <PackageVersion Include="Melanchall.DryWetMidi" Version="8.0.3" />
    <PackageVersion Include="CliWrap" Version="3.10.5" />
  </ItemGroup>
```

Projects use versionless references (`<PackageReference Include="CliWrap" />`), so "which libraries
may this layer use" is a one-file question and versions cannot skew. Two honest observations:
`NetArchTest.Rules 1.3.2` is pinned but **no project references it** (a leftover; §10.7 explains),
and there is no lock file — reproducibility rests on exact pins.

### 2.3 The dependency graph is the architecture

```
Karaokay.Domain        (leaf, no references)
Karaokay.Contracts     (leaf, no references)  ← lyrics.json types + serializer
        ▲
Karaokay.Application ──▶ Domain
Karaokay.Infrastructure ──▶ Application, Contracts  + DryWetMidi, CliWrap
        ▲                        ▲
Karaokay.SyntheticGen      Karaokay.Importer ──▶ Contracts, Infrastructure
        ▲                        ▲
Karaokay.UnitTests         Karaokay.IntegrationTests
Karaokay.Api ──▶ Application, Contracts, Infrastructure, ServiceDefaults
Karaokay.AppHost ──▶ ServiceDefaults
Karaokay.ArchitectureTests ──▶ everything (it inspects assemblies)
```

Domain and Contracts are **leaves** with zero project and package references. Nothing can create a
cycle through them; `Contracts` can be consumed by tools, API, and tests without dragging DryWetMidi
or EF Core (it only needs `System.Text.Json` from the BCL); and the direction is executable —
`ArchitectureRules` fails if Contracts references DryWetMidi or Application references Infrastructure
(§10.7). `Application` deliberately does **not** reference `Contracts`, even though
`RoomCodeGenerator` lives there: contracts are wire shapes, Application is use cases.

### 2.4 `InternalsVisibleTo` as the test seam

`Karaokay.Infrastructure.csproj` ends with:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="Karaokay.UnitTests" />
    <InternalsVisibleTo Include="Karaokay.IntegrationTests" />
  </ItemGroup>
```

and the Importer exposes internals to `Karaokay.IntegrationTests` only. This is how tests reach
`TickToMilliseconds`, `ComputeGapCountdown`, `BuildGaps`, `GroupPhrasesByMarkers`,
`BuildMeasureArguments`, `ImportCommand.ExecuteAsync`, and `Slugify` without publicising them.
Making them `public` would lie about the supported surface; reflection is brittle; `#if DEBUG`
splits behaviour. No strong-name key is involved, so the simple assembly name suffices.

### 2.5 CI

```yaml
      - name: Install ffmpeg
        run: sudo apt-get update && sudo apt-get install -y ffmpeg

      - name: Restore
        run: dotnet restore

      - name: Build
        run: dotnet build --configuration Release --no-restore

      - name: Test
        run: dotnet test --configuration Release --no-build
```

ffmpeg is installed because the determinism test and the importer end-to-end test need it. Release
plus `--no-restore`/`--no-build` means restore and build happen once and tests cannot silently
rebuild. One job, no matrix, no cache: Slice 1 is small. The determinism guarantee is scoped to a
toolchain, and `ubuntu-latest` can change under you (§7.5).

## 3. A five-minute MIDI primer

A Standard MIDI File is a container, not audio: a header (`MThd`) with format, track count, and time
division, then track chunks (`MTrk`) full of events.

* **Format 0** = one track; **format 1** = simultaneous tracks (conductor plus instruments), which
  SyntheticGen writes; **format 2** = independent sequences.
* An **event** is a delta-time (variable-length quantity), a status byte, and data. `NoteOn` is
  `0x9n note velocity`; `NoteOff` is `0x8n note velocity`, and running-status charts encode note-off
  as note-on with **velocity 0**. Note numbers 0–127 (60 = C4, 69 = A4 = 440 Hz), velocity 0–127.
* **Meta events** start `0xFF`: `0x51` SetTempo (3 bytes, microseconds per quarter note), `0x03`
  track name, `0x05` lyric, `0x01` text, `0x2F` end of track.
* **Time division** is ticks per quarter note (PPQ, here **480**) or SMPTE. The parser rejects SMPTE:
  the RB vocal contract is PPQ.
* **Tempo** is microseconds per quarter, not BPM: `bpm = 60_000_000 / µs` (500,000 = 120 BPM,
  600,000 = 100, 800,000 = 75).
* Only `SetTempo` changes the mapping. Between tempo events the tick→ms function is **linear**; at a
  tempo event the slope changes but time is continuous. That is "piecewise-constant tempo", and it
  is why conversion needs the whole map, not just the current BPM. 480 PPQ divides evenly by 2, 3, 4,
  5, 6, 8, 16, 32…, so triplets and 32nds land on integer ticks — Rock Band used 480 and we match it.

For one segment: `ms(t) = ms(segmentStart) + (t - tick(segmentStart)) × beatMs / 480`, where
`beatMs = 60000 / bpm`. Worked example from
`MidiLyricsParserUnitTests.TickToMillisecondsFollowsThePiecewiseTempoMap` — 120 BPM at tick 0,
100 BPM at tick 960, 75 BPM at tick 2400:

| Tick | Segment | Arithmetic | ms |
|---:|---|---|---:|
| 0 | — | — | 0 |
| 480 | 120 | 1 beat × 500 | 500 |
| 960 | 120 | 2 beats × 500 | 1000 |
| 1440 | 100 | 1000 + (480/480) × 600 | 1600 |
| 2400 | 100 | 1000 + (1440/480) × 600 | 2800 |
| 2880 | 75 | 2800 + (480/480) × 800 | 3600 |

The change at 960 adds no time; it changes the rate afterwards. Twinkle uses a different map —
changes at **beat 32** (tick 15360) and **beat 60** (tick 28800):

| Tick | Beat | Arithmetic | ms | BPM |
|---:|---:|---|---:|---:|
| 1920 | 4 | 4 × 500 | 2000 | 120 |
| 15360 | 32 | 32 × 500 | 16000 | 100 |
| 28320 | 59 | 16000 + 27 × 600 | 32200 | 100 |
| 28800 | 60 | 16000 + 28 × 600 | 32800 | 75 |
| 32160 | 67 | 32800 + 7 × 800 | 38400 | 75 |

Beats are exactly 500/600/800 ms and every syllable boundary is a whole beat, so every emitted
millisecond is an integer **by construction** — the kind of thing that keeps a golden file stable.

## 4. The lyric encoding: how karaoke text rides on notes

MIDI notes carry no text. Rock Band vocal charts attach a **lyric meta event immediately before each
note-on, at the same tick**. Everything else is convention:

| Concept | Encoding | Parser result |
|---|---|---|
| Syllable | `Lyric` + `NoteOn` at the same tick, note 48–84 | `Syllable.Text` |
| Spoken word | lyric starts `#` (or `^` on `HARM*` tracks) | strip prefix, `talky: true` |
| Word continues | lyric ends `-` or `+` | strip suffix, `connectsToNext: true` |
| Line break | `Text` meta event `[line_end]` | previous syllable gets `lineBreakAfter` |
| Phrase bounds | note-on 105 / note-on 106 | phrase window (preferred over gaps) |

The generator writes the raw lyric once:

```csharp
    private static string BuildRawLyric(SyllableSpec syllable)
    {
        var connector = syllable.Connector;
        if (connector is not ("" or "-" or "+"))
        {
            throw new InvalidOperationException($"Unsupported connector '{connector}'.");
        }

        return (syllable.Talky ? "#" : string.Empty) + syllable.Text + connector;
    }
```

The parser strips the talky prefix on all parts (`^` only on harmony, where RB uses it), then the
connector suffix:

```csharp
        if (text.StartsWith('#'))
        {
            talky = true;
            text = text[1..];
        }
        else if (isHarmony && text.StartsWith('^'))
        {
            talky = true;
            text = text[1..];
        }
```

```csharp
        var connectsToNext = text.EndsWith('-') || text.EndsWith('+');
        if (connectsToNext)
        {
            text = text[..^1];
        }
```

So `#ten-` becomes `"ten"`, talky, connecting. `-` and `+` are identical to the parser; the
distinction is charting convention.

Adjacency is enforced, not assumed: `previousEvent` is the literally preceding event, and a note-on
without a lyric at the same tick is **dropped silently**. In a `PART VOCALS` track that is the right
tolerance — a messy chart should not abort an import. `[line_end]` attaches to the most recent
syllable, not to a tick, which is why the composer puts it at the syllable's **end** tick, after
note-offs but before the next lyric.

### Why explicit phrase markers beat the gap heuristic

RB charts carry phrase bounds as note-on 105 (start) and 106 (end). Without them the fallback is
"split when the next note starts more than 1500 ms after the previous note ends". That heuristic
encodes an assumption about singing — *phrases end when the singer pauses* — false for fast songs,
held notes, and rap.

The demo songs make the difference visible. **Twinkle** is authored as 8 marked phrases whose
inter-phrase gaps are 500 ms, 0 ms (phrases 1→2 touch), 500 ms, then six of 600 ms. The maximum is
**600 ms**, far below 1500. Remove the markers and the fallback cannot split anywhere: the song
collapses to **one** phrase with 8 lines — exactly what
`RemovingPhraseMarkersCollapsesTwinkleToFewerPhrasesViaGapFallback` asserts after
`RemoveTimedEvents` deletes every 105/106 event. **Telegram** has no markers and 2000 ms gaps: 6
phrases. **Canyon** has no markers, 500 ms gaps inside groups and 3000–4000 ms between them: 7
authored phrases become 3 rendered phrases with 3 lines each.

The precedence is one line in `Parse`: `spans.Count > 0 ? GroupPhrasesByMarkers(...) :
GroupPhrasesByGaps(...)`. Markers are not a hint; they are authoritative when present.

### A source detail worth knowing

`MidiScoreComposer` emits the 106 note-on at the phrase **start** tick, not the end:

```csharp
        foreach (var marker in score.Markers)
        {
            Add(marker.StartTick, 1, new NoteOnEvent((SevenBitNumber)(byte)PhraseStartNote, (SevenBitNumber)(byte)NoteVelocity));
            Add(marker.EndTick, 2, new NoteOffEvent((SevenBitNumber)(byte)PhraseStartNote, (SevenBitNumber)0));
            Add(marker.StartTick, 1, new NoteOnEvent((SevenBitNumber)(byte)PhraseEndNote, (SevenBitNumber)(byte)NoteVelocity));
            Add(marker.EndTick, 2, new NoteOffEvent((SevenBitNumber)(byte)PhraseEndNote, (SevenBitNumber)0));
        }
```

The parser closes a span on a **note-on 106**, so in the committed `song.mid` every span is
zero-length `(start, start)` and phrase extents are rebuilt from syllables. `MidiTestFile.Phrase`
and the importer fixture put 106 at the **end** tick, the intended encoding. It is an asymmetry in
the commit, not a doc typo. It is invisible in the demo output because each span is exactly filled by
its syllables (verified: at tick 1920 the stream is `NoteOn 105`, `NoteOn 106`, `Lyric`,
`NoteOn 60`). Fixing it would not change a golden byte, but it matters for charts whose phrase tail
extends past the last syllable.

## 5. `lyrics.json`: the contract

The contract is plain classes in `Karaokay.Contracts/LyricsSchema.cs` — no schema file, no codegen.
A real fixture begins:

```json
{
  "version": 1,
  "songId": "synth-twinkle-variations",
  "source": "Synthetic",
  "title": "Twinkle Variations",
  "artist": "Public Domain Arr. K. Okay",
  "durationMs": 40400,
  "ticksPerQuarterNote": 480,
  "tempoMap": [
    {
      "tick": 0,
      "bpm": 120,
      "beatMs": 500
    },
```

Field semantics (ARCHITECTURE.md §4.1):

| Field | Meaning | Notes |
|---|---|---|
| `version` | schema major | `Validate` rejects ≠ 1 |
| `songId` | stable filesystem-safe id | from `song_id` or a slug |
| `source` | `"Synthetic"` / `"RockBand"` | validated |
| `title`, `artist` | display metadata | injected by caller; not in MIDI |
| `durationMs` | authoritative audio length | generator: score + 2 s; importer: ffprobe |
| `ticksPerQuarterNote` | provenance | always 480 here |
| `tempoMap` | informational | ms values are already flattened |
| `initialSilenceMs` | time before first audio | equals global offset |
| `vocalParts[]` | `vocals`, `harm1..3` | exactly one default |
| `phrases[]` | render windows, sorted | contain lines of syllables |
| `lines[]` | rendered rows | a new line starts after `lineBreakAfter` |
| `syllable.startMs` / `endMs` | atomic timing | authoritative, flattened |
| `talky` / `connectsToNext` / `lineBreakAfter` | render flags | flat fill; no sweep reset; last on line |
| `gaps[]` | lead-in + inter-phrase | `fromMs`, `toMs`, `countdownBeats`, `beatMs`, `renderCountdown` |

Times are `long` ms; `bpm`/`beatMs` are `double`. JSON has no int/double distinction and
System.Text.Json writes the shortest round-trippable form, so `120.0` emits as `120` and `500.0` as
`500`. A serializer that wrote `120.0` would change every fixture. `tempoMap` is informational; the
flattened ms fields are authoritative, which is why conversion happens on absolute ticks and only then
rounds.

### 5.1 The one serializer

```csharp
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };
```

Each option is a byte decision: two-space indentation and LF match `.editorconfig`; relaxed escaping
keeps non-ASCII titles readable; `PropertyNameCaseInsensitive` only affects reads, so hand-edited
fixtures survive casing errors. Names and order are pinned on the types with `[JsonPropertyName]` and
`[JsonPropertyOrder]`:

```csharp
    [JsonPropertyName("text")]
    [JsonPropertyOrder(0)]
    public string Text { get; set; } = string.Empty;
```

Without `JsonPropertyOrder`, serialization follows declaration (metadata) order — stable for a build,
but silently changes if someone reorders properties. With three fixtures and two demo copies compared
byte for byte, that is unacceptable; the attribute turns "don't reorder these fields" into contract.

Why one serializer? Because bytes are compared: the generator, importer, golden test, and `verify`
must agree. One class, one options object, one output. `verify` turns that into a check —
deserialize then re-serialize must reproduce the file exactly.

### 5.2 `Validate()` is structural, not semantic

Hand-written (no JSON Schema dependency, despite the roadmap's wording), returning one string per
violation. It checks: version 1; non-empty `songId`/`title`/`source` with `source ∈ {Synthetic,
RockBand}`; positive `durationMs` and `ticksPerQuarterNote`; a non-empty tempo map starting at tick 0
with strictly increasing ticks and positive finite BPMs; at least one part with a non-empty name and
exactly one default; phrases with `endMs > startMs`, sorted by `startMs`, non-null lines and
syllables; syllables with `endMs >= startMs`; gaps with `fromMs < toMs`.

It does **not** check `beatMs` vs `bpm`, phrase overlap (only ordering), syllables inside their
phrase, gap/song containment, `initialSilenceMs >= 0`, or duplicate part names. A green `verify`
means "well-formed", not "musically sensible".

## 6. `MidiLyricsParser.cs` line by line-ish

### 6.1 Entry and algorithm

```csharp
        if (midi.TimeDivision is not TicksPerQuarterNoteTimeDivision timeDivision)
        {
            throw new MidiLyricsParserException(
                "MIDI file uses SMPTE time division; the Rock Band vocal contract requires ticks per quarter note.");
        }
```

SMPTE ticks encode absolute time, so every later assumption would be wrong; failing loudly is better
than guessing. Then:

1. Identify tracks by first `SequenceTrackNameEvent`: `PART VOCALS` → vocals, `HARM1/2/3` →
   harmony, else ignored. The first track of a kind wins.
2. `ParseTrack` walks each track once (`tick += DeltaTime`), collecting syllables internally in
   ticks, marker spans in ticks, and `LineBreakAfter` flags.
3. Require a vocals track; otherwise throw.
4. Build tempo entries from `SetTempo` in **all** tracks, sorted, same-tick deduped, 120 BPM at tick
   0 inserted if absent.
5. Per part: phrases from marker spans if any, else the 1500 ms gap fallback.
6. Split into lines at `lineBreakAfter`; build gaps; convert ticks to ms; add the offset.
7. Assemble `LyricsDocument` with `DurationMs = options.DurationMs ?? max(1, lastEndMs)`.

Note-offs match by pitch through a `Queue<int>` per note (FIFO handles repeats and overlaps); notes
still open at track end are closed at the last tick, so a malformed chart yields a syllable instead
of an exception. Velocity-0 note-ons close notes.

### 6.2 Tick → milliseconds, once

```csharp
    internal static long TickToMilliseconds(long tick, TempoMap tempoMap)
    {
        ArgumentNullException.ThrowIfNull(tempoMap);
        var metric = TimeConverter.ConvertTo<MetricTimeSpan>(tick, tempoMap);
        return (long)Math.Round(metric.TotalMicroseconds / 1000.0, MidpointRounding.AwayFromZero);
    }
```

Each event converts from tick 0 through the whole map, so rounding cannot accumulate. Away-from-zero
is explicit. The offset is added **after** conversion (`TickToMilliseconds(tick, tempoMap) +
offsetMs`), never to ticks: mixing milliseconds into the tick domain would corrupt tempo math.

### 6.3 Phrase grouping

Marker spans are sorted, one `Phrase` is created per span, and each syllable joins the **last** span
whose start is ≤ its start; the window widens to include it. That tolerates sloppy charts and
explains why zero-length spans still work: the span seeds the start, syllables supply the end.

The fallback is one pass with a **strict** `>`:

```csharp
        for (var i = 1; i < ordered.Count; i++)
        {
            var syllable = ordered[i];
            if (syllable.StartMs - previousEnd > phraseGapMs)
            {
                phrases.Add(CreatePhrase(current, phraseStart, phraseEnd));
                current = [];
                phraseStart = syllable.StartMs;
                phraseEnd = syllable.EndMs;
            }
            else
            {
                phraseEnd = Math.Max(phraseEnd, syllable.EndMs);
            }

            current.Add(syllable);
            previousEnd = syllable.EndMs;
        }
```

At exactly 1500 ms phrases stay together. The unit test proves both sides: a second note at tick 2400
(2500 ms) leaves 2000 ms → 2 phrases; at tick 1440 (1500 ms) the gap is 1000 ms → 1 phrase.

### 6.4 Gaps and countdowns

```csharp
        var firstStart = phrases[0].StartMs;
        if (firstStart > 0)
        {
            gaps.Add(new PhraseGap
            {
                FromMs = 0,
                ToMs = firstStart,
                BeatMs = GetBeatMsAt(0, tempoPoints),
                CountdownBeats = 4,
                RenderCountdown = true,
            });
        }
```

The lead-in always asks for **4 dots** and always renders, even if shorter than four beats — a
rendering policy (full count-in whenever there is silence), so the renderer must handle dots closer
than one beat. It is omitted when the first phrase starts at 0.

Inter-phrase gaps are skipped when phrases touch (`toMs <= fromMs`), and otherwise use the beat
length in effect at the gap start (`GetBeatMsAt(fromMs)`) — that is why Twinkle's 17800→18400 gap
reports `beatMs: 600` after the 16000 ms tempo change.

```csharp
    internal static (int CountdownBeats, bool RenderCountdown) ComputeGapCountdown(
        long gapMs,
        double beatMs,
        long minCountdownGapMs)
    {
        if (beatMs <= 0 || double.IsNaN(beatMs) || double.IsInfinity(beatMs))
        {
            return (0, false);
        }

        var wholeBeats = (int)Math.Floor(gapMs / beatMs);
        var countdownBeats = Math.Min(4, Math.Max(0, wholeBeats));
        var renderCountdown = gapMs >= minCountdownGapMs && wholeBeats >= 2;
        return (countdownBeats, renderCountdown);
    }
```

Worked cases (`minCountdownGapMs = 2000`):

| Gap | Beat | floor | Dots | Render | Why |
|---:|---:|---:|---:|---|---|
| 4000 | 500 | 8 | 4 | yes | ≥2000, ≥2 beats, capped at 4 |
| 2200 | 500 | 4 | 4 | yes | ≥2000, ≥2 beats |
| 1500 | 500 | 3 | 3 | **no** | below 2000 ms |
| 900 | 500 | 1 | 1 | no | below both floors |
| 900 | 400 | 2 | 2 | no | 2 beats but below 2000 ms |

### 6.5 What the songs produce

Twinkle: 8 phrases at 2000–5500, 6000–10000, 10000–13500, 14000–17800, 18400–22600, 23200–27400,
28000–32200, 32800–38400; gaps are the lead-in (0→2000, 4 dots) plus six 500/600 ms gaps (1 dot, no
render). The 1→2 gap is absent because the windows meet at 10000.

Canyon demonstrates the fallback: 7 authored phrases at beats 4/12/20/36/45/54/68 merge into 3 —
2000→14000 (3 lines), 18000→31000 (3 lines), 34000→37500 (1 line) — with countdowns at 14000→18000
(4000 ms = 8 beats) and 31000→34000 (3000 ms = 6 beats), both capped at 4.

### 6.6 Offset and duration

`GlobalOffsetMs` (from `song.ini`'s `delay`) is added to every converted ms value — syllables, spans,
tempo points — and becomes `InitialSilenceMs` verbatim. It is never added to ticks or to
`DurationMs`. The unit test shifts a chart by 250 ms and asserts every phrase and syllable moves
exactly 250 while the lead-in still starts at 0. The generator passes music + 2 s; the importer lets
`DurationMs` default and later overwrites it with ffprobe (§8.6). A negative offset is not clamped
(times can go negative; `Validate` accepts it).

## 7. SyntheticGen: deterministic by construction

### 7.1 CLI

`Program.cs` delegates to a hand-rolled `SyntheticGenCli` with exits **0** success, **1** generation
failure (`InvalidOperationException`/`IOException`/`UnauthorizedAccessException`), **2** usage error.
Defaults: `seed = 42`, `out = "demo-tracks"`. For each song it prints slug, title, artist, and every
file's size and SHA-256 — the quickest way to see whether two runs agree.

### 7.2 Hardcoded scores, cosmetic seed

Layout, text, tempo, and note durations are literal `AddPhrase(...)` calls; only pitch and detune use
the RNG:

```csharp
    private static Random CreateRandom(int seed, int songIndex) =>
        new(unchecked((seed * 397) + songIndex));
```

```csharp
    private int PickNote()
    {
        var baseIndex = _noteIndex % _scale.Length;
        var jitter = _random.Next(-1, 2);
        var index = ((baseIndex + jitter) % _scale.Length + _scale.Length) % _scale.Length;
        return Math.Clamp(_scale[index], 48, 84);
    }

    private double PickDetune() => (_random.NextDouble() * 2.0 - 1.0) * 0.0025;
```

Exactly two draws per note (`Next(-1, 2)`, then `NextDouble()`), and an independent stream per song
(`seed * 397 + index`), so changing one song's length does not shift another's melody. The seed is
forbidden from touching timing or layout because `lyrics.json` must be identical across seeds: the
fixture tests the transform, and the web demo's timeline should not change when someone tries
`--seed 7`. Only the audio pitch may vary. Verified on this commit:

| File | Seed 42 vs 7 |
|---|---|
| `song.mid` | differs (note numbers) |
| `vocals.opus` | differs (frequencies) |
| `instrumental.opus` | **identical** (fixed chord progressions) |
| `lyrics.json` | **identical** (no pitch in contract) |

The songs: Twinkle is the marked multi-tempo song; Telegram is all-talky with 2000 ms gaps;
Canyon has the 8- and 6-beat instrumental breaks.

`ScoreBuilder.AddPhrase` places syllables sequentially from `startBeat`, one `Beats` each, and
records a marker `[startBeat, cursorBeat]` when asked. `Build()` derives a chord per 4-beat bar from
fixed progressions and sets `DurationMs = TickToMilliseconds(lastBeat) + TailMs` (`TailMs = 2000`).

### 7.3 The composer and its total event order

`MidiScoreComposer` writes format 1, 480 PPQ, two tracks: a conductor track (name + delta-encoded
`SetTempo`) and `PART VOCALS`. The vocal track collects `(tick, priority, sequence, event)` tuples and
sorts them by tick, then priority, then insertion sequence. The priorities *are* the input contract:

| Priority | Event | Why |
|---:|---|---|
| 0 | track name | first |
| 1 | marker note-on 105/106 | opens spans at phrase start |
| 2 | note-offs (syllables, markers) | close previous note before the next lyric |
| 3 | `[line_end]` text | attaches to the syllable that just ended |
| 4 | `Lyric` | must immediately precede its note-on |
| 5 | syllable `NoteOn` | last at its tick |

Without this sort, event order would follow list iteration and bytes would drift. Each syllable adds
its note-off at the end tick, its lyric and note-on at the start tick, and `[line_end]` at the end
tick when flagged; events are then delta-encoded.

### 7.4 Audio rendering

`AudioStemBaker` renders PCM itself, then hands it to ffmpeg:

```csharp
        var frameCount = checked((int)(score.DurationMs * SamplesPerMs));
        var instrumental = RenderInstrumental(score, frameCount);
        var vocals = RenderVocals(score, frameCount);
```

with 48 kHz, 2 channels, `SamplesPerMs = 48`. A 40,400 ms song therefore has exactly
`40,400 × 48 = 1,939,200` frames = 7,756,800 bytes of s16le PCM. No resampling, no rounding: duration
in ms and sample count are the same fact twice.

```csharp
    private static double MidiToFrequency(int midiNote) =>
        440.0 * Math.Pow(2.0, (midiNote - 69) / 12.0);
```

A4 (69) = 440 Hz, C4 (60) = 261.6256 Hz. Vocals multiply by `1 + Detune` (≤ ±0.25 %). Both stems use
a linear attack/release envelope (`Math.Min` when a note is shorter than attack+release) so notes do
not click: instrumental 10 ms/60 ms per beat plus a 30 ms decaying 1760 Hz click every fourth beat;
vocals 5 ms/5 ms per syllable at gain 0.30. Each beat restarts from phase 0, so the instrumental is a
train of enveloped tones rather than one continuous oscillator — simple and deterministic. Stereo is
written by duplicating each 16-bit sample:

```csharp
            bytes[offset++] = low;
            bytes[offset++] = high;
            bytes[offset++] = low;
            bytes[offset++] = high;
```

Values are clamped to ±1 before scaling by 32767 and rounding away from zero, so summed chord tones
plus click cannot wrap into distortion.

### 7.5 The ffmpeg call and its determinism flags

```csharp
                .Add("-c:a").Add("libopus")
                .Add("-b:a").Add("64k")
                .Add("-ar").Add("48000")
                .Add("-ac").Add("2")
                .Add("-map_metadata").Add("-1")
                .Add("-fflags").Add("+bitexact")
                .Add("-flags:a").Add("+bitexact")
                .Add("-threads").Add("1")
```

| Flag | What it removes |
|---|---|
| `-f s16le -ar 48000 -ac 2` | ambiguity about raw input format (no container header) |
| `-c:a libopus -b:a 64k` | codec/bitrate variability; VBR is deterministic for identical input |
| `-ar 48000 -ac 2` (output) | resampling/downmix, whose float math can differ across builds |
| `-map_metadata -1` | input metadata (including the source filename) |
| `-fflags +bitexact` / `-flags:a +bitexact` | format- and codec-level version fields |
| `-threads 1` | scheduler-dependent frame ordering |

For one ffmpeg/libopus build this makes encoding a pure function of the PCM bytes. The guarantee is
**scoped to the toolchain**: a different libopus version can legitimately emit different bytes, which
couples the committed demo assets to the CI image (`ubuntu-latest` may move). Pinning a container or
ffmpeg build is the fix; not committing the assets would hide the coupling, not remove it. Contrast
`StemMixer` (§8), which deliberately omits bitexact flags because its output is asserted by
properties (layout, selection, loudness), not bytes.

### 7.6 Writing `lyrics.json` by reading your own output

```csharp
        var midiPath = Path.Combine(songDir, MidiFileName);
        File.WriteAllBytes(midiPath, MidiScoreComposer.Serialize(score));

        MidiFile readBack;
        using (var stream = File.OpenRead(midiPath))
        {
            readBack = MidiFile.Read(stream);
        }
```

If the writer parsed the in-memory `MidiFile` instead, `lyrics.json` would prove nothing about the
committed `song.mid`; a serialization bug could survive until a golden test re-parsed the file. By
writing, reading back, and parsing the bytes on disk, `lyrics.json = f(committed song.mid)` is true
by construction. `LyricsTimelineWriter` then validates the document (refusing to emit a bad fixture)
and writes UTF-8 **without BOM**, because a BOM would add three unexpected bytes.

### 7.7 The reusable entry point

`SyntheticSongGenerator.GenerateAsync(seed, outDir)` is public so the determinism test calls it
in-process instead of spawning the CLI. It produces the four files, then hashes each
(`Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()`) into a `GeneratedSong`. The CLI
and the test share everything except argument parsing. Seed 42 reproduces the committed files on this
commit; a second run reproduces them again.

## 8. The Importer: a real CLI

### 8.1 Dispatch and exit codes

```csharp
            case "scan":
                if (rest.Length != 1)
                {
                    return UsageError("scan expects exactly one <root> directory.");
                }

                return ScanCommand.Execute(rest[0]);

            case "import":
                return await ImportCommand.ExecuteAsync(rest, CancellationToken.None).ConfigureAwait(false);
```

Exit codes match the generator: **0** success, **1** failure, **2** usage. Usage goes to stderr,
results to stdout. `scan` is read-only; `import` writes; `verify` validates.

### 8.2 `song.ini` tolerance

`SongIniReader` reads `name`, `artist`, `delay`, `charter`, `song_id`, forgivingly: keys are
case-insensitive; blank and `;`/`#` lines are comments; a missing `[song]` header is accepted (the
initial section is `"song"`, so top-level keys count); other sections are ignored; whitespace is
trimmed; the value starts after the **first** `=`; duplicate keys keep the last; `delay` accepts an
integer or float, invariant, rounded away from zero, and anything unparseable becomes 0; blank
`charter`/`song_id` become `null`. The importer ingests a library, it does not police one.

### 8.3 Finding the chart

```csharp
    private static readonly string[] CandidateFileNames =
    [
        "notes.mid",
        "notes.midi",
        "song.mid",
        "song.midi",
        "chart.mid",
    ];
```

`notes.mid` is the RB convention; `song.mid` lets the synthetic layout be imported by the same tool.
Files are enumerated once and compared `OrdinalIgnoreCase`, which matters on case-sensitive
filesystems (`Notes.MID`). Failure throws `FileNotFoundException` naming the first candidate.

### 8.4 Import flow

1. Parse `directory` and optional `--out` (anywhere; exactly one positional).
2. Require the directory and `song.ini`; missing ini is exit 1, not a guess.
3. Discover the MIDI; resolve the id: safe `song_id`, else slug of `name`, else slug of the folder.
   `Slugify` lowercases invariantly, keeps ASCII letters/digits, collapses other runs to one `-`,
   falls back to `"song"`.
4. Parse with `GlobalOffsetMs: ini.DelayMs`, `source: "RockBand"`.
5. Enumerate `.ogg`/`.oga`; select instrumental stems; fail if none; select vocals (may be empty).
6. Target is `<outRoot>/<songId>`, default out root `<dir>/karaokay-out`.
7. Bake `instrumental.opus`; bake `vocals.opus` or the silent fallback.
8. Probe the encoded instrumental's duration and overwrite `lyrics.DurationMs`.
9. Serialize `lyrics.json`; print sizes, stem provenance, measured LUFS; return 0.

Step 8 is deliberate: an encoded Opus container is slightly longer than the source (a "2 second"
sine probes as 2016 ms because of pre-skip/padding), and `durationMs` should describe the file the
renderer plays. If ffprobe is missing or fails, the parser-derived duration stands.

### 8.5 Stem selection and the mix graph

```csharp
        return oggFiles
            .Where(IsAudioStem)
            .Where(file => !HasStemPrefix(file, "crowd") && !HasStemPrefix(file, "vocals"))
            .OrderBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file, StringComparer.Ordinal)
            .ToList();
```

`IsAudioStem` accepts `.ogg` and `.oga`; `HasStemPrefix` compares the **file name**, so `CROWD_1.ogg`
is excluded. Sorting by name then path makes ffmpeg input order deterministic. The vocal selector is
the mirror image (`vocals*` only). The graph is
`[0:a][1:a]…amix=inputs=N:duration=longest[a]`. `amix`'s default normalization divides each input by
N to avoid clipping; `duration=longest` keeps the full length; loudnorm then compensates the level.

### 8.6 Two-pass loudness normalization

`BakeAsync` runs ffmpeg twice with the same graph. Pass 1 appends
`loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json`, writes to `-f null -`, and `ParseLoudnormOutput`
reads `input_i`, `input_tp`, `input_lra`, `input_thresh`, `target_offset` from the JSON in stderr.
Pass 2 substitutes:

```csharp
        public string ToLoudnormFilter() =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"loudnorm=I=-16:TP=-1.5:LRA=11:measured_i={InputI:0.00}" +
                $":measured_tp={InputTp:0.00}:measured_lra={InputLra:0.00}" +
                $":measured_thresh={InputThresh:0.00}:offset={TargetOffset:0.00}:linear=true:print_format=json");
```

and encodes libopus 96 kbit/s, 48 kHz, stereo, `-map_metadata -1`. Single-pass loudnorm is adaptive:
it estimates as it goes and moves gain over time, which can pump and lands near, not on, the target.
Feeding measured values back with `linear=true` applies **one constant gain**, preserving internal
dynamics and hitting the target precisely. If the linear gain would break the true-peak ceiling,
ffmpeg falls back to dynamic mode and `offset` compensates; the end-to-end test tolerates ±1 LU.

> **LUFS sidebar.** Loudness Units relative to Full Scale (ITU-R BS.1770 / EBU R128) measures
> *perceived* loudness, not peaks. The signal is K-weighted (high-pass plus high-shelf approximating
> the ear), mean-squared over 400 ms blocks, then gated: blocks below −70 LUFS absolute and below
> −10 LU relative to the ungated mean are discarded. That is "integrated loudness". Targets are
> conventions: EBU broadcast −23 LUFS, Spotify/YouTube around −14, Apple Podcasts −16. Karaoke uses
> **−16 LUFS**, true peak **−1.5 dBTP**, loudness range 11, so a synthetic demo and a real RB
> multitrack feel equally loud beside a YouTube video. ±1 LU accounts for lossy encoding,
> measurement windows, and dynamic-mode residue.

One footnote: `LoudnessTargetLufs = -16` is used only by the summary printer; the actual target is the
literal `I=-16` inside the filter strings, so changing the constant alone would not change
normalization.

### 8.7 Silent fallback, ffprobe, and `verify`

With no `vocals*` stem, `CreateSilentOpusAsync` writes one second of stereo silence (`anullsrc`,
`-t 1`, libopus 96k), so the three-file layout always exists and the renderer needs no "missing
stem" branch. The trade-off: `vocals.opus` may be 1 s while the instrumental is song-length.

`TryProbeDurationMsAsync` runs `ffprobe -v error -show_entries format=duration -of csv=p=0`, parses
seconds invariantly, and rounds `seconds × 1000` away from zero; missing ffprobe or unparseable
output returns `null` instead of throwing.

`verify` runs six checks: `instrumental.opus` exists/non-empty, `vocals.opus` exists/non-empty,
`lyrics.json` exists/non-empty, it deserializes, structural validation is clean, and re-serializing
reproduces the file **byte for byte**. Checks 4–6 catch truncated JSON, schema drift, and hand-edits
that changed formatting. (Example output, from the fake folder in §12.5: six `PASS` lines and
`verify: all checks passed`.)

`scan` is the read-only sibling: it recurses (ordinal-sorted, skipping inaccessible directories),
requires `song.ini` + a discoverable MIDI + at least one stem, and prints the relative path,
metadata, delay, chart file, instrumental/vocal stems, and a separate `crowd=[…]` list. Finding
nothing prints "no songs found" and still exits 0 — an empty library is not an error.

## 9. RoomCodeGenerator: small but instructive

```csharp
    /// <summary>23 unambiguous uppercase letters (26 minus <c>I</c>/<c>O</c>/<c>Q</c>).</summary>
    public const string Alphabet = "ABCDEFGHJKLMNPRSTUVWXYZ";

    /// <summary>Room codes are always four characters long.</summary>
    public const int CodeLength = 4;
```

Four testable choices:

* **Injectable randomness** (`Random? random = null`, default `Random.Shared`) lets tests replay the
  exact draw sequence instead of asserting probabilities.
* **Ordinal collision set**: `activeCodes` is copied into `HashSet<string>(StringComparer.Ordinal)`,
  and `_activeCodes.Add(code)` is both the collision test and the reservation, so two calls on one
  instance cannot collide.
* **Bounded retries**: `MaxAttempts = 100`, then `InvalidOperationException` saying the set looks
  exhausted. An unbounded loop would hang.
* **A human alphabet**: no `I`, `O`, `Q` (letters easily misread as digits or as each other), no
  digits, no lowercase.

That last point is where prose and code disagree. ARCHITECTURE.md §4.2 says
"`ABCDEFGHJKLMNPRSTUVWXYZ` — 24 letters (no `I O Q 0 1`) → 331,776 codes." The string has **23**
letters (26 − 3), and `24⁴ = 331,776`; `23⁴ = 279,841`. The tests encode the correct arithmetic:
`AlphabetIsTheDocumentedUnambiguousLetters` asserts the literal and `26 - 3`, and
`ExhaustedActiveSetThrowsAfterBoundedRetries` builds all `279_841` codes, asserts the count, then
asserts the generator throws. The literal is authoritative; the prose slipped. The test file's own
comment still says "24-letter" and one inline comment says `random.Next(24)` — both stale; the code
uses `Alphabet.Length`, 23.

## 10. The test suite as a safety net

### 10.1 Inventory: 44 tests

| Project | Tests | Breakdown |
|---|---:|---|
| UnitTests | 28 | `LyricsJsonTests` 4; golden 5 (theory ×3 + 2 facts); parser 13 (6 facts + 2 + 5 theory rows); room code 4; determinism 1; sanity 1 |
| IntegrationTests | 5 | stem selection 3; end-to-end 1; sanity 1 |
| ArchitectureTests | 11 | 8 dependency rules + 2 positive controls + sanity 1 |
| **Total** | **44** | |

The three sanity tests prove each host discovers and runs tests, so a misconfigured runner cannot
report zero tests as success.

### 10.2 Golden files

```csharp
        var actualBytes = Encoding.UTF8.GetBytes(LyricsJson.Serialize(parsed));
        Assert.True(
            actualBytes.AsSpan().SequenceEqual(goldenBytes),
            $"Parsed '{slug}' does not match the golden fixture byte-for-byte " +
            $"(expected {goldenBytes.Length} bytes, produced {actualBytes.Length}).");
```

A fixture is "golden" when it is produced by the real implementation, committed, compared exactly
(so property order, whitespace, number formatting, and encoding are locked), and regenerated only on
purpose. Field-by-field assertions would miss all of that. The theory runs three slugs; each case
asserts both that re-parsing the committed `song.mid` reproduces the golden bytes and that the web
client's demo copy equals it — guarding against fixture drift between test copy and served asset.

`TestPaths` walks up from `AppContext.BaseDirectory` looking for `karaokay.sln`, because runners set
the working directory to the output folder and relative paths are a trap. One honest limitation: the
test reconstructs `songId`/`title`/`artist` from the golden document (they are not in MIDI), so it
verifies the transform, not metadata provenance.

### 10.3 Markers: a test of precedence

The first golden fact asserts 3 tempos, 8 phrases, and at least one inter-phrase gap in (0, 1500) ms.
The stronger test removes every 105/106 event from the committed MIDI
(`vocalsChunk.RemoveTimedEvents(IsPhraseMarker)`), re-parses, and asserts
`Assert.Single(vocals.Phrases)`. If someone "simplified" the parser to always use the gap heuristic,
the first test would still pass (the fixture has 8 phrases) but this one would fail. It proves a
decision, not an output.

### 10.4 Determinism: 12 hashes, twice over

`Seed42IsReproducibleAndMatchesTheCommittedDemoTracks` generates into `tempRoot/a` and `tempRoot/b`,
then for 3 songs × 4 files asserts `hashA == hashB` (12 comparisons), and for each file asserts the
committed demo exists, has the same hash, **and** is byte-equal. The two halves catch different bugs:
a-vs-b catches nondeterminism (unseeded RNG, timestamp metadata, thread-dependent encoding);
vs-committed catches intentional/unintentional pipeline changes. Output can be perfectly deterministic
and still wrong relative to the fixture.

### 10.5 MIDI fixtures in code

```csharp
        AddEvent(endTick, 2, new NoteOffEvent(Seven(noteNumber), (SevenBitNumber)0));
        AddEvent(startTick, 4, new LyricEvent(lyric));
        AddEvent(startTick, 5, new NoteOnEvent(Seven(noteNumber), (SevenBitNumber)NoteVelocity));
        if (lineBreakAfter)
        {
            AddEvent(endTick, 3, new TextEvent(LineEndMarker));
        }
```

Binary fixtures are opaque in review, multiply per edge case, and go stale; a builder shows intent
and can construct exact-threshold gaps, missing markers, and velocity-0 note-offs cheaply. The cost is
a second implementation that can drift — which is why the real fixtures come from the composer and
this builder is only for targeted unit tests. It puts 106 at `endTick` (the correct encoding), which
is how the §4 asymmetry was spotted.

### 10.6 Integration with real ffmpeg

`StemSelectionTests` use a listing with five audio stems, `crowd.ogg`, `CROWD_1.ogg`,
`vocals.ogg`, `vocals_1.ogg`, a PNG, and a MIDI. They assert exactly 5 instrumental and 2 vocal
stems, sorted; the graph says `amix=inputs=5`; the argument list has exactly five `-i` and contains
no "crowd"/"vocals" string — the "crowd never reaches the mix" guarantee at argument level.

`ImporterEndToEndTests` builds a fake RB folder with ffmpeg sine stems (`libvorbis -q:a 4`), a
contract-shaped `notes.mid`, and `delay = 250`, then runs `ImportCommand`. It asserts: exactly three
files; slug `test-song`; `initialSilenceMs == 250`; byte-identical re-serialization; 2 phrases and a
`Hel` syllable with `ConnectsToNext`; parsing the same MIDI with offset 0 and comparing every
phrase/syllable start and end proves a uniform **+250 ms** shift; instrumental selection excludes
`crowd.ogg`/`vocals.ogg`; graph `inputs=3`; LUFS in `[-17, -15]`; `verify` exits 0. The LUFS
assertion is the payoff of two-pass normalization; the +250 comparison is the payoff of offsetting
after conversion. Neither is expressible as a byte-golden test.

`FfmpegGuard.Require()` throws "install ffmpeg" instead of skipping. A skipped test is a silent hole;
a failed test is actionable. CI installs ffmpeg, so the guard only fires on developer machines.

### 10.7 Architecture rules and positive controls

```csharp
/// <remarks>
/// These checks intentionally use <see cref="Assembly.GetReferencedAssemblies"/> directly instead
/// of NetArchTest.Rules 1.3.2: on net10.0 that library mis-reports dependencies (a probe showed it
/// claiming Infrastructure does not reference Karaokay.Contracts, which it demonstrably does).
/// The positive-control tests at the bottom prove the reflection probe sees real references.
/// </remarks>
```

NetArchTest 1.3.2 predates .NET 10, and its dependency detection returned false for a dependency the
assembly provably has. Rather than trust a green suite built on a broken detector, the rules were
rewritten over reflection: each subject assembly is checked against a forbidden list that includes
both project names and packages (`Melanchall.DryWetMidi`, `CliWrap`,
`Microsoft.EntityFrameworkCore`). A rule is just:

```csharp
    [Fact]
    public void ContractsArePureBclDataContracts()
    {
        AssertNoDependency(ContractsAssembly, ContractsForbidden);
    }
```

Then come two **positive controls**:

```csharp
    [Fact]
    public void DependencyProbeSeesTheKnownInfrastructureToContractsReference()
    {
        var detected = FindReferencedDependencies(InfrastructureAssembly, KnownInfrastructureReferences);
        Assert.Contains("Karaokay.Contracts", detected);
    }
```

and the same for `SyntheticGen → Infrastructure`. A positive control asserts the *detector* works,
using a fact known independently (the csproj files contain those `ProjectReference`s). It matters
because "no forbidden dependency found" is indistinguishable from "the detector found nothing at
all". Without one, a reflection change or load-context quirk turns every rule into a test that passes
for the wrong reason — a vacuous green. If the probe cannot see a dependency literally in the csproj,
the control fails loudly and tells you the other rules are meaningless.

Assemblies the test can name a type from load via `typeof(...).Assembly`; `Api`, `AppHost`, and
`Importer` have no such type, so they load with `Assembly.LoadFrom` on the `.dll` beside the test
binary. Violation messages print the full referenced-assembly list, so no debugger is needed.

## 11. The determinism discipline checklist

1. **Separate seed from score**: hardcode timing/layout/text; the seed picks only cosmetic audio.
2. **Derive per-song RNG streams arithmetically** (`seed * 397 + index`), never `Random.Shared`.
3. **Never randomize anything in the contract**; if it is in `lyrics.json`, it is deterministic.
4. **Compute durations from the score**, not wall-clock or I/O.
5. **Use exact frame counts** (`durationMs × samplesPerMs`) and one sample rate; never resample.
6. **Keep DSP pure**: `Math.Sin`/`Math.Pow`, explicit envelopes, no timers/threads/environment;
   clamp before quantizing and choose a rounding mode explicitly.
7. **Encode with a fixed invocation**: input format, rate, channels, codec, bitrate,
   `-map_metadata -1`, `+bitexact`, `-threads 1`.
8. **Write the artifact, read it back, parse the bytes** when fixtures must correspond to it.
9. **Use one serializer** with explicit names/order, two-space indent, LF, UTF-8 without BOM, and
   shortest-round-trip numbers.
10. **Sort before serializing/encoding**: total order with tie-breakers (tick, priority, sequence;
    filename, path) and ordinal comparers for identifiers.
11. **Format and parse invariantly everywhere**, with `InvariantGlobalization` as a backstop.
12. **No timestamps, GUIDs, hostnames, or absolute paths in artifacts** (temp PCM stays outside).
13. **Pin versions centrally** and commit the generated fixtures.
14. **Encode the claim as tests**: generate twice and hash; compare committed bytes; regenerate
    golden output from committed input.

## 12. Try it yourself: six experiments

### 12.1 Seed 42 twice

```bash
dotnet run --project src/tools/Karaokay.SyntheticGen -c Release -- --seed 42 --out /tmp/a
dotnet run --project src/tools/Karaokay.SyntheticGen -c Release -- --seed 42 --out /tmp/b
diff -r /tmp/a /tmp/b && echo "byte-identical"
```

Expected: no diff and matching SHA-256s for all 12 files — the unit-test property without xUnit.

### 12.2 Change only the seed

```bash
dotnet run --project src/tools/Karaokay.SyntheticGen -c Release -- --seed 7 --out /tmp/c
for f in song.mid instrumental.opus vocals.opus lyrics.json; do
  cmp -s /tmp/a/synth-twinkle-variations/$f /tmp/c/synth-twinkle-variations/$f \
    && echo "same  $f" || echo "DIFF  $f"
done
```

Expected for all three songs: `DIFF song.mid`, `same instrumental.opus`, `DIFF vocals.opus`, `same
lyrics.json`. Pitch jitter changes note numbers and vocal frequencies; chords are fixed and the
contract has no pitch. If `lyrics.json` differs, randomness leaked into timing.

### 12.3 Corrupt one golden byte

Change a digit in `src/tests/Karaokay.UnitTests/fixtures/golden/synth-twinkle-variations.json` (for
example the first `"startMs": 2000` to `2001`), then:

```bash
dotnet test src/tests/Karaokay.UnitTests --filter "FullyQualifiedName~MidiLyricsParserGoldenTests"
```

Expected: that slug's case fails at the first assertion — parsed bytes no longer equal the edited
fixture. (The demo-copy assertion behind it would also fail, but xUnit stops at the first failure.)
The other slugs pass; the counts/tempo test still passes. Revert the digit to go green. Editing the
demo copy instead fails only the second assertion, which shows which copy is the source of truth.

### 12.4 Change one tempo

Change Twinkle's second tempo from `100` to `101` (`new TempoChange(32 * 480L, 100)` in
`SyntheticSongs.cs`), then:

```bash
dotnet test src/tests/Karaokay.UnitTests --filter "FullyQualifiedName~SyntheticGenDeterminismTests"
```

Expected: failure on all four twinkle files — `song.mid` carries a different `SetTempo`, the lyric
milliseconds shift (60000/101 is not whole ms per beat, so boundaries round), and both Opus files
change. Golden tests still pass because they read the **committed** MIDI, not the score source.

### 12.5 Add `crowd.ogg` to an importable folder

```bash
rm -rf /tmp/rb && mkdir -p /tmp/rb/song
cp src/clients/web/public/demo-tracks/synth-countdown-canyon/song.mid /tmp/rb/song/notes.mid
printf '[song]\nname = Crowd Test\nartist = Nobody\ndelay = 0\n' > /tmp/rb/song/song.ini
for f in guitar drums crowd vocals; do
  ffmpeg -hide_banner -loglevel error -f lavfi -i sine=frequency=220:duration=2 \
    -c:a libvorbis -q:a 4 -y /tmp/rb/song/$f.ogg
done
dotnet run --project src/tools/Karaokay.Importer -c Release -- scan /tmp/rb
```

Expected scan line:

```
song | Crowd Test | Nobody | delay=0 ms | midi=notes.mid | instrumental=[drums.ogg, guitar.ogg] | vocals=[vocals.ogg] | crowd=[crowd.ogg]
```

`crowd.ogg` is reported but not mixed. Then import and verify:

```bash
dotnet run --project src/tools/Karaokay.Importer -c Release -- import /tmp/rb/song --out /tmp/rb/out
dotnet run --project src/tools/Karaokay.Importer -c Release -- verify /tmp/rb/out/crowd-test
```

Expected: `mixed from 2 stem(s): drums.ogg, guitar.ogg`, loudness near `-16.0 LUFS`, six PASS lines.
`lyrics.json` differs in size from the committed canyon fixture because `durationMs` is now the
probed stem length (about 2016 ms for a "2 second" Opus), not 39500.

### 12.6 Change the alphabet

Add `I` to `RoomCodeGenerator.Alphabet`, then:

```bash
dotnet test src/tests/Karaokay.UnitTests --filter "FullyQualifiedName~RoomCodeGeneratorTests"
```

Expected: the literal/`26 - 3` test fails; the exhaustion test fails at `279_841` (a 24-letter
alphabet yields 331,776); the generation and seeded-collision tests **still pass**, because they draw
from `Alphabet.Length`. Tests that encode a decision catch the change; tests that encode only the
mechanism do not.

## 13. Where to go next (Slice 2+)

The contract is the input to everything after it. Slice 2 (ARCHITECTURE §7) adds song/media endpoints
and a browser stage that treats `lyrics.json` as its timeline: phrases become windows, lines become
rows, `startMs`/`endMs` drive the sweep fill, `talky` switches to a flat fill, `connectsToNext` keeps
the sweep from resetting, and `gaps` feed countdown dots. The three `demo-tracks` folders already sit
in the web client's `public/` tree, so the stage can bundle them with no backend. Later, harmony
parts (`harm1..harm3`) become renderable, the API returns `lyrics.json` with each song, and Slice 3's
rooms/queue wrap the same contract in SignalR messages (ARCHITECTURE §4.3) rather than changing it.
The importer remains the ingestion tool for real multitrack libraries, the generator remains the
fixture factory, and the golden files remain the shared regression lock for every renderer — which is
why the discipline in §11 is worth keeping.
