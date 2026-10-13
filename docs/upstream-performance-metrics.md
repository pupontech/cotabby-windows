# Performance metrics: Windows GPU readouts and request latency

Upstream: https://github.com/FuJacob/cotabby/commit/8cdbea2d2619b0f89a73d46eb0bb856504d07343
(#858, "Show GPU usage in the Performance pane")

## Scope and differences

This is a partial adaptation, not complete macOS Performance-pane parity. Windows
adds GPU readouts and a local opt-in latency table. CPU/process-RAM readouts,
sparklines and the upstream rolling resource history are not included.

The Windows adapter reads the `GPU Engine` and `GPU Adapter Memory` performance
counter categories. Utilization requires consecutive raw samples: the first
sample is unknown, not a measured zero. GPU fields are independent and optional.
Missing categories, unsupported drivers, permissions or unreadable counters must
not stop the application. No administrator repair command is required to use
Cotabby.

The machine utilization indicator groups process contributions by adapter and
physical engine, then selects the busiest engine instead of summing parallel
engines. Cotabby's GPU share is a Windows cumulative-engine-time estimate; it is
not an exact Task Manager per-process reading. GPU memory is the sum of dedicated
and shared usage across readable adapters, not Cotabby-only memory or VRAM on the
model's selected adapter. The UI labels this all-adapter quantity explicitly.
Actual hardware behavior and comparisons with Task Manager remain owner-gated.

Tracking is off by default. The Windows log records one entry for a successfully
finished generation whose suggestion was shown; canceled, empty and never-shown
requests do not enter the table. It stores only timestamp, model identity and
elapsed generation milliseconds, never prompt or suggestion text. Windows keeps
the newest 200 entries; upstream keeps 100 and records at the engine router
rather than requiring presentation. These are intentional Windows differences.

## Files and responsibilities

- `src/Cotabby.Core/Metrics/`: counter identity parsing, aggregation, sampling
  state, process-share arithmetic and production formatting helpers.
- `src/Cotabby.App/Metrics/WindowsGpuUsageProvider.cs`: best-effort Windows raw
  counter adapter; sampling state is retained across successive reads.
- `src/Cotabby.Core/Suggestions/PerformanceMetricsStore.cs`: bounded local log at
  `%APPDATA%\Cotabby\performance.json`, opt-in recording and atomic best-effort
  persistence. Persistence is synchronous and can incur disk latency; it is not
  claimed to be nonblocking.
- `src/Cotabby.Core/Suggestions/SuggestionCoordinator.cs`: request-scoped model
  attribution and presentation/completion recording gate.
- `src/Cotabby.App/Hosting/AppHost.cs` and `Settings/AppSettings.cs`: composition,
  additive `EnablePerformanceTracking` setting and model identity wiring.
- `src/Cotabby.App/UI/SettingsWindow.*`: Performance navigation, GPU readouts,
  tracking toggle, newest-first request table and Clear action.
- `tests/Cotabby.Core.Tests/`: portable arithmetic/store/coordinator regressions
  and supplementary markup wiring checks. Markup checks do not prove rendering.
- `.github/workflows/windows-ci.yml`: exact-head build and test ZIP, feature
  notes, package manifest/hash checks and extracted CPU-native DLL loading.

## Acceptance checks

1. Parser preserves adapter/physical-engine identity; malformed and nonfinite
   readings are ignored. Copy and driver-specific compute engines are not dropped.
2. Parallel engines do not sum to a fabricated machine 100%; contributions on the
   same physical engine are combined before choosing the busiest engine.
3. No-baseline or reset/shrinking cumulative counters show unknown. Leaving the
   pane resets sampling before a later visit.
4. Memory remains readable even without engine data; a measured zero is shown as
   zero. A missing value is a dash.
5. Tracking respects opt-out, retains 200 entries and uses the request's model.
   Queued presentations rejected by cancellation must not create rows.
6. Concurrent Record/Clear persistence does not publish torn or stale JSON. Invalid
   persisted records cannot crash Settings; failed file operations remain local.
7. Labels and values occupy separate layout columns. README and the test ZIP must
   describe only implemented metrics, with no full-parity or live-proof claim.

## Windows owner test

Extract the complete CI ZIP into a **new folder** and run `Cotabby.exe` as a
standard user. Keep the complete `runtimes/win-x64` layout beside it. Existing
settings and model caches are reused.

- Open Settings > Performance and wait for at least two samples. Compare machine
  GPU activity against Task Manager during a workload and a real Cotabby request.
  Check integrated/discrete adapters when available; unknown data must stay a dash.
- Enable tracking and generate a real suggestion. Confirm one model/latency row,
  disable tracking and confirm later requests add no rows.
- Clear the table, restart and confirm the cleared state and toggle persist.
- Leave Performance and close Settings; sampling should stop. Check responsiveness
  while the pane is selected. Minimized-window behavior is not yet live-verified.
- Recheck small/large-model autocomplete from the prior output-budget port.

Local Linux tests and cross-builds prove portable logic and compilation only.
Windows CI proves tests, folder packaging and CPU-native loading without a model.
Neither proves WPF rendering, physical GPU accuracy or real model suggestions.
These stay pending until the owner tests this build. Do not share raw
`C:\tmp\cotabby-live.log`: it can contain typed text and local paths.

Rollback is a revert of the focused feature. The settings addition needs no
migration. This feature may be delivered as a draft PR and CI test ZIP; merge,
tag and release require new owner acceptance.
