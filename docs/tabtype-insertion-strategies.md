# Selectable insertion strategies

Status: implemented on `feat/tabtype-insertion-strategies`, stacked on per-app
profile commit `80d4c29`. Exact tests, Windows proof, review and package evidence
are recorded in the pull request and external handoff. No merge, release or live
acceptance is implied.

## Supported contract

A global Advanced setting selects `auto`, `unicode` or `clipboard`. Existing
settings default to `auto`. There is no per-app insertion override in this slice.
Malformed non-string strategy values fall back to auto without discarding valid
global disable/blocklist settings. This does not repair syntactically invalid
JSON, which retains the pre-existing whole-document fallback.
Each insertion snapshots the chosen strategy before waiting for serialization;
a setting change applies to the next call, not half of an already queued write.

- `auto`: preserve the shipped test exactly: at most 24 UTF-16 code units and
  no LF (`\n`) uses one Unicode SendInput batch; other text uses clipboard paste.
  CR-only handling is not silently changed. This is a transport heuristic, not
  proof that a host retained the characters.
- `unicode`: never read/write the clipboard or silently retry through it. Explicit
  text longer than eight UTF-16 code units uses small paced bursts, keeping valid
  surrogate pairs together, on one native thread with input blocked until cleanup.
  If input isolation is unavailable it refuses before writing. Focus is checked
  between bursts; cancellation after initial dispatch does not interrupt the text.
  A later focus/native failure can leave partial text: there is no retry, rollback
  or clipboard fallback. Host applications may still interpret injected characters
  or line endings differently; use auto if this is unreliable in a real app.
- `clipboard`: use the existing clipboard-preserving paste path even for short
  text. Clipboard contents temporarily change. Existing native-format capture,
  sequence ownership, same-thread input blocking/injection/unblocking and
  irreversible-dispatch cleanup remain in force.

Every concrete path retains secure/credential refusal and exact focused-element,
process and foreground-owner checks before mutation. Native event-count success
is not universal host acknowledgement. Never retry another transport after a
partial or irreversible dispatch.

## UI Automation limitation and proposal correction

The older phase-0 draft proposed `TextPattern.SetValue`/range insertion. That is
not a .NET UI Automation text-writing API. `ValuePattern.SetValue` sets the
control's entire value; it is not selection/caret-preserving insertion. The
current focus snapshot is explicitly eventually consistent and has no complete
selection replacement contract. Replacing the whole field from that snapshot
would risk overwriting newer host text.

This slice therefore does not offer a UIA write mode. A manually supplied `uia`
setting must refuse insertion rather than silently fall back to clipboard. Other
unknown historical values use the default auto policy. A future UIA/native-host
adapter needs its own supported API, selection/undo/IME and stale-value design.

References:
- [TextPattern](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.textpattern?view=windowsdesktop-9.0)
- [ValuePattern.SetValue](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.valuepattern.setvalue?view=windowsdesktop-9.0)
  (Microsoft's example explicitly notes TextPattern does not set document text.)

## Verification gates

Pure selector tests pin exact auto boundaries, LF/CR and UTF-16 semantics,
forced choices, unknown-setting fallback and unsupported-UIA refusal. Settings
legacy-load/roundtrip tests and actual Windows Advanced controls verify
persistence/live wiring. Production inserter Windows fixtures must read final
host text and clipboard state for forced modes and retain existing stale-target,
credential and irreversible-dispatch tests. A clean cross-build is not Windows
runtime proof. Exact results, commit and artifact hashes belong in the PR and
external delivery report, not as a self-referential ZIP checksum here.

## Owner test after the verified ZIP is delivered

Extract the whole ZIP into a new folder and keep `runtimes`. Under Advanced,
compare Auto, Unicode and Clipboard in real writing apps; include long text and
multiline text. Confirm Clipboard restores an existing marker and Unicode leaves
it untouched. Restart to check persistence, then switch back to Auto. Use dummy
values for credential checks. Physical keys, host-specific handling, real models
and clipboard watchers/remote-session behavior remain owner testing.
