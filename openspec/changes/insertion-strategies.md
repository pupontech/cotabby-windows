# Selectable insertion strategies

## Problem

The production inserter chooses Unicode or clipboard transport using a fixed
short-text heuristic. Users need a supported global transport choice without
weakening the existing target, credential or clipboard safety contract.

## Scope

Implement the supported part of phase-0 insertion strategies on the existing
single ITextInserter: pure Core selection, global Advanced setting, live wiring,
legacy-compatible auto behavior, forced Unicode/clipboard and Windows proof.

The older proposal's UIA requirement is explicitly deferred: TextPattern has no
text setter and ValuePattern.SetValue replaces a whole value, not a selection.
Do not invent a setter, replace full fields from stale snapshots or silently
fall back to clipboard under an explicitly requested unsupported UIA mode.

## Non-goals

No per-app strategy field, model change, native host automation adapter, UIA
whole-value replacement, clipboard sniffing/history, cloud, telemetry or durable
user-text storage. No merge of the separate per-app profile PR is implied.

## Acceptance criteria

1. Missing setting defaults auto and exactly preserves <=24 UTF-16 code units
   without LF -> Unicode; else clipboard. CR-only behavior is pinned unchanged.
2. Explicit Unicode never accesses clipboard; explicit clipboard uses the safe
   existing paste transaction for short text too. No cross-transport retry.
3. Snapshot strategy once per insertion before semaphore waits. Same inserter
   instance receives live settings; old constructor callers stay compatible.
4. Unknown settings fall back to auto, except explicit unsupported `uia` refuses
   with no clipboard/native write and an actionable content-free diagnostic.
5. Preserve credential/secure checks, exact target/PID/foreground revalidation,
   same-thread BlockInput/inject/unblock, clipboard ownership/restore and cleanup
   after irreversible dispatch. Partial dispatch never triggers retry.
6. TDD selector/settings cases, real Windows Advanced controls/persistence and
   production-inserter final-text/clipboard checks pass on final pushed SHA.
7. Independent review plus full Core/Win32/build/package/native proof and exact
   artifact manifest/hash verification precede test ZIP handoff.

## Rollback

Set InsertionStrategy to auto. Keep all existing profile/global safety settings.
See docs/tabtype-insertion-strategies.md for transport limits and owner checks.
