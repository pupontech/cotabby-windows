# Performance-metrics verification record

## Local evidence

SDK 9.0.318 on Linux. The preserved continuation baseline passed 139 tests.
The integrated candidate passes **172 tests, zero failures and zero skipped**.
App and Windows smoke-tool cross-builds pass with zero warnings/errors; the
self-contained win-x64 folder publish retains the LLamaSharp native layout.
`git diff --check`, actionlint 1.7.12 and actual PowerShell workflow parsing pass.
These are local results, not Windows runtime proof.

## Independent review and corrections

Requested models were live-smoked and their recorded session identities checked:
`gpt-6-luna` via Codex and `deepseek-v4-flash` via the official DeepSeek endpoint.
Six initial read-only reviewers covered GPU counters, latency/concurrency,
WPF integration, upstream/spec differences, tests and Windows packaging.
Two Luna writers and one DeepSeek writer worked in independent source copies;
only their explicitly owned source files were integrated. The parent reran
focused tests and all integrated gates after writers exited.

Verified corrections include:

- Retain consecutive raw GPU counter samples instead of discarding rate-counter
  baselines; preserve adapter/physical-engine identity and choose the busiest
  engine for the machine indicator.
- Keep zero memory distinct from unknown; read memory independently from engines.
- Reject malformed/nonfinite data and retain driver-specific engine types.
- Preserve per-instance process-counter identity so one reset cannot be hidden
  by growth on another engine. Parent regression failed with numeric zero before
  the guard, then passed with unknown as required.
- Serialize local-log persistence, use unique temporary paths, contain invalid
  records and bound input size. Close the temp stream before replacing its file
  (Windows sharing semantics differ from Linux).
- Record after confirmed presentation/completion and capture request model
  identity; cancellation and stale finalizers cannot attribute another request.
- Move counter reads off the UI thread; test single-flight and stop/reentry
  ordering with deterministic deferred readers. Refresh request rows each tick.
- Separate labels/readouts in XAML. The supplemental markup regression failed
  on missing value columns before the layout fix, then passed.
- Narrow unsupported CPU/RAM parity claims and include current feature notes in
  the CI ZIP. No telemetry or cloud inference was added.

An independent final Luna review found a queued presentation surviving
coordinator disposal. The parent reproduced it (expected zero overlay shows,
actual one), added synchronous identity invalidation/disposed guards and owned
cleanup, then reran the focused suite: three tests passed. The independent
reviewer rechecked the actual corrected source and passed it.

The final DeepSeek review reported no concrete security/logic blockers. Its
report-file write hit a tool cap; the full terminal verdict and actual source
inspection were retained rather than claiming a report file existed.
Nonblocking observations: local-log persistence remains synchronous and can
incur disk latency; Task Manager/physical-GPU equivalence is not certified.

## Hosted Windows and owner gates

The PR workflow must pass against the exact feature SHA before the test ZIP is
handed over. It runs the full suite, checks source provenance, hashes every
package member and loads the extracted CPU-native DLL without a model.
The new Windows smoke tool exercises a synthetic paired native counter
calculation, the open-temp rename negative control, production log
persist/reopen/Clear and best-effort adapter reads. A GPU-less runner can return
unknown; none of these checks certifies physical-GPU accuracy.

Owner acceptance remains required for WPF layout/responsiveness, real GPU
readings, minimize/restore behavior, tracking with real suggestions and existing
small/large-model autocomplete. Deliver only a draft PR/CI test ZIP. Do not merge,
tag or release this feature before owner acceptance.
