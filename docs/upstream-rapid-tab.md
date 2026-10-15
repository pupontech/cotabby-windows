# Held-presentation rapid-Tab acceptance: upstream #838 Windows adaptation

## Source and scope

Source: [FuJacob/cotabby #838, `7724926b3e93f14e3b576ba46ff52c4f64d9712d`](https://github.com/FuJacob/cotabby/commit/7724926b3e93f14e3b576ba46ff52c4f64d9712d).
Windows implementation is based on v0.4.1, retaining the existing ports.
Merged into main as PR #6 and shipped in v0.5.0.

## Behavior

The macOS controller can hold a presentation off-screen (pixel caret read in
flight, or a host caret lagging published text) while `overlayState` still
describes the *previous* presentation. After a Tab accept, that stale state
named the pre-accept tail, so a rapid follow-up Tab compared the new tail
against the old one, failed, tore the session down, and passed the Tab into
the host — moving focus to the page's next control.

#838 adds the controller's held text (`heldPresentationText`) as an
authorization source: the held offer is Cotabby's own committed-to-paint
content for this exact session, so it authorizes acceptance just as a painted
ghost does. Any other mismatch still rejects: a visible ghost that is neither
the tail nor its pending replacement is stale UI.

## Windows adaptation: coordinator presentation mark

The WPF presenter marshals `Show`/`Update` across the dispatcher internally
but returns synchronically, so the Windows port carries the equivalent state
in the coordinator (`_heldPresentationText`) instead of in the presenter:

- Set immediately before every overlay `Show`/`Update` that reflects a
  replaced tail, and before the post-accept re-anchored present.
- Cleared on session cancel/hide, on acceptance commit paths that reflect the
  new tail, and never left across sessions (the acceptance check binds it to
  the intent's request id and requires the published snapshot to end with the
  held text and the held text to cover the accepted tail).

The acceptance path consults the mark before refusing: a rapid second Tab
inside the in-flight window now accepts the tail instead of being refused and
leaked. A ghost that is neither the tail nor the held offer still refuses and
still passes the Tab through, unchanged from v0.4.1.

## Verification

- Two new rig-based tests (`RapidTabAcceptanceTests.cs`) drive the real
  double-tap rig end to end: the rapid pair commits exactly one head and one
  whole remainder, and a Tab with nothing remaining is not captured.
- Windows CI run 38744191971 (PR head) and 38748405213 (main) green at the
  merge commit; the rapid-Tab tests passed on every run.
- Live behavior on a real browser (rapid double-Tab on a real suggestion with
  physical keys) remains owner-verified.
