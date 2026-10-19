# Selectable insertion strategies

## Problem

Users need a supported global transport choice without weakening target,
credential or clipboard safety. The forced-Unicode experiment failed actual
Windows wrong-field proof; a typed partial-dispatch outcome prevents replay but
cannot undo already redirected text.

## Supported scope

Implement Auto and Clipboard on the existing single ITextInserter: pure Core
selection, global Advanced setting, live wiring, exact legacy Auto behavior,
forced Clipboard and Windows proof. Do not expose forced Unicode or UIA writes.
Explicit stored `unicode` and `uia` requests refuse nonempty insertion before OS
writes; never silently fall back to clipboard or Auto for those requests.

Forced Unicode is deferred after paced native events reached a replacement field
in real Windows tests. A foreground HWND pin did not fix that failure. Any later
implementation requires reliable host acknowledgement/admission and new focus
transition proofs; elapsed delays and native event counts are not acknowledgements.
The older proposal's UIA setter is also deferred: TextPattern has no text setter
and ValuePattern.SetValue replaces a whole value, not a selection. Do not replace
whole fields from stale snapshots.

## Non-goals

No per-app strategy field, model change, native host automation adapter, Unicode
pacing path, UIA whole-value replacement, clipboard sniffing/history, cloud,
telemetry or durable user-text storage. No merge of the per-app profile PR,
version bump, tag or release is implied.

## Acceptance criteria

1. Missing setting defaults Auto and exactly preserves <=24 UTF-16 code units
   without LF -> one Unicode batch; else clipboard. CR-only behavior unchanged.
   This is compatibility, not universal host-retention certification.
2. Explicit Clipboard uses the existing paste transaction even for short text.
   No cross-transport retry. Forced Unicode/UIA are not selectable in Advanced.
3. Snapshot concrete strategy before semaphore waits; same inserter instance
   receives live settings and old constructor callers stay compatible.
4. Explicit known unsupported `unicode`/`uia`, including case variants, refuse
   without native/clipboard mutation; saved values persist on load/unrelated
   settings changes and produce an actionable warning. Other unknown historical
   strings fall back to Auto. Malformed typed strategy values preserve globals.
5. Preserve supported-path credential/secure checks, target/owner revalidation,
   same-thread BlockInput/inject/unblock for paste, clipboard ownership/restore
   and irreversible cleanup. Emoji admission must be live, fail closed when
   unwired, and run before host-key suppression and again before query deletion.
   Known partial Unicode native counts raise a typed
   indeterminate result; retire only the owning acceptance offer, never replay
   its full text or cancel a newer request.
6. TDD selector/settings regressions, actual Windows Advanced controls,
   production inserter supported-mode final-text/clipboard checks, and deferred
   Unicode zero-mutation checks pass. Keep both wrong-field regression fixtures
   armed; refusal must occur before the first text event, not by skipping them.
7. Fresh independent affected-scope review, full Core/Win32/build/package/native
   proof on final pushed SHA, and exact manifest/hash verification precede test
   ZIP handoff. Earlier unsafe experiment artifacts are not deliverables.

## Rollback

Select Auto explicitly. Preserve all profile/global safety settings. See
`docs/tabtype-insertion-strategies.md` for limits, deferrals and owner checks.
