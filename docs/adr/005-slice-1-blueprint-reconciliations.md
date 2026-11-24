# ADR-005: Slice 1 blueprint reconciliations — corrected room-code alphabet, reflection-based architecture rules, and code-level lyrics validation

- **Status:** Accepted
- **Date:** 2026-11-24
- **Deciders:** Architect
- **Context level:** Documentation governance / Slice 1 (core contracts & importer tooling)
- **Related:** [ARCHITECTURE.md §4.2](../ARCHITECTURE.md#42-ef-core-entity-models) (room-code alphabet), [ARCHITECTURE.md §4.1](../ARCHITECTURE.md#41-lyricsjson--canonical-karaoke-timeline) (lyrics contract), [ADR-004](004-dual-source-media-pipeline.md) (importer pipeline and CI media guard), [ADR-006](006-line-wrapping-ownership.md) (the other Slice 1 reconciliation)

## Context

Slice 1 implemented the core contracts and importer tooling that Slice 2 consumes. During
implementation and review, three statements in `docs/ARCHITECTURE.md` turned out to be
**internally inconsistent or not implementable as written**. The implementation resolved each
one, but the blueprint was not updated at the same time, so the repository briefly had two
descriptions of the same contract — exactly the condition that turns a blueprint into folklore.

The three inconsistencies:

1. **Room-code alphabet.** The blueprint printed a 23-letter alphabet (`ABCDEFGHJKLMNPRSTUVWXYZ`)
   while claiming "24 letters … 331,776 codes". `24⁴ = 331,776` and `23⁴ = 279,841`; the printed
   alphabet was already correct, the count was not. The implementation (commit `3f998e1`) uses
   **23 letters → 279,841 codes**.

2. **Architecture enforcement.** The blueprint named **NetArchTest** as the dependency-rule
   engine. On `net10.0` the library mis-reported a dependency the assembly provably had, and the
   rules needed **per-project positive controls** — a test that proves the detector sees a
   reference known to exist in the `.csproj` — which NetArchTest does not provide cleanly.
   `Karaokay.ArchitectureTests` therefore uses **hand-written reflection rules**
   (`src/tests/Karaokay.ArchitectureTests/ArchitectureRules.cs`) with positive controls, and the
   unused package pin was removed.

3. **Lyrics validation.** The blueprint specified a **JSON Schema check** for `lyrics.json`. That
   would have meant maintaining a schema file *and* the C# contract model — two descriptions of
   one document, guaranteed to drift. Validation is instead **code-level**, in
   `LyricsJson.Validate` (`src/core/Karaokay.Contracts/LyricsJson.cs`), sharing the same
   deterministic serializer used for round-trip verification. One model, one validator, no
   duplicated contract.

## Decision

Record the corrected values as authoritative and treat `docs/ARCHITECTURE.md` as revised to match
them:

- **Room codes:** 23-letter alphabet (`ABCDEFGHJKLMNPRSTUVWXYZ`, no `I O Q`) → **279,841** codes.
- **Architecture rules:** reflection-based dependency checks in
  `src/tests/Karaokay.ArchitectureTests/ArchitectureRules.cs`, each subject assembly paired with
  positive controls that prove the probe detects real references.
- **Lyrics validation:** `LyricsJson.Validate` in `src/core/Karaokay.Contracts/LyricsJson.cs`,
  with a deterministic serialize/deserialize round-trip; no separate JSON Schema file.

These are **reconciliations, not new architecture**. The intent of the original decisions is
unchanged; only the mechanism and one arithmetic claim were corrected.

## Consequences

**Positive**

- The blueprint and the code now describe the same contract, so Slice 2 can be built against a
  single source of truth.
- The corrected room-code count makes capacity planning and the `MaxAttempts` discussion honest
  (279,841 is still ample for a party app, but it is not 331,776).
- Positive controls turn "no forbidden dependency found" into a *tested* claim rather than a
  green checkmark that may simply mean the detector is blind.
- One validator for one document eliminates the schema/model drift that a separate JSON Schema
  would have invited.

**Negative / Accepted risks**

- The architecture rules are bespoke code (roughly a few dozen lines) that the team owns and must
  maintain, instead of a pinned third-party package. Accepted: the rules are simple reflection
  queries and the positive controls are the real safety net.
- `LyricsJson.Validate` is the only gate; there is no external schema artifact for non-.NET
  consumers. Accepted: the web renderer consumes the document type-safely and the contract is
  deliberately small.

**Governance**

Future edits to `docs/ARCHITECTURE.md` that change a contract, a count, or an enforcement
mechanism must be recorded — either as a **new ADR** or as a **blueprint version bump with a
version-history entry** (see ADR-006 and the v1.2 history). Silent in-place edits to authoritative
values are not acceptable: the blueprint is a contract, and the ADR trail is how the contract's
history stays auditable.

## Alternatives considered

| Alternative | Why rejected |
|---|---|
| Leave the blueprint as written and rely on the code | Reintroduces two sources of truth; the next slice's author would implement the printed 24-letter alphabet and the wrong capacity math. |
| Fix the three statements in place without an ADR | The blueprint's version history would not explain *why* the values changed; reviewers could not distinguish a correction from a typo. |
| Keep NetArchTest and add positive controls around it | The reported false negative was in the detector itself; wrapping a broken probe in positive controls would fail the controls for the wrong reason. |
| Emit a JSON Schema from the C# model and validate with it | Adds a build step and a second consumer for a document only the importer and renderers read; the round-trip serializer already proves the exact wire shape. |

## Validation plan

- `docs/ARCHITECTURE.md` v1.2 history cites this ADR; §4.2 states 23 letters / 279,841 codes.
- `RoomCodeGeneratorTests` assert the 23-letter alphabet and four-character shape; the generator's
  capacity math is consistent with 279,841.
- `Karaokay.ArchitectureTests` runs the reflection rules **plus** positive controls in CI, so a
  blind detector fails instead of passing silently.
- Importer `verify` validates every `lyrics.json` through `LyricsJson.Validate` and a
  byte-identical round-trip parse (see `ARCHITECTURE.md` §7).
- The v1.2 header links this ADR and ADR-006, making the reconciliation discoverable from the
  blueprint itself.
