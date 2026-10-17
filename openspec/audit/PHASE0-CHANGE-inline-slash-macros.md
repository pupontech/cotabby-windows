# Inline slash macros

**Target phase:** 3 (after `insertion-strategies` provides the selectable
insert path). **Upstream provenance:** none (macros/snippets are not in
the upstream port). **Risk level:** Medium — intercepts typed characters.
**Flag:** `EnableSlashMacros` (default `false`).

## Why

Power users of ghost-text autocomplete expect text expansion: type
`/sig` and get a saved signature, `/addr` a mailing address, `/mtg` a
meeting blurb. The macOS port has none; Windows has none. The emoji picker
already proves the pattern on this codebase — a trigger state machine
(`EmojiTriggerStateMachine`), a popup (`EmojiPopupWindow`), commit via
backspace-eat + paste (`EmojiPickerController.cs:130-158`), all gated on
`IsCredentialField`/`IsSecure` (`:81-86`). Slash macros reuse that exact
architecture with user-defined snippets.

## What Changes

- New `Cotabby.Core.Suggestions.SlashMacroTriggerStateMachine`: mirrors
  `EmojiTriggerStateMachine` — `:`→`/` trigger, word chars accumulate the
  query, Escape/whitespace cancels, Tab/Enter/`/` commits the highlighted
  match. Pure, unit-testable.
- New `Cotabby.Core.Suggestions.SlashMacroCatalog`: matches a query against
  the user's macro list (name → snippet), ordered by exact-prefix match.
  Ships with a small built-in starter set (dates, signature placeholder)
  that the user can delete.
- New `Cotabby.App.SlashMacros.SlashMacroController`: observes the hook
  stream alongside the emoji controller (same wiring in `AppHost`/
  `App.OnStartup`), drives a new `SlashMacroPopupWindow` reusing the
  `EmojiPopupWindow` visual pattern, and commits via the existing
  `ITextInserter` with the same backspace-eat + insert sequence
  (`EmojiPickerController.cs:137-146`).
- Macros persist in `AppSettings.SlashMacros` (`List<SlashMacro>`:
  trigger + snippet) — **user-authored content only**; the app never
  captures or infers macros from typed text.
- `AppSettings.EnableSlashMacros` (default `false`); macro editor in the
  Writing pane (`SettingsWindow.xaml.cs:617-798`).
- While a macro popup is open, the suggestion coordinator is paused for
  that field (the two popups never compete); credential/secure fields
  never start a trigger (`EmojiPickerController.cs:81-86` precedent).

## Non-goals

- **No auto-capture of typed text into macros** — this is the line that
  keeps the feature out of typing-history territory: the user explicitly
  authors every snippet.
- No macro variables/placeholders in v1 (fixed snippets; `${cursor}`
  support is a follow-up if wanted).
- No cloud sync of macros.

## Proposal outline (proposal.md)

```markdown
## ADDED Requirements

### Requirement: User-defined slash macros expand typed triggers

The app MAY, when the user enables slash macros, offer a popup of
user-authored snippets matching a `/trigger` query and insert the chosen
snippet at the caret. Macros MUST be authored only by the user — the app
MUST NEVER derive or capture macros from typed text. Triggers MUST NOT
start in credential or secure fields, and expansion MUST go through the
existing validated insertion path.

#### Scenario: Trigger shows matching macros
- **WHEN** the user types `/sig` in a plain field
- **THEN** matching user-authored snippets are shown in a popup

#### Scenario: Commit inserts the snippet
- **WHEN** the user accepts a highlighted macro
- **THEN** the typed trigger is replaced by the snippet via the insertion
  path, with focus revalidated first

#### Scenario: Credential field never triggers
- **WHEN** the user types `/` in a sign-in field
- **THEN** no popup appears

#### Scenario: No auto-capture
- **WHEN** the user types any text
- **THEN** no macro is created or modified without explicit user action
```

## Source files touched (verified)

| File | Change |
| --- | --- |
| `src/Cotabby.Core/Suggestions/SlashMacroTriggerStateMachine.cs` | **new** (mirrors `src/Cotabby.Core/Emoji/EmojiTriggerStateMachine.cs`) |
| `src/Cotabby.Core/Suggestions/SlashMacroCatalog.cs` | **new** (mirrors `src/Cotabby.Core/Emoji/EmojiMatcher.cs`) |
| `src/Cotabby.App/SlashMacros/SlashMacroController.cs` | **new** (mirrors `src/Cotabby.App/Emoji/EmojiPickerController.cs`) |
| `src/Cotabby.App/SlashMacros/SlashMacroPopupWindow.xaml` (+ `.cs`) | **new** (mirrors `src/Cotabby.App/Emoji/EmojiPopupWindow.xaml`) |
| `src/Cotabby.App/Settings/AppSettings.cs` | add `EnableSlashMacros`, `SlashMacros` list |
| `src/Cotabby.App/Hosting/AppHost.cs` | register controller like `RegisterEmojiPicker` (`:50-53`) |
| `src/Cotabby.App/UI/SettingsWindow.xaml.cs:617-798` | Writing-pane macro editor |
| `tests/Cotabby.Core.Tests/SlashMacroTriggerStateMachineTests.cs` | **new** |
| `tests/Cotabby.Core.Tests/SlashMacroCatalogTests.cs` | **new** |

## Risks

- Trigger conflicts with real text (URLs `https://…`, paths `/usr/…`):
  the trigger only arms on `/` at a word start, and any non-word char
  (like `:` in a URL) cancels — pinned by state-machine tests.
- Two popups (emoji + macros) fighting: a single "popup coordinator"
  pause rule — whichever opens first suppresses the other until commit/
  cancel; both share the same credential/secure gate.
- Commit reliability: reuses the *shipped* emoji commit path (backspace
  count from the state machine, 40 ms settle, inserter ownership
  revalidation) — no new insertion mechanics.

## Test plan (unit)

`tests/Cotabby.Core.Tests/SlashMacroTriggerStateMachineTests.cs`:

- `SlashAtWordStart_ArmsTrigger`
- `SlashMidWord_DoesNotArm` (URLs, paths)
- `QueryAccumulatesWordChars`
- `EscapeOrWhitespace_Cancels`
- `TabCommitsHighlightedMacro`
- `EnterCommitsHighlightedMacro`
- `NoMatches_Cancels`
- `CommitReportsQueryLengthForBackspaceEat`

`tests/Cotabby.Core.Tests/SlashMacroCatalogTests.cs`:

- `ExactPrefixMatch_RanksFirst`
- `PartialMatch_IsIncluded`
- `NoMatch_ReturnsEmpty`
- `StarterMacros_AreDeletable`
- `UserMacroList_IsTheOnlySource` (no capture API exists — reflection)

## Manual Windows smoke steps

1. Enable; add macro `sig` → "Best regards, Jane". Type `/sig` in
   Notepad: popup appears, Tab commits, typed `/sig` is replaced.
2. Type `https://` in a browser field: no popup (trigger doesn't arm
   mid-token).
3. Type `/sig` in a password field: no popup (credential gate).
4. Commit with Enter and with mouse click: both insert correctly.
5. Confirm `settings.json` contains only the macros you authored;
   restart and confirm persistence.

## Rollback

- `EnableSlashMacros=false` → controller unsubscribes; zero behavior
  change.
- Removing all macros leaves an empty catalog; the feature is inert.
- Code revert removes the three new Core/App files; the emoji picker
  (which shares the pattern but not code) is untouched.

## Privacy & security constraints

- **No auto-capture**: the only persistence is the user-authored
  `SlashMacros` list — this is configuration, not captured typing. The
  catalog has no write API fed by the input stream (asserted by test).
- Credential/secure gate is inherited verbatim from the emoji controller
  pattern (`:81-86`) and re-checked at commit (`:135`).
- Insertion goes through the shipped, ownership-validated
  `ITextInserter`; no new synthetic-input code.
- No cloud calls; macros live in the local settings file.
