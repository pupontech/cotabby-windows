# TabType Phase 1 — diagnostics, field protection, battery posture, statistics

Four Phase 1 changes from the repository's own audit
(`openspec/audit/PHASE0-CHANGE-INDEX.md`), all reconciled against the shipped
Windows architecture. Each risky behavior ships **off by default** except the
diagnostics hardening, which only reduces what already gets written.

## 1. Bounded, sanitized diagnostics with live verbosity

- `C:\tmp\cotabby-live.log` is now size-bounded: it rotates to
  `cotabby-live.log.1 … .N` at `MaxLogBytes` (default 8 MiB) and keeps
  `MaxLogFiles` backups (default 3). Rotation is a rename, so an external
  `tail` must be reopened; the Advanced pane says so.
- Every file, console and debugger line passes through
  `Cotabby.Core.Support.SecretSanitizer` (provider keys, JWTs, bearer/basic
  headers, credentialed URLs, signed query parameters, secret-named
  assignments, Luhn-valid card numbers, private-key blocks). There is no
  raw-text escape hatch.
- A verbosity control (Information / Debug / Warning / Error) applies
  immediately to all three sinks; the default is Information, matching the
  v0.4.1 hardening floor.
- Fail-soft: an unwritable path, a refused rotation rename or a disposed
  provider disables the sink instead of throwing into inference, input or
  acceptance. A single record larger than the cap is replaced by
  `[oversized diagnostic suppressed]` so one log call cannot defeat the disk
  bound.
- `LogVerbosity`, `MaxLogBytes` and `MaxLogFiles` live in `settings.json`.
  Verbosity is live; size and retention are read at startup.

## 2. Extra field protection (opt-in, default off)

`AppSettings.SmarterFieldSuppression` in Settings → Apps:

- **Password-manager process defaults**: focus inside 1Password, KeePass,
  KeePassXC, Bitwarden, Enpass or Dashlane produces no suggestion. Exact
  process-name matching; additive to the user's blocked-app list.
- **Transient mixed-character pause**: in a plain single-line `Edit`, a
  trailing run of 8+ characters spanning three or more character classes
  suppresses suggestions until the next whitespace. It reads only the
  in-memory prefix, stores nothing, and releases on the first space.
- Extra credential labels: `passphrase`, `pass phrase`, `security answer`,
  `recovery code`.

The shipped `IsCredentialField` policy is unchanged and still evaluated
**before** any `TextPattern`/`ValuePattern` payload read. Known limits: the
pause can also delay suggestions over code, identifiers and generated
strings, and neither heuristic is a password detector — metadata coverage in
browsers and Electron apps still varies.

## 3. Battery-aware inference (opt-in, default off)

`AppSettings.BatteryAwareInference` + `BatteryThresholdPercent` (default 40)
in Settings → Advanced, with a live posture line and a refresh button:

| Posture | When | Effect |
| --- | --- | --- |
| `Normal` | AC, charging, unknown/no battery, or feature off | unchanged |
| `Throttled` | on battery at or above the threshold | debounce doubles; completion budget drops one preset tier |
| `Deferred` | on battery below the threshold | new requests are skipped until AC returns or charge recovers by 5 points |

In-flight generations are never canceled by a posture change. Power state is
read from the 12-byte `SYSTEM_POWER_STATUS` via `GetSystemPowerStatus`, cached
for two seconds; any unreadable or nonsensical state resolves to `Normal` and
never surfaces an error. The energy-saver pause from #812 keeps its own
independent toggle.

**Fixed while porting this:** the native struct previously declared the
energy-saver byte *after* `BatteryFullLifeTime`, so it was read at offset 12
of a 16-byte managed struct instead of offset 3 of the real 12-byte layout.
The marshaller filled only the first 12 bytes, so the extra byte was always
zero and the Low Power Mode pause could never engage. `BatteryPolicyTests`
now pins the size and offset.

## 4. Suggestion statistics (opt-in, default off)

`AppSettings.EnableSuggestionStatistics` in Settings → Performance records a
count-only funnel: triggered, requested, engine fired, presented, accepted
word / phrase / whole, exhausted, cancelled, suppressed (credential, battery)
and insertion rejected. Refresh and Clear buttons mirror the latency table.

Counters are in-memory and per-session: no text, no field or app identity, no
model id, no timestamps, no file and no telemetry. This is a deliberate
narrowing of the audit proposal, which suggested a JSON file — a persistent
file buys nothing for tuning and would be the first durable store of user
activity. "Accepted" means the inserter reported success; it is not
independent proof that the host application kept the text.

## Verification

- `dotnet test tests/Cotabby.Core.Tests` — 418 tests, 0 failed.
- Windows-targeted builds of `Cotabby.App` and the regression smoke tool —
  0 warnings, 0 errors.
- `tools/Cotabby.Tools.RegressionSmoke` drives the real Settings window and
  asserts every new control persists and reaches the coordinator.
- `tests/Cotabby.Win32.Tests` now runs in the Windows workflow, so the field
  identity suite executes instead of being skipped.
- Owner live testing still required: physical keys, real model output, a real
  battery/energy-saver transition, and browser credential-metadata recall.
