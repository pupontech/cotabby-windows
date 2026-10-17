# Insertion strategies

**Target phase:** 2. **Upstream provenance:** none (Windows-specific;
the macOS port inserts via AX/CGEvent). **Risk level:** Medium — the
insertion path is the most safety-critical code in the repo (clipboard
preservation, STA threading, irreversible dispatch). **Flag:**
`InsertionStrategy` (default `"auto"` = today's exact heuristic).

## Why

`SendInputTextInserter` (`src/Cotabby.Win32/Insertion/SendInputTextInserter.cs`)
picks one of two strategies by a hardcoded threshold: text ≤ 24 chars
without newlines goes as a single `SendInput` unicode batch
(`MaxSendInputChars` `:52`); everything else goes through the
clipboard-paste + `BlockInput` + restore path (`:88-149`). That heuristic
is right most of the time, but some hosts drop unicode input under load
(the file's own remarks `:18-26` document Notepad/Electron history) and
some users/password managers (clipboard watchers, remote desktop,
VM consoles) object to clipboard touch at all. The README's stack table
also names "UIA `TextPattern`" as the insertion technology
(`README.md:72`) — a third strategy that exists in the architecture
description but not in the code. Users need a supported way to pin the
strategy per their environment.

## What Changes

- New pure `Cotabby.Core.Insertion.InsertionStrategySelector`: given text
  length/newline/target field metadata + chosen strategy
  (`auto`|`unicode`|`clipboard`|`uia`), pick the concrete path. `auto`
  reproduces today's rule exactly (≤24 chars & no newline → unicode; else
  clipboard) so default behavior is byte-identical.
- `SendInputTextInserter` consults the selector instead of the inline
  threshold, and gains a **UIA `TextPattern`/`ValuePattern`** path: when
  the focused element exposes `TextPattern` (or `ValuePattern` for
  single-line), insert by setting the pattern range/value directly — no
  clipboard, no synthetic input. Falls back to the selector's next
  strategy when the pattern is unavailable (refuse-and-return-false, never
  a partial write).
- The UIA path reuses the existing ownership gate: `TargetStillFocused`
  (`:153-168`) runs before every strategy, and `IsCredentialField`/
  `IsSecure` refuse first (`:155`).
- New `AppSettings.InsertionStrategy` (default `"auto"`), surfaced in the
  Advanced pane with per-strategy explanation.
- `AppHost.cs:88-90` DI stays a single `ITextInserter`; the strategy is a
  constructor setting, not a second implementation (keeps the composition
  root unchanged).

## Non-goals

- No clipboard-content sniffing, no clipboard history features.
- No per-app strategy selection (that composes with `per-app-profiles`
  later if wanted; not now).
- No change to the acceptance/insertion safety contract: target
  revalidation, `BlockInput` bracketing for clipboard paste, and
  irreversible-dispatch cleanup all remain exactly as shipped.

## Proposal outline (proposal.md)

```markdown
## ADDED Requirements

### Requirement: The insertion strategy is selectable

The app MUST let the user choose between automatic selection, unicode
SendInput, clipboard paste, and UI Automation TextPattern insertion. The
automatic default MUST reproduce the shipped heuristic exactly. Every
strategy MUST revalidate the focused target before writing and MUST
refuse credential/secure fields.

#### Scenario: Short text uses unicode batch under auto
- **WHEN** an accepted word of ≤24 characters is inserted
- **THEN** the unicode batch path is used, matching prior releases

#### Scenario: Long text uses clipboard paste under auto
- **WHEN** a long completion is inserted
- **THEN** the clipboard paste path with BlockInput bracketing is used

#### Scenario: UIA TextPattern insertion avoids the clipboard
- **WHEN** the strategy is `uia` and the target exposes TextPattern
- **THEN** the text is written through the pattern and the clipboard is
  never modified

#### Scenario: UIA path unavailable falls back safely
- **WHEN** the strategy is `uia` and the target exposes no text pattern
- **THEN** insertion is refused (returned false) without partial writes
```

## Source files touched (verified)

| File | Change |
| --- | --- |
| `src/Cotabby.Core/Insertion/InsertionStrategySelector.cs` | **new** — pure selection logic |
| `src/Cotabby.Win32/Insertion/SendInputTextInserter.cs:52-86` | consult selector; add UIA pattern path |
| `src/Cotabby.App/Settings/AppSettings.cs` | add `InsertionStrategy` (default `"auto"`) |
| `src/Cotabby.App/Hosting/AppHost.cs:88-90,133-169` | pass strategy into the inserter |
| `src/Cotabby.App/UI/SettingsWindow.xaml.cs:932-970` | Advanced-pane strategy picker |
| `tests/Cotabby.Core.Tests/InsertionStrategySelectorTests.cs` | **new** |

## Risks

- **Highest-risk area in the repo**: the inserter's STA-thread contract
  (`:176-188`), clipboard materialization/restore (`:93-148`), and
  dispatched-event-count handling (`:220`, CHANGELOG 0.4.1) must be
  preserved verbatim; the UIA path is additive and must fail closed
  (return false) rather than degrade silently.
- UIA `TextPattern.SetValue` on some hosts triggers undo-stack pollution
  or IME issues: fail-closed fallback + owner smoke test required; the
  `uia` strategy is opt-in precisely because of this.
- `auto` must stay byte-identical — selector tests pin the exact boundary
  (24 chars, newline) so a future tweak cannot silently flip defaults.

## Test plan (unit)

`tests/Cotabby.Core.Tests/InsertionStrategySelectorTests.cs`:

- `AutoShortSingleLine_SelectsUnicode`
- `AutoLongText_SelectsClipboard`
- `AutoTextWithNewline_SelectsClipboard`
- `AutoAtTwentyFourCharBoundary_SelectsUnicode` (pins shipped behavior)
- `ExplicitUnicode_SelectsUnicodeRegardlessOfLength`
- `ExplicitClipboard_SelectsClipboardForShortText`
- `ExplicitUia_SelectsUia`
- `UnknownStrategyString_FallsBackToAuto`

Integration coverage for the inserter itself lives in the existing
Windows CI synthetic-UIA proof (`windows-ci.yml`); the new UIA path adds a
synthetic `TextPattern` element case to that suite (CI-only, no unit
test can construct a real UIA element).

## Manual Windows smoke steps

1. Default (`auto`): accept a word in Notepad (unicode path — clipboard
  untouched) and a long completion (clipboard path — clipboard restored);
  confirm identical behavior to v0.4.1.
2. Strategy `unicode`: force a long completion (raise completion length);
  confirm it still inserts (or fails cleanly) — documents host limits.
3. Strategy `uia` on Notepad (exposes ValuePattern): confirm insertion
  with the clipboard never modified (copy a marker string first, verify
  after).
4. Strategy `uia` on a host without TextPattern (e.g., a game/console
  window): confirm refusal, no partial text, and a warning in the log.
5. Credential field check: no strategy ever inserts into a sign-in field
  (existing CI credential proof extended with the new path).

## Rollback

- `InsertionStrategy="auto"` restores the shipped heuristic; the UIA path
  is unreachable under `auto`.
- Code revert of the selector + inserter restores the hardcoded threshold;
  the selector has no other callers.

## Privacy & security constraints

- Every strategy runs the existing `TargetStillFocused` ownership gate and
  the credential/secure refusal **before** any write
  (`SendInputTextInserter.cs:153-168`); the UIA path inherits it.
- The UIA path is the *least* privileged option (no clipboard touch, no
  synthetic input) — it strengthens, never weakens, the privacy posture.
- No typed-type persistence; clipboard save/restore semantics unchanged.
- No cloud calls; local Win32/UIA only.
