# Log rotation and verbosity control

**Target phase:** 1 (after `secret-sanitizer`, which must sanitize the sink
before rotation is layered on). **Upstream provenance:** none.
**Risk level:** Low. **Flag:** `LogVerbosity` — the working copy already
carries an *uncommitted* hardening batch that moved the log floor from
Debug to Information (`FileLoggerProvider.cs:55`, `AppHost.cs:61`); this
change formalizes that floor as a user setting and adds rotation on top.

## Why

`FileLoggerProvider` is append-only with **no rotation** (its own doc
comment says so) and a hardcoded verbosity gate. As shipped in the last
commit (`f5c6dbd`) that gate was `IsEnabled => logLevel >= LogLevel.Debug`
(`:55`); the current working copy carries an uncommitted hardening batch
that (a) wraps the writer creation in try/catch so an unwritable path
disables the sink instead of crashing startup, and (b) moves the floor to
`LogLevel.Information` in both the provider and `AppHost.cs:59-72`. Either
way, the level is a compile-time constant: on a long-running machine the
log grows without bound, and users have no supported way to raise
verbosity back to Debug for a deep-dive session — the file exists
specifically so diagnostics land at `C:\tmp\cotabby-live.log` for the
owner's verification workflow (`AGENTS.md:53-55`). Unbounded growth
eventually eats disk and slows every `WriteLine` (each one flushes
synchronously under a lock).

## What Changes

- New pure `Cotabby.Core.Support.LogRotationPolicy`: given a current file
  size and a max-bytes setting, decide `KeepWriting` vs `Rotate`; given a
  verbosity string, map to a minimum `LogLevel`. No IO in the policy.
- `FileLoggerProvider` gains:
  - size check per `N` writes (not per line) against `MaxLogBytes`
    (default 8 MB) → rename to `cotabby-live.log.1`, rolling `.1`→`.2`→…
    capped at `MaxLogFiles` (default 3), oldest deleted;
  - verbosity-driven `IsEnabled` from settings instead of the Debug+ gate;
  - rotation failures are swallowed (best-effort; a failed rotate must
    never crash the app or drop a line).
- `AppHost.cs:59-72` passes `Settings.LogVerbosity` (and max sizes) into
  the provider at construction.
- New `AppSettings.LogVerbosity` (`"debug"` default — current behavior;
  `"information"`, `"warning"`, `"error"` lower it) surfaced in the
  Advanced pane (`SettingsWindow.xaml.cs:932-970`) next to the existing
  "Open log" button.
- The sanitizer from `secret-sanitizer` (Phase 1, prior change) runs on
  every line before write; rotated `.1`/`.2` files inherit the same
  protection.

## Non-goals

- No change to the log path (`C:\tmp\cotabby-live.log`) — the owner's
  verification workflow greps that exact file.
- No async/batched flushing (the current synchronous-flush contract is
  load-bearing for crash diagnostics).
- No structured logging migration.

## Proposal outline (proposal.md)

```markdown
## ADDED Requirements

### Requirement: The local log is size-bounded and verbosity is user-selectable

The file logger MUST rotate the log when it exceeds the configured maximum
size, keeping at most the configured number of rolled files, and MUST
suppress lines below the configured verbosity. Rotation failures MUST NOT
prevent logging or crash the app.

#### Scenario: Log exceeding max size is rotated
- **WHEN** the log file exceeds `MaxLogBytes`
- **THEN** it is renamed to `cotabby-live.log.1`, older files shift, and a
  fresh `cotabby-live.log` is opened

#### Scenario: Verbosity filters lines
- **WHEN** verbosity is `warning` and the app logs an information line
- **THEN** the line is not written

#### Scenario: Default settings preserve current diagnostics
- **WHEN** settings are absent or legacy
- **THEN** verbosity is `debug` and the path is unchanged
```

## Source files touched (verified)

| File | Change |
| --- | --- |
| `src/Cotabby.Core/Support/LogRotationPolicy.cs` | **new** — pure size/verbosity decisions |
| `src/Cotabby.App/Hosting/FileLoggerProvider.cs:25-68` | rotation + verbosity + sanitized writes |
| `src/Cotabby.App/Hosting/AppHost.cs:59-72` | pass settings into the provider |
| `src/Cotabby.App/Settings/AppSettings.cs` | add `LogVerbosity`, `MaxLogBytes`, `MaxLogFiles` |
| `src/Cotabby.App/UI/SettingsWindow.xaml.cs:932-970` | Advanced-pane controls |
| `tests/Cotabby.Core.Tests/LogRotationPolicyTests.cs` | **new** |

## Risks

- Rotation during an active diagnosis splits the evidence file the owner
  greps; mitigated by the generous 8 MB default (the current CI-era logs
  are far smaller) and rolling `.1`/`.2` retention.
- Changing the default verbosity would silently reduce diagnostic fidelity
  for the verification workflow — hence default `debug` (no behavior change
  on upgrade).
- Rotation interacts with `FileShare.Read` tail tooling; the rolled file is
  a rename, not a copy, so open tails must be reopened — document in the
  Advanced pane tooltip.

## Test plan (unit)

`tests/Cotabby.Core.Tests/LogRotationPolicyTests.cs`:

- `SizeBelowMax_KeepWriting`
- `SizeAboveMax_Rotates`
- `RotationRolls_OldestDeletedBeyondMaxFiles`
- `VerbosityDebug_EnablesAllLevels`
- `VerbosityWarning_SuppressesInformationAndDebug`
- `VerbosityError_SuppressesWarningAndBelow`
- `UnknownVerbosityString_FallsBackToDebug`
- `RotationCheckedEveryNWrites_NotPerLine` (counter behavior)

File-level rotation mechanics (rename/delete) are exercised by a
temp-path integration test in the same file using the real provider with
an injected tiny max size — the provider itself stays in `Cotabby.App` and
is instantiated with a temp directory so no test touches
`C:\tmp\cotabby-live.log`.

## Manual Windows smoke steps

1. Launch; confirm `C:\tmp\cotabby-live.log` still receives
   `Model … loaded` and constrained-decode markers (`AGENTS.md:53-55`).
2. Set `MaxLogBytes` tiny (via settings JSON) and generate suggestions;
   confirm `cotabby-live.log.1` appears and the primary log keeps growing.
3. Set verbosity to `warning` in the Advanced pane; confirm debug/info
   lines stop, warnings/errors still land.
4. Confirm sanitized redaction still applies after rotation.

## Rollback

- `LogVerbosity="debug"` + restore `MaxLogBytes`/`MaxLogFiles` defaults →
  behavior identical to today except rotation at 8 MB.
- Code revert of `FileLoggerProvider` restores the append-only provider;
  `LogRotationPolicy` has no other callers.

## Privacy & security constraints

- Rotation inherits the secret sanitizer; rolled files are local-only.
- No typed-type persistence introduced; verbosity lowering can only reduce
  what is written, never add field text.
- Credential-field protections unchanged (`IsCredentialField` gate is
  upstream of any logging).
- No cloud calls; local file IO only.
