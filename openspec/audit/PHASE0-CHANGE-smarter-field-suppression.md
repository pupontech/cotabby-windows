# Smarter field suppression

**Target phase:** 1. **Upstream provenance:** extends #848 /
[`d81d304`](https://github.com/FuJacob/cotabby/commit/d81d304f1ea0a5b4bb5feff9b71407ebee56b13c)
(sign-in and verification-field protection). **Risk level:** Medium —
suppression recall/precision changes directly affect the shipped privacy
guarantee; every change must keep the metadata-before-payload contract.
**Flag:** `SmarterFieldSuppression` (default `false`; enables the new
heuristics while the shipped #848 logic remains the fallback).

## Why

`CredentialFieldDetector` (`src/Cotabby.Core/Focus/CredentialFieldDetector.cs`)
decides `IsCredentialField` from metadata — password flag, whole-word
accessible labels (`LabelKeywords` `:6-12`, negated by `ProseLabelWords`
`:14-18`), bounded automation identifiers (`IdentifierNamesCredential`
`:61`, vocabulary split with `CredentialIdentifierWords`/`QualifierIdentifierWords`)
— plus an address-shape fallback (`LooksLikeAddressEntry` `:95`). The
README parity table already records the known gap: "browser metadata
coverage varies, credential recognition is metadata-based and is not
complete DOM/autofill parity" (`README.md:159`). Real misses cluster in
three places: (1) Chromium/Electron custom widgets expose generic
automation ids while the credential hint lives in an ARIA label the
current label sources don't always reach; (2) password managers
(1Password, KeePass, Bitwarden, browser autofill dropdowns) expose
neutral single-line edits whose only signal is the owning process;
(3) mid-typing a credential into a *neutral* field (a user typing a
password into a mislabeled box) is indistinguishable today.

## What Changes

- **Process-level defaults**: a built-in suppression set of
  password-manager process names (`1password`, `keepass`, `keepassxc`,
  `bitwarden`, `enpass`, `dashlane`) treated like `BlockedApps` for
  *suggestion purposes only* — additive to the user's own blocklist, not
  replacing it. Applied in `SuggestionAvailability.ShouldRequest`
  (`SuggestionAvailability.cs:41-46`) alongside the existing check, and
  shown in the Apps pane as a non-editable "protected by default" section.
- **Entropy back-off for neutral fields**: while typing in a plain
  single-line field, a bounded high-entropy heuristic (length ≥ 8, ≥ 3
  character classes, no dictionary hit) pauses suggestions for that field
  until a whitespace or low-entropy character arrives. This is a *pause*,
  never a read of stored text, and it only exists to avoid surfacing a
  completion over a half-typed password in a mislabeled field. The
  heuristic never persists anything and never fires for credential-flagged
  fields (already suppressed).
- **Label-source widening for the UIA adapter**: `UiaFocusTracker`
  (`src/Cotabby.Win32/Focus/UiaFocusTracker.cs`) additionally consults
  `HelpText` and the `LabeledBy` chain when building the label list passed
  to `CredentialFieldDetector.IsCredentialField` — the CHANGELOG already
  names these sources (`CHANGELOG.md:38-41`); this makes them
  unconditional rather than best-effort.
- **Vocabulary additions**: extend `LabelKeywords` with observed
  browser/ARIA variants (`"pass phrase"`, `"passphrase"`, `"security
  answer"`, `"recovery code"`, `"new pin"`, `"confirm password"` is
  already covered via qualifier split — verify by test) — every addition
  ships with a regression test proving neutral prose fields stay eligible.
- All new decisions still run **before** any text/value read; the
  entropy heuristic is the only post-read signal and applies solely to
  plain fields, on in-memory text, with zero persistence.
- New `AppSettings.SmarterFieldSuppression` (default `false`) gating the
  process-defaults + entropy heuristic; label-source widening ships
  always-on (it can only increase recall of the existing policy).

## Non-goals

- No DOM scraping, no browser-extension integration, no network calls.
- No change to `IsSecure` (password-flag) handling — it stays the
  strongest signal.
- No per-site allow/deny; process-level only.
- The entropy heuristic never stores, hashes, or transmits field text.

## Proposal outline (proposal.md)

```markdown
## MODIFIED Requirements

### Requirement: Sign-in, verification and payment fields are never completed

(Existing #848 requirement, unchanged in policy: the decision MUST be made
from field metadata before reading text/value into a snapshot, and the
same gate MUST apply to every caller.) This change widens the metadata
sources (unconditional HelpText/LabeledBy), adds a built-in
password-manager process suppression set, and adds a bounded,
non-persisting entropy back-off for mislabeled neutral fields.

#### Scenario: Password-manager window is suppressed
- **WHEN** focus enters a `1password` window
- **THEN** no suggestion is requested for it

#### Scenario: High-entropy run in a neutral field pauses suggestions
- **WHEN** a neutral single-line field contains a high-entropy run
- **THEN** suggestions pause until whitespace or a low-entropy character

#### Scenario: Prose field is not suppressed by entropy back-off
- **WHEN** the user types ordinary prose
- **THEN** suggestions continue unchanged

#### Scenario: Decision still precedes payload read
- **WHEN** any new suppression rule fires
- **THEN** no field text was read into the snapshot before the decision
```

## Source files touched (verified)

| File | Change |
| --- | --- |
| `src/Cotabby.Core/Focus/CredentialFieldDetector.cs:6-25` | vocabulary additions |
| `src/Cotabby.Core/Suggestions/SuggestionAvailability.cs:23-50` | password-manager set + entropy back-off hook |
| `src/Cotabby.Core/Support/FieldEntropyHeuristic.cs` | **new** — pure, bounded entropy pause decision |
| `src/Cotabby.Win32/Focus/UiaFocusTracker.cs` | unconditional HelpText/LabeledBy label sources |
| `src/Cotabby.App/Settings/AppSettings.cs` | add `SmarterFieldSuppression` (default `false`) |
| `src/Cotabby.App/UI/SettingsWindow.xaml.cs:854-932` (Apps pane) | show protected-by-default processes |
| `tests/Cotabby.Core.Tests/CredentialFieldDetectorTests.cs` | extend (vocabulary, prose negatives) |
| `tests/Cotabby.Core.Tests/FieldEntropyHeuristicTests.cs` | **new** |
| `tests/Cotabby.Core.Tests/SuggestionAvailabilityTests.cs` | extend (password-manager set) |

## Risks

- **False positives** suppress suggestions in legitimate fields (the
  #848 lesson: over-suppression is a UX bug, under-suppression is a
  privacy bug — here we bias toward suppression, but the entropy
  heuristic must be conservative and self-releasing). Mitigation: flag
  defaults off; heuristic only pauses, resumes on whitespace; funnel
  (`suggestion-statistics-funnel`) measures any drop in `Presented`.
- **False negatives** (the #848 gap) can never be fully closed by
  metadata — documented, not claimed.
- Entropy heuristic fires on passphrase-style *prose* fields the user
  wants completed in (e.g., writing about crypto keys): mitigation —
  resume on the first space, and the heuristic only ever *delays*, never
  disables.

## Test plan (unit)

`tests/Cotabby.Core.Tests/FieldEntropyHeuristicTests.cs`:

- `ShortOrSingleClassRun_DoesNotPause`
- `HighEntropyRun_PausesSuggestions`
- `WhitespaceAfterRun_ResumesSuggestions`
- `OrdinaryProse_NeverPauses`
- `CredentialFlaggedField_NotPassedToHeuristic` (gate upstream)

`CredentialFieldDetectorTests.cs` additions (existing file):

- `PassphraseLabel_IsCredential`
- `SecurityAnswerLabel_IsCredential`
- `ProseSubjectLine_IsNotCredential` (regression: prose negatives hold)
- `HelpTextSource_CredentialLabelDetected`

`SuggestionAvailabilityTests.cs` additions:

- `PasswordManagerProcess_NoRequest`
- `PasswordManagerProcessWithFlagOff_Requests` (flag gate)

## Manual Windows smoke steps

1. Enable `SmarterFieldSuppression`; focus a 1Password window (or any
   listed manager): confirm no ghost text appears (Advanced → log shows
   the suppression reason).
2. In Notepad type a random high-entropy string (`Xk9#mQ2$vLp7`): confirm
   ghost text pauses mid-run and resumes after a space.
3. Type normal prose in Notepad: suggestions unaffected.
4. Focus a sign-in page field in Chrome/Edge with a visible
   "Email"/"Password" label: confirm no suggestion (metadata recall
   unchanged-or-better); note any miss in the audit follow-up (known
   browser-metadata variance).
5. Confirm `%APPDATA%\Cotabby\settings.json` round-trips the flag and
   statistics funnel `Presented` count did not drop for prose apps.

## Rollback

- `SmarterFieldSuppression=false` → password-manager set and entropy
  back-off disable; #848 logic (labels/ids/address-shape) continues
  unchanged. The always-on label-source widening is a recall improvement
  to the existing policy and has no off switch — it is covered by the
  existing #848 test suite.

## Privacy & security constraints

- The entropy heuristic is the only new post-read signal; it operates on
  in-memory text of **plain** fields only, persists nothing, hashes
  nothing, and pauses rather than reads further.
- `IsCredentialField` remains the single policy flag; `AGENTS.md:30-38`
  ("decide from field metadata … before reading text/value") is preserved
  — the widening adds metadata *sources*, it does not move the decision
  after the payload read.
- No cloud calls; process-name matching is local.
- Password-manager suppression is deny-by-default and cannot be
  user-removed (it is additive to `BlockedApps`, not a user-editable
  list).
