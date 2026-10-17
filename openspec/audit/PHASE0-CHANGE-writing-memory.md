# Writing memory

**Target phase:** 3 (last in phase — **requires an explicit owner privacy
decision before implementation**). **Upstream provenance:** related to
#841 / [`c3d17c1`](https://github.com/FuJacob/cotabby/commit/c3d17c1a6f709d8181a8d888f33e23f03014c428)
(opt-in typing history) — **deliberately not ported**
(`UPSTREAM_PORTING_PLAN.md:37`, `README.md:160`): "It would create the
first durable on-device store of user text; needs an explicit privacy
decision (encryption/key custody, retention, delete-all, Cotypist import
provenance) first." **Risk level:** High (privacy). **Flag:**
`EnableWritingMemory` (default `false`), hard-gated on the owner decision
recorded in this change's acceptance criteria.

## Why

The prompt pipeline conditions on a persona line and language
instruction (`SuggestionRequest.cs:32-36`, `BaseCompletionPromptRenderer`)
but knows nothing about *how this user writes* — their recurring terms,
project names, tone. Upstream explored a typing-history feature and
declined it for the reason quoted above. This change proposes a strictly
narrower alternative: a memory of **accepted-suggestion vocabulary and
user-saved snippets only** — never a keystroke log, never typed text,
never field content — sufficient to bias the prompt ("Written by Jane.
Recurring terms: Kubernetes, ArcLight, Q3 roadmap.") while keeping the
privacy surface reviewable.

## What Changes

- New `Cotabby.Core.Writing.WritingMemoryStore`: a bounded (default 200
  terms) in-memory store with **two, and only two, inputs**:
  1. **Accepted-suggestion vocabulary**: when the user accepts a
     suggestion (any funnel terminal state, `SuggestionCoordinator.cs`
     acceptance paths), the accepted *chunk's* distinctive terms (≥ 5
     chars, not stopwords) are counted — accepted suggestions are text the
     user chose to put in their document, which is materially different
     from raw keystroke capture.
  2. **User-saved terms**: explicit "Remember this term" affordance in the
     Writing pane.
- Derived prompt injection: top-N memory terms render into the existing
  persona/language preface (`BaseCompletionPromptRenderer`), clamped to a
  small token budget via the existing `CompletionPromptBudget` — memory
  can never crowd out caret context.
- Persistence: `%APPDATA%\Cotabby\writing-memory.json`, same
  best-effort/atomic-write pattern as `SettingsStore.Save`
  (`AppSettings.cs:167-175`). **Plaintext, local-only, off by default** —
  encryption is explicitly deferred (see privacy decision below).
- `AppSettings.EnableWritingMemory` (default `false`); Writing pane
  (`SettingsWindow.xaml.cs:617-798`) gains: enable toggle, term list with
  per-term delete, "Forget everything" (delete-all), and a plain-language
  description of exactly what is stored.
- Acceptances in credential fields contribute nothing (no suggestions
  exist there); the emoji/macro commit paths are excluded by design
  (their "acceptances" are glyphs, not vocabulary).
- The funnel (Phase 1) counts `MemoryTermsRecorded` — a count, never the
  terms themselves.

## Non-goals

- **No keystroke/typing history** — the declined #841 shape. No raw typed
  text, no prefixes, no field snapshots, ever.
- No cross-device sync, no cloud, no export by default (an explicit
  user-initiated "Export…" file dialog is a possible follow-up, not v1).
- No automatic encryption/key management in v1 (deferred to the owner
  decision; see risks).

## Proposal outline (proposal.md)

```markdown
## ADDED Requirements

### Requirement: Opt-in writing memory biases prompts from chosen text only

The app MAY, when the user explicitly enables writing memory, retain a
bounded local list of terms drawn only from suggestions the user accepted
and terms the user explicitly saved, and use it to bias the completion
prompt. The store MUST be local, bounded, and deletable in full. It MUST
NOT capture typed text, keystrokes, field content, or anything from
credential fields. It MUST be off until the user opts in.

#### Scenario: Accepted suggestion contributes vocabulary
- **WHEN** the user accepts a suggestion containing a distinctive term
- **THEN** the term is counted in the bounded memory

#### Scenario: Typed text is never captured
- **WHEN** the user types any text
- **THEN** nothing is written to the memory without an accept or explicit
  save

#### Scenario: Forget everything clears the store
- **WHEN** the user chooses "Forget everything"
- **THEN** the memory file is deleted and prompt biasing stops

#### Scenario: Feature off by default
- **WHEN** settings are absent or the toggle is off
- **THEN** no memory file is created and prompts are unchanged
```

## Source files touched (verified)

| File | Change |
| --- | --- |
| `src/Cotabby.Core/Writing/WritingMemoryStore.cs` | **new** — bounded store, two input paths |
| `src/Cotabby.Core/Writing/WritingMemoryTerms.cs` | **new** — pure term extraction (stopwords, length) |
| `src/Cotabby.Core/Support/BaseCompletionPromptRenderer.cs` | render memory terms into the preface (budget-clamped) |
| `src/Cotabby.Core/Suggestions/SuggestionCoordinator.cs:928-1055` | feed accepted chunks to the store (terminal accept states only) |
| `src/Cotabby.App/Hosting/AppHost.cs:133-169` | wire store, push `EnableWritingMemory` |
| `src/Cotabby.App/Settings/AppSettings.cs` | add `EnableWritingMemory` (default `false`) |
| `src/Cotabby.App/UI/SettingsWindow.xaml.cs:617-798` | Writing pane: toggle, list, per-term delete, Forget everything |
| `tests/Cotabby.Core.Tests/WritingMemoryStoreTests.cs` | **new** |
| `tests/Cotabby.Core.Tests/WritingMemoryTermsTests.cs` | **new** |

## Risks

- **The privacy decision is unresolved** (`UPSTREAM_PORTING_PLAN.md:37`):
  this change MUST NOT ship until the owner explicitly approves durable
  local text storage with the constraints below. Until then it is a
  proposal only — that gate is acceptance criterion #1.
- Accepting a suggestion is still user content: even narrowed, this is a
  durable text store. Mitigations: bounded (200 terms), stopword/length
  filtering, local plaintext, delete-all, off by default, and a UI that
  shows exactly what is stored.
- Prompt-bias quality: memory terms can mislead the model if noisy
  (frequency-ordered, capped at ~10 in the prompt) — measured via the
  funnel's acceptance rate before/after.

## Test plan (unit)

`tests/Cotabby.Core.Tests/WritingMemoryTermsTests.cs`:

- `StopwordsAndShortWords_AreExcluded`
- `DistinctiveTerms_AreExtracted`
- `EmojiOnlyChunk_YieldsNoTerms`
- `DuplicateTerms_IncrementCounts`

`tests/Cotabby.Core.Tests/WritingMemoryStoreTests.cs`:

- `AcceptedChunk_RecordsTerms`
- `TypedTextPath_DoesNotExist` (reflection: no public capture API)
- `ExplicitSave_RecordsTerm`
- `PerTermDelete_RemovesTerm`
- `ForgetEverything_ClearsAndDeletesFile`
- `BeyondMaximumTerms_LowestFrequencyEvicted`
- `DisabledStore_RecordsNothing`
- `CorruptMemoryFile_LoadsAsEmpty`
- `StoredTermsFeedPromptRenderer_WithinBudget` (composition test)

## Manual Windows smoke steps

1. **Gate**: do not run these steps until the owner privacy decision is
   recorded in this file and `UPSTREAM_PORTING_PLAN.md`.
2. Enable in the Writing pane; accept several suggestions in Notepad;
   open the pane and confirm only distinctive accepted terms are listed.
3. Confirm `%APPDATA%\Cotabby\writing-memory.json` contains terms and
   counts — and grep it for a sentence you typed but never accepted
   (expect absent).
4. Confirm suggestions visibly bias toward remembered terms in a fresh
   document.
5. "Forget everything" → file deleted, biasing stops immediately.
6. Disable → no writes; file remains until Forget-everything is used
   (documented behavior).

## Rollback

- `EnableWritingMemory=false` → store never constructed with a path;
  prompt rendering path unchanged (no memory section emitted).
- "Forget everything" deletes the on-disk store for users who enabled it
  during evaluation.
- Code revert removes the store, the coordinator feed, and the renderer
  section — the prompt pipeline returns to the v0.4.1 preface.

## Privacy & security constraints

- **Owner privacy decision is a hard prerequisite** (upstream #841 was
  declined pending exactly this; the decision must cover retention,
  delete-all, and whether plaintext local storage is acceptable).
- Capture surface is limited to *accepted suggestion chunks* +
  *explicitly saved terms*; no keystroke stream, no typed text, no field
  snapshots, no prefixes.
- Credential fields contribute nothing (no suggestions exist there); the
  store's only input is the acceptance terminal path.
- Local-only storage under `%APPDATA%\Cotabby\`; no encryption in v1
  (documented); no cloud calls, no telemetry, no export.
- The store is bounded and fully user-visible/deletable — the opposite of
  the silent durable-store shape #841 was declined for.
