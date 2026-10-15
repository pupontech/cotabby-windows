# Double-tap acceptance: upstream #840 Windows adaptation

## Source and scope

Source: [FuJacob/cotabby #840, `3ad86f3ff0c7fa4abe250fde348611f162027650`](https://github.com/FuJacob/cotabby/commit/3ad86f3ff0c7fa4abe250fde348611f162027650).
Windows implementation is based on v0.3.0, retaining the existing output-budget,
Performance pane and credential-protection ports. This feature is part of the
v0.4.1 release preparation; the earlier double-tap test ZIP is superseded by the
new tagged package once published.

## Use

Select **Settings > Shortcuts > Accept Entire Suggestion > Double-tap Tab**.
The first distinct Tab accepts the next word or phrase immediately. A second
press within the inclusive 300 ms window accepts the remainder of the same
suggestion once. Key-up separates presses. Autorepeat accepts ordinary units,
resets pending state, and never arms or completes a double tap. Shift+Tab remains
host navigation. Intervening input, replacement/cancellation, focus changes,
settings application and credential rejection retire pending state.

The default remains Backtick. DoubleTap replaces the single full-accept slot;
switching back requires choosing Backtick, Tilde or Backslash explicitly. Legacy
settings retain Backtick. There are no per-app bindings or typing-history storage.
The overlay Tab chip still identifies the first-press key, not the whole gesture.

## Safety contract

Acceptance is serialized and tied to request, process and field identity. A
confirmed insertion advances the known prefix before the next queued accept,
including when UIA's text snapshot lags. The coordinator rejects a refreshed
credential or changed target and never inserts replacement-request text.

The production inserter also revalidates the actual focused UIA element and its
foreground process after its own asynchronous waits, immediately before dispatch.
Long paste operations perform input blocking, injection and unblocking on a single
STA thread, with no await inside the blocked region. Clipboard formats are copied
before replacement and restored only while Cotabby still owns the clipboard
sequence. If a format cannot be copied, long insertion is refused without changing
the clipboard. Cancellation before dispatch refuses the operation; cancellation
after dispatch does not undo a sent paste. A successful SendInput call establishes
OS dispatch, not acknowledgement by every host application.

### Rejected captured keys are not replayed

A captured Tab or full-accept key can be lost if focus changes, insertion is
rejected, or the coordinator shuts down. It is deliberately **not replayed** into
an unknown application. Each captured key has one terminal outcome, and refusals
produce a content-free warning in the application log even with no diagnostic
event subscriber. No field text, field identity, request prefix or key character
is included in that warning. Typed-character codes are no longer recorded by key
diagnostics. Older versions did record them, including in credential fields:
delete the previous `C:\tmp\cotabby-live.log` when upgrading. Other diagnostic
entries still contain model output and local paths; do not share raw logs.

A future lossless replay contract needs immutable raw key/modifier data, original
foreground target verification and hook-owned tagged injection. This release does
not claim that contract or universal atomic focus/insertion guarantees.

## Verification evidence

Windows verification [run 38732360964](https://github.com/pupontech/cotabby-windows/actions/runs/38732360964)
on `39031e0a72a68be5a17ba132e1f5e5933d8c9c7e` passed:

- 275 unit tests, no failures/skips.
- Actual rendered Settings dropdown: Double-tap Tab visible, selectable and saved.
- Production inserter: short Unicode and long clipboard chunks read back exactly
  from a real WPF host, stale-target rejection, text and custom clipboard-format
  preservation.
- Nine synthetic real-hook/UIA fixture cases: double tap, repeat, unowned Tab,
  cross-process/same-process focus changes, default Backtick, delayed dispatch,
  stale snapshots and Shift+Tab pass-through. The fixture adapter is separate from
  the production inserter proof and does not establish physical-key behavior.
- Real Windows UIA credential guards, GPU counter/persistence smoke, complete
  package manifest verification and native CPU DLL loading without a model.

Regressions were first observed failing on Windows: stale-target injection in
run 38731061978, lost long paste in run 38731445887, and clipboard-format loss in
run 38731877706. The portable refusal-log test also failed before its fix.
Subsequent release commits must pass the same gates at their own exact SHA.

## Owner-live checks and rollback

Extract the whole ZIP into a new folder and run Cotabby.exe as a standard user.
Existing settings and model cache are reused. Enable Double-tap Tab, check a
quick pair, a slow pair, a held Tab, Shift+Tab, typing between accepts, phrase mode,
and focus changes in the applications you use. Verify short/long suggestions with
an actual cached/downloaded model. Physical keys, live model decoding, browser
metadata variability and GPU accuracy remain owner-live checks, not CI claims.
Select Backtick in Settings to turn the optional gesture off.
