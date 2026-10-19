# Selectable insertion strategies

Status: Auto/Clipboard implementation on `feat/tabtype-insertion-strategies`,
stacked on per-app profile commit `80d4c29`. Forced Unicode and UIA writes are
**deferred**, not supported modes. Exact tests, Windows proof, review and package
evidence are recorded in the feature PR and external handoff. No merge, release
or live acceptance is implied.

## Supported contract

A global Advanced setting selects `auto` or `clipboard`. Existing settings
default to `auto`. There is no per-app insertion override in this slice.
Malformed non-string strategy values fall back to auto without discarding valid
global disable/blocklist settings. This does not repair syntactically invalid
JSON, which retains the pre-existing whole-document fallback.
Each insertion snapshots the chosen strategy before waiting for serialization;
a setting change applies to the next call, not half of an already queued write.

- `auto`: preserve the shipped test exactly: at most 24 UTF-16 code units and
  no LF (`\n`) uses one Unicode SendInput batch; other text uses clipboard paste.
  CR-only handling is not silently changed. This is the legacy transport
  heuristic, **not proof that a host retained the characters**. This slice does
  not redesign or certify Auto's existing host-specific input behavior.
- `clipboard`: use the existing clipboard-preserving paste path even for short
  text. Clipboard contents temporarily change. Existing native-format capture,
  sequence ownership, same-thread input blocking/injection/unblocking and
  irreversible-dispatch cleanup remain in force.
- Explicit saved `unicode` and `uia` requests **refuse nonempty insertion** before
  native or clipboard writes. They never silently become Auto or Clipboard.
  Advanced displays an unsupported-mode warning until a supported choice is
  explicitly selected. Other unknown historical strings use default Auto.

Every supported path retains secure/credential refusal and focused-element,
process and foreground-owner checks before mutation. Native event-count success
is not universal host acknowledgement. Known partial Unicode event dispatch
raises an indeterminate outcome; acceptance retires only the owning offer so the
same full chunk cannot be accepted again. A newer replacement request is not
cancelled by the old failure. This prevents replay, not rollback of host text.
Emoji commits also require live strategy admission before suppressing the host
key and again before erasing `:query:`; a known unsupported mode must preserve
the user's query rather than deleting it before a refused insertion.
Never retry another transport after a partial or irreversible dispatch.

## Why forced Unicode is deferred

The experiment preserved at commit `9639b31` failed real Windows proofs. A large
Unicode event batch lost characters despite native dispatch success. Small paced
bursts retained the long text, but a programmatic focus change after the first
host-visible burst redirected a later burst into another field in the same
process. Pinning the foreground HWND did not fix that failure. `BlockInput`
excludes physical input, not application-driven focus changes, and elapsed time
is not acknowledgement that a host consumed input.

Consequently, the paced path is not enabled in this slice. Its wrong-field
regressions remain fail-closed refusal checks for stored `unicode` requests;
there is no skip or hidden clipboard fallback. Enabling forced Unicode later
requires a supported host acknowledgement/admission design and actual
cross-window and same-window focus-transition proof, not a larger delay.

## UI Automation limitation and proposal correction

The older phase-0 draft proposed `TextPattern.SetValue`/range insertion. That is
not a .NET UI Automation text-writing API. `ValuePattern.SetValue` sets the
control's entire value; it is not selection/caret-preserving insertion. The
current focus snapshot is explicitly eventually consistent and has no complete
selection replacement contract. Replacing the whole field from that snapshot
would risk overwriting newer host text. No UIA write mode is offered.

References:
- [TextPattern](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.textpattern?view=windowsdesktop-9.0)
- [ValuePattern.SetValue](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.valuepattern.setvalue?view=windowsdesktop-9.0)
- [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)
- [BlockInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-blockinput)

## Verification gates

Selector tests pin exact Auto boundaries, LF/CR and UTF-16 semantics, forced
Clipboard, historical fallback and explicit unsupported-mode refusal. Actual
Windows Advanced controls verify persistence/live wiring, including preserving
unsupported saved requests rather than coercing them on load. Production inserter
fixtures verify supported transport readback/clipboard state and refusal without
host mutation for deferred Unicode, including armed wrong-field regressions.
Existing stale-target, credential, clipboard and irreversible-dispatch proofs
remain required. A clean cross-build is not Windows runtime proof. Exact final
results, commit and artifact hashes belong in the PR/external report, not as a
self-referential ZIP checksum here.

## Owner test after the verified ZIP is delivered

Extract the whole ZIP into a new folder and keep `runtimes`. Under Advanced,
compare Auto and Clipboard in real writing apps, including long and multiline
text. Confirm Clipboard restores an existing marker. Restart to check persistence,
then switch back to Auto. There is no selectable forced Unicode or UIA write mode.
Use dummy values for credential checks. Physical keys, host-specific handling,
real models and clipboard watchers/remote sessions remain owner testing.
