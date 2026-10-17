# Alternatives popup

**Target phase:** 4 (consumes `beam-search` candidates).
**Upstream provenance:** none. **Risk level:** Medium (new interactive
surface over the hook stream). **Flag:** `ShowAlternativesPopup` (default
`false`) + `AlternativesTriggerKey` (default `Ctrl+Space`).

## Why

The ghost overlay (`IOverlayPresenter`, `GhostOverlayWindow`) presents
exactly one completion. When that completion is wrong — wrong tone, wrong
continuation — the user's only move is to keep typing and hope the next
decode is better. The emoji picker already proves a caret-anchored
selection popup on this codebase (`EmojiPopupWindow`, `EmojiPickerController`);
beam search (Phase 4) supplies the candidates. The popup closes the loop:
see alternatives, pick one, insert it through the same validated path.

## What Changes

- New pure `Cotabby.Core.Suggestions.AlternativesSelectionState`: owns the
  candidate list, highlighted index, and key handling (Up/Down move,
  Enter/Tab/click commits, Escape cancels, digits 1–9 jump) — mirrors
  `EmojiTriggerStateMachine` testability.
- New `Cotabby.Core.Overlay.IAlternativesPresenter` (Core contract,
  WPF-implemented): `Show(candidates, anchor)`, `UpdateSelection(index)`,
  `Hide()` — deliberately separate from `IOverlayPresenter` so the ghost
  overlay contract stays untouched.
- New `Cotabby.App.Alternatives.AlternativesPopupWindow.xaml` (+ code
  behind): reuses `EmojiPopupWindow`'s borderless/topmost/click-through
  visual pattern.
- New `Cotabby.App.Alternatives.AlternativesController`: on the trigger
  key with an active session, requests candidates via the widened
  `ISuggestionEngine.GenerateCandidatesAsync` (beam search), shows the
  popup anchored at the live caret, and commits the selection through
  `ITextInserter` with the same ownership revalidation as the acceptance
  path (`SuggestionCoordinator.AcceptIntentAsync` `:886-902`).
- Ghost overlay hides while the popup is open (one surface at a time);
  the suggestion session is paused, not cancelled, so dismissing the
  popup restores the ghost text.
- Credential/secure fields: trigger never arms (same gate as the emoji
  controller, `EmojiPickerController.cs:81-86`); commit re-checks
  `IsCredentialField` before insertion.
- `AppSettings.ShowAlternativesPopup` (default `false`),
  `AlternativesTriggerKey` (default `"Ctrl+Space"`), surfaced in
  Shortcuts (`SettingsWindow.xaml.cs:799-853`).
- Funnel (Phase 1): `AlternativesOpened`, `AlternativeSelected`,
  `AlternativesDismissed` counters — counts only.

## Non-goals

- No editing candidates in the popup (pick-or-dismiss only).
- No candidate persistence (popup content is ephemeral decode output).
- No mouse-capture games: click-through except on the popup's own list.

## Proposal outline (proposal.md)

```markdown
## ADDED Requirements

### Requirement: An opt-in popup offers alternative suggestions

The app MAY, when the user enables the alternatives popup, show ranked
candidate completions anchored at the caret on a trigger key, and insert
the selected candidate through the validated insertion path. The popup
MUST NOT open in credential or secure fields, MUST revalidate focus
before commit, and MUST restore the ghost suggestion when dismissed.

#### Scenario: Trigger shows candidates
- **WHEN** the user presses the trigger key with a suggestion visible
- **THEN** ranked candidates appear anchored at the caret

#### Scenario: Selection inserts via the validated path
- **WHEN** the user picks a candidate
- **THEN** it is inserted after focus revalidation, exactly like an
  accepted suggestion

#### Scenario: Dismiss restores ghost text
- **WHEN** the user presses Escape
- **THEN** the popup closes and the previous ghost text returns

#### Scenario: Credential field never opens the popup
- **WHEN** the trigger key is pressed in a sign-in field
- **THEN** nothing appears
```

## Source files touched (verified)

| File | Change |
| --- | --- |
| `src/Cotabby.Core/Suggestions/AlternativesSelectionState.cs` | **new** — pure selection state machine |
| `src/Cotabby.Core/Overlay/IAlternativesPresenter.cs` | **new** — Core contract |
| `src/Cotabby.App/Alternatives/AlternativesPopupWindow.xaml` (+ `.cs`) | **new** (pattern: `src/Cotabby.App/Emoji/EmojiPopupWindow.xaml`) |
| `src/Cotabby.App/Alternatives/AlternativesController.cs` | **new** (pattern: `src/Cotabby.App/Emoji/EmojiPickerController.cs`) |
| `src/Cotabby.App/Hosting/AppHost.cs` | register presenter/controller (pattern: `RegisterEmojiPicker` `:50-53`) |
| `src/Cotabby.Core/Suggestions/ISuggestionEngine.cs` | consume `GenerateCandidatesAsync` (widened by `beam-search`) |
| `src/Cotabby.App/Settings/AppSettings.cs` | add `ShowAlternativesPopup`, `AlternativesTriggerKey` |
| `src/Cotabby.App/UI/SettingsWindow.xaml.cs:799-853` | Shortcuts-pane controls |
| `tests/Cotabby.Core.Tests/AlternativesSelectionStateTests.cs` | **new** |

## Risks

- Popup steals focus / interferes with the hook: the emoji popup already
  solves this (click-through, `ShowActivated=false` pattern); reuse
  verbatim, never focus the popup window.
- Candidate latency: popup waits on a beam decode; reuse the coordinator
  watchdog budget and show a "…" state past 300 ms.
- Two surfaces fighting (ghost + popup): strict one-surface rule with the
  session paused (not cancelled) while open.

## Test plan (unit)

`tests/Cotabby.Core.Tests/AlternativesSelectionStateTests.cs`:

- `OpenWithCandidates_SelectsFirst`
- `DownAndUp_MoveSelectionAndClamp`
- `DigitKey_JumpsToIndex`
- `EnterCommitsSelected`
- `TabCommitsSelected`
- `EscapeDismisses`
- `EmptyCandidates_DoesNotOpen`
- `DismissReportsNoSelection` (for the funnel counter)
- `SelectionIndexSurvivesListShrink`

Controller-level wiring (CI synthetic-UIA suite, not unit — popup is
`Cotabby.App`): synthetic hook + UIA proof that the popup opens on the
trigger key in a plain field and never in a credential field, mirroring
the existing emoji/credential CI proofs.

## Manual Windows smoke steps

1. Enable; set beam width 2+ (previous change); type in Notepad; press
   Ctrl+Space: candidates appear at the caret.
2. Arrow keys move the highlight; Enter inserts the selection; the typed
   prefix is untouched and focus revalidation passes (no insertion into a
   switched window).
3. Escape: popup closes, ghost text returns.
4. Trigger in a password field: nothing opens.
5. With width 1 (single candidate): popup shows one row (or stays
   hidden — document the chosen behavior) and commit still works.
6. Performance pane funnel: `AlternativesOpened`/`AlternativeSelected`
   counts move with no text stored.

## Rollback

- `ShowAlternativesPopup=false` → controller unsubscribes; ghost overlay
  behaves exactly as v0.4.1.
- Code revert removes the popup surface and controller;
  `GenerateCandidatesAsync` remains (used by the benchmark CLI) — the
  beam decode stays available for measurement even with the popup off.

## Privacy & security constraints

- Candidates are ephemeral decoded suggestions; nothing persisted; the
  popup adds no new capture surface.
- Credential/secure gate at trigger and at commit (`IsCredentialField`
  re-checked before any insertion — same contract as acceptance).
- Commit goes through the shipped `ITextInserter` with full ownership
  revalidation (`:886-902` pattern); no synthetic-input code of its own.
- No cloud calls; local decode + local UI only.
