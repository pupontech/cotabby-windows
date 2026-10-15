# Battery-aware inference

**Target phase:** 1. **Upstream provenance:** none (Windows-specific; the
macOS port relies on App Nap/power assertions that have no WPF equivalent).
**Risk level:** Low-Medium. **Flag:** `BatteryAwareInference` (default
`false`) + `BatteryThresholdPercent` (default `40`).

## Why

Autocomplete inference on a laptop on battery is exactly the workload
users complain about: GPU/CPU decode every keystroke burst, draining the
battery for a convenience feature. Windows exposes battery state through
`SystemInformation.PowerStatus` (WPF) and WMI `Win32_Battery`; neither is
consulted today. The macOS port gets analogous behavior from the OS power
management integration; a Windows equivalent has to be explicit.

## What Changes

- New pure `Cotabby.Core.Support.BatteryPolicy`: given a power snapshot
  (`OnAcPower`, `BatteryChargePercent`, `BatteryChargeStatus`) and settings,
  return an `InferencePosture`:
  - `Normal` — current behavior;
  - `Throttled` — debounce doubles (50 ms → 100 ms default headroom per
    `AppSettings.DebounceMs` comment), `MaxTokens` clamped to the next
    lower `CompletionLengthPreset` tier;
  - `Deferred` — while charge < threshold and not charging, new
    suggestions are not requested until AC returns (in-flight requests
    finish; the funnel records the pause).
- New `Cotabby.App.Hosting.PowerStatusWatcher`: polls
  `SystemInformation.PowerStatus` (and falls back to WMI
  `Win32_Battery.BatteryStatus` when `PowerStatus` reports "unknown" —
  common on desktops with a UPS) on a slow timer (30 s) plus
  `SystemEvents.PowerModeChanged`, publishing a `PowerSnapshot` to the
  coordinator. Unavailable/unknown power state MUST resolve to `Normal`
  (best-effort, never an error — same contract as the GPU counters,
  `UPSTREAM_PORTING_PLAN.md:50-53`).
- `SuggestionCoordinator` accepts an optional `PowerSnapshot` provider;
  `TriggerGeneration` (`SuggestionCoordinator.cs:455`) consults
  `BatteryPolicy` before scheduling, and the debounce is applied via the
  existing `SuggestionWorkController` (`SuggestionWorkController.cs:32`).
- New `AppSettings.BatteryAwareInference` (default `false`),
  `BatteryThresholdPercent` (default `40`), surfaced in the Advanced pane.
- Tray icon tooltip / settings show the active posture
  ("Paused — on battery") so the behavior is never mysterious.

## Non-goals

- No model auto-switching (smaller model on battery) — model choice stays
  explicit in Engine & Model.
- No sleep/wake assertions (no `SetThreadExecutionState`); Cotabby never
  blocks sleep.
- No cloud or telemetry of any kind.

## Proposal outline (proposal.md)

```markdown
## ADDED Requirements

### Requirement: Inference posture adapts to battery state

The app MAY, when the user enables battery-aware inference, throttle or
defer suggestion requests while running on battery below the configured
threshold. Power state MUST be read locally; an unknown power state MUST
behave as AC power. In-flight generations MUST be allowed to finish.

#### Scenario: On battery below threshold with feature enabled
- **WHEN** the device is on battery below the threshold
- **THEN** new suggestions are deferred and the user is told why

#### Scenario: AC power restores normal posture
- **WHEN** the device returns to AC power
- **THEN** suggestions resume on the next keystroke

#### Scenario: Unknown power state behaves as AC
- **WHEN** the power state cannot be read
- **THEN** the posture is `Normal` and no error is surfaced
```

## Source files touched (verified)

| File | Change |
| --- | --- |
| `src/Cotabby.Core/Support/BatteryPolicy.cs` | **new** — pure posture decision |
| `src/Cotabby.App/Hosting/PowerStatusWatcher.cs` | **new** — `PowerStatus`/WMI polling (App project, `net9.0-windows`) |
| `src/Cotabby.Core/Suggestions/SuggestionCoordinator.cs:455-503` | consult policy before `TriggerGeneration` scheduling |
| `src/Cotabby.App/Hosting/AppHost.cs:133-169` | wire watcher, push settings |
| `src/Cotabby.App/Settings/AppSettings.cs` | add `BatteryAwareInference`, `BatteryThresholdPercent` |
| `src/Cotabby.App/UI/SettingsWindow.xaml.cs:932-970` | Advanced-pane controls |
| `tests/Cotabby.Core.Tests/BatteryPolicyTests.cs` | **new** |

## Risks

- Flapping at the threshold boundary (charge hovering at 40%): hysteresis
  — entering `Deferred` requires charge < threshold; exiting requires
  ≥ threshold + 5 or AC.
- WMI latency on the UI thread: watcher runs on a timer off the
  dispatcher; failures degrade to `Normal`.
- Desktop-with-no-battery machines: `PowerStatus.BatteryChargeStatus`
  `Unknown` → `Normal`, verified by test.
- Deferred suggestions feel like breakage if unexplained — tray/settings
  text is part of acceptance, not a nicety.

## Test plan (unit)

`tests/Cotabby.Core.Tests/BatteryPolicyTests.cs`:

- `OnAcPower_NormalPosture`
- `OnBatteryAboveThreshold_ThrottledPosture`
- `OnBatteryBelowThreshold_DeferredPosture`
- `UnknownPowerState_NormalPosture`
- `ThresholdHysteresis_PreventsFlapping`
- `ThrottledPosture_DoublesDebounce`
- `ThrottledPosture_ClampsMaxTokensDownOneTier`
- `FeatureDisabled_AlwaysNormal`
- `Charging_NormalPosture`

## Manual Windows smoke steps

1. Enable with a laptop unplugged below 40%: type in Notepad; confirm the
   tray/settings shows the paused posture and no ghost text appears.
2. Plug in: confirm suggestions resume on the next keystroke.
3. On a desktop (no battery): confirm nothing changes and no errors in
   the log.
4. Hover the threshold (40/41%): confirm no oscillation between postures.

## Rollback

- `BatteryAwareInference=false` → policy always returns `Normal`; watcher
  can be disposed.
- Code revert removes the watcher and the coordinator hook; the
  `BatteryPolicy` class has no other callers.

## Privacy & security constraints

- Power state is an OS signal; no user content involved.
- No typed-type persistence: deferral decisions are in-memory.
- Credential-field protections unchanged (policy runs after the
  `IsCredentialField` gate in `TriggerGeneration`, `:460`).
- No cloud calls: `SystemInformation.PowerStatus` and local WMI only.
