# Tab-acceptance retention

**Target phase:** 2. **Upstream provenance:** extends #840 /
[`3ad86f3`](https://github.com/FuJacob/cotabby/commit/3ad86f3ff0c7fa4abe250fde348611f162027650)
(double-tap Tab acceptance). **Risk level:** Medium — touches the
acceptance state machine and the "don't lose user keystrokes" contract.
**Flag:** `RetainSuggestionAfterAccept` (default `false`).

## Why

Today a Tab word-accept advances the session and re-anchors the remainder
(`AcceptIntentAsync` `:1035-1055`, `RefreshAfterAcceptAsync` `:1142-1164`),
but the *remainder's survival* depends on a 60 ms refresh round-trip
landing while focus is unchanged. Any hiccup — a UIA focus flicker, a
slightly slow refresh, a single non-matching keystroke between the two
Tabs — cancels the session (`CancelSession` `:1166`) and the user must
retype to regenerate. The double-tap feature (#840) already needs the
pending-pair state to survive this window (`DoubleTapAcceptanceState`
`:21-25`); retention generalizes it: after a Tab accept, keep the
remaining suggestion and the double-tap arming alive for a short,
bounded dwell so consecutive Tab presses chain reliably, and let the user
configure strictness.

## What Changes

- `DoubleTapAcceptanceState` gains a **dwell window**: after
  `RecordWordAccept` (`:21`), the pending pair survives one non-Tab
  keystroke and up to `AcceptanceRetentionMs` (default 800 ms) of
  wall-clock, instead of being reset by any intervening key
  (`SuggestionCoordinator.cs:352-355`, `:386-389`). A keystroke that
  *conflicts* with the remaining suggestion (a mismatch character) still
  ends retention — matching user intent.
- `SuggestionCoordinator.AcceptIntentAsync` re-anchor path: when the
  refresh round-trip loses the field for less than the dwell window, the
  session is retained and re-anchored on recovery instead of cancelled
  (`:986-1055` — the existing `IsSameAcceptanceBase` failure path already
  re-anchors; this widens the *session-null* case).
- New pure `Cotabby.Core.Suggestions.AcceptanceRetentionPolicy`: decides
  retain/reset given (pending state, elapsed, intervening key, key
  conflicts with remainder). Testable without the coordinator.
- New `AppSettings.RetainSuggestionAfterAccept` (default `false`) and
  `AcceptanceRetentionMs` (default `800`); surfaced in Shortcuts next to
  the double-tap choice (`SettingsWindow.xaml.cs:799-853`).
- With the flag off, behavior is byte-identical to v0.4.1 (every
  intervening key resets — the shipped #840 semantics).

## Non-goals

- No indefinite retention (bounded dwell only; a stale ghost text that
  outlives the user's context is a bug).
- No retention across focus changes to a *different* field/process (the
  existing ownership gates `:886-902` stay absolute).
- No change to the "rejected captured keys are not replayed" contract
  (`CHANGELOG.md:28`) — retention governs suggestion state, never key
  replay.

## Proposal outline (proposal.md)

```markdown
## MODIFIED Requirements

### Requirement: Tab acceptance chains across the accept round-trip

(Existing #840 double-tap requirement unchanged.) When retention is
enabled, the pending suggestion and double-tap arming MUST survive the
post-accept refresh round-trip and one non-conflicting keystroke for up to
the configured dwell. Retention MUST NOT extend across a different field,
process, or a keystroke that conflicts with the remaining text, and MUST
NOT replay captured keys.

#### Scenario: Second Tab within dwell accepts the remainder
- **WHEN** the user Tabs twice with retention on, refresh hiccups once
- **THEN** the second Tab still accepts the remainder

#### Scenario: Conflicting keystroke ends retention
- **WHEN** the user types a character not matching the remainder
- **THEN** the suggestion is cancelled as before

#### Scenario: Retention off preserves shipped behavior
- **WHEN** retention is disabled
- **THEN** any intervening keystroke resets the pair (v0.4.1 behavior)
```

## Source files touched (verified)

| File | Change |
| --- | --- |
| `src/Cotabby.Core/Suggestions/AcceptanceRetentionPolicy.cs` | **new** — pure retain/reset decision |
| `src/Cotabby.Core/Suggestions/DoubleTapAcceptanceState.cs:21-47` | dwell + non-conflicting-keystroke survival |
| `src/Cotabby.Core/Suggestions/SuggestionCoordinator.cs:352-364,386-389,986-1055` | retention hooks on the reset/accept paths |
| `src/Cotabby.App/Settings/AppSettings.cs` | add `RetainSuggestionAfterAccept`, `AcceptanceRetentionMs` |
| `src/Cotabby.App/Hosting/AppHost.cs:143-146` | wire retention into `DoubleTapFullAcceptanceEnabled` area |
| `src/Cotabby.App/UI/SettingsWindow.xaml.cs:799-853` | Shortcuts-pane controls |
| `tests/Cotabby.Core.Tests/AcceptanceRetentionPolicyTests.cs` | **new** |
| `tests/Cotabby.Core.Tests/DoubleTapAcceptanceStateTests.cs` | extend (dwell cases) |
| `tests/Cotabby.Core.Tests/SuggestionCoordinatorDoubleTapTests.cs` | extend (re-anchor retention) |

## Risks

- Ghost text lingering after the user moved on: dwell is bounded (800 ms
  default) and conflict-ending; the overlay still hides on any real focus
  change (`OnFocusChanged` `:267-274`).
- Clock-regression edge (already handled for double-tap,
  `DoubleTapAcceptanceState.cs:39-40`): retention reuses the same
  monotonic uptime provider (`SuggestionCoordinator.cs:109-110`).
- Autorepeat must not arm retention (`:906-915` keeps the shipped rule:
  autorepeat clears the pending pair).

## Test plan (unit)

`tests/Cotabby.Core.Tests/AcceptanceRetentionPolicyTests.cs`:

- `NonConflictingKeyWithinDwell_Retains`
- `NonConflictingKeyAfterDwell_Resets`
- `ConflictingKey_ResetsImmediately`
- `DifferentProcessOrField_ResetsImmediately`
- `Autorepeat_Resets`
- `RetentionDisabled_AnyKeyResets`
- `ClockRegression_DoesNotRetain`

`DoubleTapAcceptanceStateTests.cs` additions:

- `PendingPairSurvivesNonConflictingKeyWithinDwell`
- `PendingPairDiesAfterDwell`

`SuggestionCoordinatorDoubleTapTests.cs` additions:

- `SessionNulledBrieflyDuringRefresh_IsRetainedAndReAnchored`
- `FocusMovedToOtherProcess_IsNotRetained`

## Manual Windows smoke steps

1. Enable retention; in Notepad, Tab-accept one word, immediately Tab
  again → remainder inserts (even if a stray keystroke lands between).
2. Tab-accept, type a mismatching character → suggestion disappears
  (conflict ends retention).
3. Tab-accept, wait > 1 s, Tab again → no stale insertion (dwell expired).
4. Disable → behavior identical to v0.4.1 (second Tab without
  double-tap mode does nothing).
5. Focus another window mid-retention → no insertion into the new window
  (ownership gates hold).

## Rollback

- `RetainSuggestionAfterAccept=false` → `AcceptanceRetentionPolicy`
  always returns reset; shipped #840 semantics restored.
- `DoubleTapFullAcceptanceEnabled`/`AcceptanceMode` settings untouched —
  the double-tap feature itself keeps working as shipped.

## Privacy & security constraints

- Retention keeps *suggestion* text in memory for ≤ 800 ms longer; it
  never persists anything to disk and never extends to credential fields
  (sessions there are never created — `TriggerGeneration` `:460`).
- Ownership revalidation before every insertion is unchanged
  (`AcceptIntentAsync` `:886-902`): retention cannot direct text into a
  window the user moved to.
- Captured-key non-replay contract preserved (`CompleteAcceptanceIntent`
  `:1106-1128`): retention does not synthesize replays.
- No cloud calls.
