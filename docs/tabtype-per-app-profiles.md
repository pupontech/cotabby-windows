# TabType Phase 2 — per-app profiles

Status: implemented on `feat/tabtype-per-app-profiles`, based on Phase 1
`d10b9c2`. Parent verification: 439 Core tests pass; Windows-targeted App and
RegressionSmoke builds pass with zero warnings/errors. Final independent review
and Windows workflow/runtime proof are pending. This is not a tagged release or
a statement of live Windows acceptance. Remaining Phase 2 features are separate.

## Contract

Settings → Apps will let a user add, edit and remove process-specific overrides.
Profiles contain configuration only, never field text. The empty/default profile
collection preserves the global settings. Existing settings files need no migration.

Supported profile fields are enabled, word/phrase acceptance, completion-length
preset, display mode, multiline permission, debounce and optional single/multiline
token budgets. Each unset value inherits its global setting. A profile preset
selects the existing token-budget pair; an explicit per-line budget overrides
that line of the pair. Battery throttling is applied afterward, not bypassed.

Process keys are exact, case-insensitive executable base names, not titles,
paths or wildcard expressions. The editor may trim user input and remove `.exe`;
the runtime accepts only the stored base-name representation. Duplicate keys
are rejected by the editor. Ambiguous valid duplicate keys loaded from JSON
use global settings rather than choosing an override by list order. Invalid
profiles and null entries are ignored. Numeric ranges are 20–300 ms debounce,
4–96 single-line tokens and 4–128 multiline tokens.

Profiles cannot enable a globally disabled application, override the blocklist,
read credentials, suggest into Cotabby's own process, change the loaded model,
or bypass energy-saver/battery deferral. They do not change the global shortcut
or emoji configuration. Target and effective-settings transitions must retire
obsolete suggestions and work so a late result cannot appear in another field.

Display modes need real rendering evidence; storing the value alone does not
satisfy this contract. The implementation and final Windows proof determine
which rendering semantics are accepted; no full TabType visual parity is claimed.

## Verification gates

- Resolver, mapping and serialization tests: legacy/default/null settings,
  exact matching, inheritance, enum/range validation, duplicate ambiguity and
  preset/explicit-budget precedence.
- Coordinator tests: switching apps and same-process fields, live profile edits,
  delayed/stale generation, global disable/blocklist/self/credential precedence
  and composition with existing battery restrictions.
- Actual Settings window on GitHub Actions Windows: add/edit/remove, validation,
  inherit choices, immediate runtime settings and saved/reloaded values.
- Full Core and Win32 suites, Windows app/tool builds, existing credential and
  real-hook fixture proofs, complete folder-package manifest/native-load checks.
- Independent defect and separate requirement/standards review of the final diff.

No result is claimed here until recorded against the final commit. Physical
keys, real application focus/accessibility metadata, model decoding and real
battery transitions still require owner testing on Windows.

## Owner test sequence (after a verified test ZIP is provided)

1. Extract the entire ZIP into a new folder, keep `runtimes`, run `Cotabby.exe`.
2. Add a `notepad` profile with phrase acceptance and a short completion preset.
   Check Notepad uses it and another writing application retains global defaults.
3. Edit to inherit each value; verify global behavior returns without restart.
4. Disable suggestions in the profile, then remove it. Verify suppression then
   restoration; an existing blocklist entry must still prevent suggestions.
5. Switch quickly between fields/apps while a suggestion is being generated.
   No old text should appear or be inserted into the replacement field.
6. Restart Cotabby and verify saved configuration and behavior. Use dummy values
   to confirm password/sign-in fields remain untouched.

Rollback: remove profiles through Apps (or remove the `AppProfiles` JSON member
with Cotabby stopped). Existing global settings and blocklist remain applicable.
