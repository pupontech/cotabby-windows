# Low Power Mode pause: upstream #812 Windows adaptation

## Source and scope

Source: [FuJacob/cotabby #812, `d4c4d23410dc8a720c30725ae63451dad598f525`](https://github.com/FuJacob/cotabby/commit/d4c4d23410dc8a720c30725ae63451dad598f525).
Windows implementation is based on v0.4.1, retaining the existing ports.
Merged into main as PR #6 and shipped in v0.5.0.

## Behavior

A new opt-in **Pause in Low Power Mode** toggle lives in Settings → General
(Behavior card, below Inline Emoji Picker), matching upstream's engine-pane
toggle in value and default (off). When enabled, new suggestion requests pause
while the OS energy-saver state is engaged; either half alone (setting on but
saver off, or saver on but setting off) keeps suggestions flowing.

## Windows deviation: poll instead of notification

Upstream subscribes to macOS's power-state notification. Windows reads the
same semantic state via `GetSystemPowerStatus` (`SYSTEM_POWER_STATUS.
SystemStatusFlag`, non-zero while battery saver is engaged), wrapped in
`Cotabby.App/Power/LowPowerModeMonitor` behind the pure
`ILowPowerModeProvider` contract. The reader keeps one cached answer per
2-second window (pure CPU guard — the coordinator already re-evaluates on
every keystroke) and a failed read keeps the previous answer rather than
stopping suggestions. The gate sits in the coordinator before request
triggering, after the focused-field lookup.

## Verification

- 3 new unit tests (`LowPowerModeGateTests.cs`) pin the two-halves rule and
  the provider contract with a fake provider.
- Windows CI run 38748405213 (main) green at the merge commit.
- Real engagement of Windows battery saver (its UI, its effect on
  `GetSystemPowerStatus` on the owner's machine) remains owner-verified; CI
  cannot toggle the OS state.
