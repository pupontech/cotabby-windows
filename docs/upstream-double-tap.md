# Double-tap acceptance: upstream #840 Windows adaptation

## Source and current state

Source: [FuJacob/cotabby #840, `3ad86f3ff0c7fa4abe250fde348611f162027650`](https://github.com/FuJacob/cotabby/commit/3ad86f3ff0c7fa4abe250fde348611f162027650).
Windows base: `c0a2522` (v0.3.0). Implementation checkpoint: `bf19e5f`.
This is an **unreleased, review-blocked implementation**, not accepted runtime parity.

The implementation note was written before coding in the scratch workspace. This
repository note carries its scope, rationale and current evidence forward; it does
not imply that all acceptance criteria below have passed.

## Intended behavior

Select **Settings → Shortcuts → Accept Entire Suggestion → Double-tap Tab**.
The first distinct Tab accepts the normal word or phrase immediately. A second
physical press within the inclusive 300 ms window accepts the remainder of the
same suggestion once. A key-up separates physical presses; it does not cancel a
pair. Repeats remain ordinary-unit accepts and never arm or complete a pair.
Intervening input, session replacement, cancellation, focus transition, settings
application and credential rejection retire pending state.

## Windows scope and deliberate deviations

- Global, fixed Tab gesture only. No per-app shortcut overrides or key recorder;
  existing process blocklisting is unchanged.
- One active full-accept slot uses the existing `AcceptSuggestionKey` string.
  `DoubleTap` replaces the previous choice; switching back requires selecting
  Backtick/Tilde/Backslash explicitly. It does not retain a dormant prior binding.
  Legacy/missing settings still default to Backtick; double-tap is opt-in.
- Stable `RequestId` replaces upstream's `(generation, fullText)` token. Mutating
  the visible tail must not change the identity of the current suggestion.
- Windows synthesizes autorepeat from Tab down/up transitions because the
  low-level hook has no repeat flag. Synthetic proof cannot establish behavior
  for real physical holds, lost key-up events or all host applications.
- The overlay's existing Tab chip still identifies the first-press key, not the
  double-tap gesture. Settings explains the second press. Welcome copy must not
  claim that Backtick is always the active full-accept binding.
- No Apple APIs, new model choices, inference changes, telemetry, typing-history
  collection or single-file packaging.

## Ownership and implementation

Core: `DoubleTapAcceptanceState`, `SuggestionCoordinator`, `KeyboardEvent` and
focused tests. Win32: `KeyboardHook` derives repeat metadata. App:
`AppSettings`, `AppHost` and `SettingsWindow` persist and apply the choice.
The final accept-time process guard rejects a live field whose process differs
from the suggestion owner before reading its text. Existing metadata-first
credential admission and all request/accept/emoji guards remain in force.
A process guard alone does not prove same-process field identity or key delivery.

## Acceptance matrix and evidence

| Criterion | Current evidence |
| --- | --- |
| Inclusive 300 ms, one-shot pair, mismatch and clock regression | Portable state tests pass (RED then GREEN) |
| Immediate first unit, second-tail promotion, phrase mode | Coordinator tests pass with a mutable host-text fixture |
| Repeat and intervening-key rejection | Portable tests pass; synthetic real-hook repeat case passes on Windows |
| Rapid queued Tabs keep both intents exactly once, and a physical gap wider than the window stays two units | Portable tests pass; synthetic real-hook case passes on Windows |
| Exact combined host text with a stale UIA snapshot | Portable test asserts the final host string; synthetic real-hook case passes on Windows |
| Failed, throwing, delayed and disposed insertion never advances, arms or inserts the tail | Portable tests pass; one outcome is reported per captured key |
| No insertion into a different process or a different control in the same process | Portable tests pass; synthetic real-hook cases pass on Windows |
| Legacy defaults, existing choices, DoubleTap round-trip and explicit switch back | Portable settings tests pass against the real settings source |
| Windows hook, UIA, packaged native layout and fixture text readback | Windows CI run 38552244230: 261 tests, 8/8 synthetic hook cases, credential UIA proof, native DLL load, 546-member byte-verified package |
| Real model suggestions, physical-key gestures, real host apps | Owner validation pending |

### Captured-key replay is deliberately refused

A Tab (or the full-accept key) is eaten only when an accept is expected to happen. If that accept is
then rejected - insertion refused, insertion threw, focus moved, field identity changed, the
coordinator was disposed - the key is NOT replayed into the application, so that press is lost. The
coordinator reports one outcome per captured key (`Committed`, `InsertionRejected`, `InsertionThrew`,
`Invalidated`, `Blocked`, `CoordinatorDisposed`, `DispatchFailed`) and never sprays a synthetic key at
an unknown target and never retries. A future replay path needs immutable raw key data, exact
modifier state, the original foreground window and a hook-owned tagged injection marker checked
immediately before dispatch; until that exists, refusal is the honest behaviour and must not be
advertised as lossless. Synthetic proof also cannot establish physical key auto-repeat.

## Verified local commands

Using .NET 9 SDK at the existing scratch toolchain:

- `dotnet test tests/Cotabby.Core.Tests/Cotabby.Core.Tests.csproj -c Release --no-restore`: **261 passed, 0 failed, 0 skipped**, independently rerun by the parent.
- Focused double-tap tests after the fix: **11 passed**, repeated three times by
  the writer. Before the fix, three tests hung in their manually queued test
  SynchronizationContext; uncaptured async continuations now allow the explicit
  queue pump to run. No tests were skipped or deleted.
- `dotnet build src/Cotabby.App/Cotabby.App.csproj -c Release -p:EnableWindowsTargeting=true`:
  writer log records **0 warnings, 0 errors**.
- Parent `git diff --check`: no diagnostics, exit 0.

Windows CI also runs the real keyboard hook and UI Automation against WPF fixtures: 8/8 cases pass, covering double tap, repeat, unowned Tab, cross-process and same-process focus change, delayed dispatch and stale snapshots. That proof uses a fixture-directed insertion adapter and synthetic keys, so it is not a claim about physical keys or the production SendInput inserter.

Linux unit tests and WPF cross-compilation are **not Windows execution proof**.
No actual Windows hook, physical key, live model load or constrained decode has
been verified for this port yet. No tag or release was created.

## Rollback and delivery gates

Revert the focused implementation commit to remove the optional choice, timing
state and repeat wiring while retaining existing Backtick/Tab behavior. Select
Backtick explicitly if rolling back a config that selected DoubleTap.

Before delivery: resolve exact-diff safety findings, add missing focused tests,
correct misleading UI copy, run all local gates on a quiescent tree, open a PR,
verify exact-head Windows CI including attributed real-hook proof, and verify
all downloaded ZIP manifest members and source provenance. Live Windows/model
acceptance stays explicit. Tags/releases require separate owner authorization.
