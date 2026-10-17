# Suggestion statistics funnel

**Target phase:** 1 (measurement substrate for Phase 2/3 tuning).
**Upstream provenance:** none. **Risk level:** Low. **Flag:**
`EnableSuggestionStatistics` (default `false`, opt-in like
`EnablePerformanceTracking`).

## Why

The coordinator already emits every funnel transition as log lines —
`Triggering suggestion` (`SuggestionCoordinator.cs:450`),
`Scheduled request` (`:495`), `Firing request` (`:505`),
`Overlay shown` (`:641`), `Request … finished` (`:668`), `Cancel session`
(`:1171`), and the acceptance-outcome event (`:121`) — but nothing
aggregates them. Today the only quantitative signal is the opt-in latency
log (`PerformanceMetricsStore`, bounded at 200 entries,
`PerformanceMetricsStore.cs:25`), which records only *latency*, never
whether a suggestion was **shown, accepted, or rejected**. Without a
funnel, the Phase 2 `confidence-gating` and `prediction-cache` changes and
the Phase 4 `benchmark-cli` have no local acceptance-rate ground truth to
tune or validate against, and the Performance pane cannot answer "are
suggestions actually being used?"

## What Changes

- New `Cotabby.Core.Suggestions.SuggestionStatisticsStore` — a bounded,
  count-only funnel mirroring the `PerformanceMetricsStore` pattern
  (opt-in flag, in-memory counters, JSON persistence under
  `%APPDATA%\Cotabby\statistics.json`, corrupt-file tolerance):
  `Triggered → Requested → EngineFired → Presented → AcceptedWord →
  AcceptedPhrase → AcceptedWhole → Exhausted → Cancelled`, each with a
  timestamp + model id (like `PerformanceEntry`, `PerformanceMetricsStore.cs:8-13`).
- Coordinator hook points (each an existing log line gains a parallel
  counter bump; no new IO on the hot path):
  - `HandleEvent` → `ShouldRequest` true: `Triggered`
    (`SuggestionCoordinator.cs:435`)
  - `TriggerGeneration`: `Requested` (`:479`), plus a `SuppressedCredential`
    count (`:460-465`)
  - work fires: `EngineFired` (`:509`)
  - overlay shown: `Presented` (`:638`)
  - `AcceptIntentAsync`: `AcceptedWord` / `AcceptedPhrase` / `AcceptedWhole`
    by `AcceptanceGranularity` and `whole` (`:928-932`),
    `InsertionRejected` on failure (`:951`)
  - `CancelSession`: `Cancelled` with the existing reason string (`:1171`)
- New `AppSettings.EnableSuggestionStatistics` (default `false`).
- Performance pane gains a read-only funnel summary
  (`SettingsWindow.xaml.cs:971-1093`, alongside the latency table) +
  "Clear statistics" button mirroring `OnClearPerformanceLog`
  (`:1007`).
- The funnel feeds the acceptance-rate column of `benchmark-cli`
  (Phase 4) by reading the same JSON.

## Non-goals

- No text, prefix, or field identity in any entry — counters and
  timestamps only (same contract as `PerformanceEntry`).
- No network/telemetry export.
- No change to existing log lines (funnel is additive).

## Proposal outline (proposal.md)

```markdown
## ADDED Requirements

### Requirement: An opt-in funnel records suggestion lifecycle counts

The app MUST offer an opt-in, count-only statistics store that records
when a suggestion is triggered, requested, fired, presented, accepted
(word/phrase/whole), exhausted, or cancelled. Entries MUST contain no
field text, request prefix, or field identity. Recording MUST be a no-op
while the toggle is off.

#### Scenario: Accepted suggestion increments the right counter
- **WHEN** the user accepts a word with Tab
- **THEN** `AcceptedWord` increments and `Presented` was previously counted

#### Scenario: Credential-field suppression is counted without payload
- **WHEN** a suggestion is suppressed on a sign-in field
- **THEN** `SuppressedCredential` increments and no text is stored

#### Scenario: Disabled statistics cost nothing
- **WHEN** the toggle is off
- **THEN** no entries are recorded and no file is written
```

## Source files touched (verified)

| File | Change |
| --- | --- |
| `src/Cotabby.Core/Suggestions/SuggestionStatisticsStore.cs` | **new** (mirrors `PerformanceMetricsStore.cs`) |
| `src/Cotabby.Core/Suggestions/SuggestionCoordinator.cs:435,450,460,479,505,509,638,668,928,951,1171` | counter hooks at existing transitions |
| `src/Cotabby.App/Hosting/AppHost.cs:86,114-116` | construct + wire store (same pattern as `PerformanceMetrics`) |
| `src/Cotabby.App/Settings/AppSettings.cs` | add `EnableSuggestionStatistics` (default `false`) |
| `src/Cotabby.App/UI/SettingsWindow.xaml.cs:971-1093` | Performance-pane funnel summary + Clear |
| `tests/Cotabby.Core.Tests/SuggestionStatisticsStoreTests.cs` | **new** |
| `tests/Cotabby.Core.Tests/SuggestionCoordinatorFunnelTests.cs` | **new** (coordinator wiring, fake engine/overlay) |

## Risks

- Double-counting on retried/re-anchored accepts (`AcceptIntentAsync`
  re-anchor path `:1000-1032`): funnel must count exactly once per
  committed intent — reuse the existing `AcceptanceIntentStatus` terminal
  states (`StateCommitted`/`InsertedNotCommitted`, `:1111-1116`) as the
  single completion point.
- Extra JSON write per event on the same hot path as the latency store:
  coalesce writes (same bounded-file strategy; write at most once per
  event, flush on idle like `PerformanceMetricsStore.Save`).
- Performance pane UI growth: keep the summary to one compact row.

## Test plan (unit)

`tests/Cotabby.Core.Tests/SuggestionStatisticsStoreTests.cs`:

- `RecordingWhileEnabled_AppendsEntry`
- `RecordingWhileDisabled_IsNoOp`
- `EntriesBeyondMaximum_AreTrimmed`
- `CorruptStatisticsFile_LoadsAsEmpty`
- `EntryContainsNoTextFields` (reflection over the entry record)

`tests/Cotabby.Core.Tests/SuggestionCoordinatorFunnelTests.cs` (fake
engine/inserter/overlay per existing `SuggestionWorkControllerTests`
pattern):

- `TypedCharacter_CountsTriggeredAndRequested`
- `CredentialField_CountsSuppressedCredentialOnly`
- `PresentedSuggestion_CountsPresentedOnce`
- `WordAccept_CountsAcceptedWord`
- `WholeAccept_CountsAcceptedWhole`
- `CancelledSession_CountsCancelled`
- `RejectedInsertion_DoesNotCountAcceptance`

## Manual Windows smoke steps

1. Enable in Performance pane; type in Notepad; accept with Tab; confirm
   the funnel row shows Triggered→Presented→AcceptedWord ≥ 1.
2. Type into a password field; confirm `SuppressedCredential` increments
   and nothing else changes.
3. Confirm `%APPDATA%\Cotabby\statistics.json` contains only
   timestamps/model/counts (grep for typed words — expect none).
4. Disable; confirm the file stops growing and the row shows "off".

## Rollback

- `EnableSuggestionStatistics=false` → recording no-ops; store never
  constructed with a path.
- Code revert removes the store and the counter hooks; the log lines the
  funnel mirrors are untouched.

## Privacy & security constraints

- Entries are timestamp + model id + enum counters — structurally
  incapable of holding typed text (asserted by test).
- Credential-field suppression is counted by flag only; no payload read.
- Local JSON only; no cloud calls, no telemetry.
- `IsCredentialField` gate unchanged; the funnel never bypasses it.
