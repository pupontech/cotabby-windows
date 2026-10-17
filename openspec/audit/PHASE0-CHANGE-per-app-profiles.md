# Per-app profiles

**Target phase:** 2. **Upstream provenance:** none (the upstream port has a
per-app *disable* list; Windows already ships it as `BlockedApps`,
`AppSettings.cs:73-78`). **Risk level:** Medium (settings-model extension +
UI). **Flag:** profiles are additive; an empty profile list preserves
current behavior exactly.

## Why

`BlockedApps` is binary: a listed process gets nothing, an unlisted process
gets everything. Real usage is per-context: code editors want the code
model, phrase acceptance, and FIM; chat clients want short suggestions and
inline mode; terminals want multi-line off. Today users switch the global
completion length or acceptance mode by hand when they change apps. A
per-app profile layer makes the existing settings overridable per process
name without touching global defaults.

## What Changes

- New `Cotabby.App.Settings.AppProfile`: a record of *overrides only*
  (`Enabled`, `AcceptanceMode`, `CompletionLengthPreset`,
  `DisplayMode`, `AllowMultiLine`, `DebounceMs`, `MaxTokensSingleLine`,
  `MaxTokensMultiLine`) keyed by case-insensitive process name (no .exe),
  mirroring the `BlockedApps` convention. Null members mean "inherit
  global".
- `AppSettings.AppProfiles` (`List<AppProfile>`, default empty — additive
  JSON, old files deserialize unchanged per `SettingsStore.Load`).
- New pure `Cotabby.Core.Settings.AppProfileResolver` (Core so it is
  unit-testable; `AppSettings` itself is compile-linked into the test
  project, `Cotabby.Core.Tests.csproj`): given global `AppSettings` +
  process name → an effective settings snapshot. Resolution is
  deterministic: exact process-name match, else global. No wildcards, no
  ordering ambiguity.
- `SuggestionCoordinator` applies the resolved snapshot at focus time:
  `OnFocusChanged` (`SuggestionCoordinator.cs:257`) re-resolves per
  `FocusedField.ProcessName` (`FocusedField.cs:23`) and pushes
  acceptance granularity / token budgets / multi-line into the existing
  coordinator properties (`:156-166`, `:181`). Resolution failure (bad
  process name) falls back to global.
- Apps pane (`SettingsWindow.xaml.cs:854-932`) gains a profile editor:
  add/remove per-app overrides in the same visual style as the existing
  blocked-app rows (`BuildBlockedAppRows` `:858`).
- `BlockedApps` stays as the deny list; a profile with
  `Enabled=false` is the new, equivalent spelling — both work, the UI
  offers profiles.

## Non-goals

- No per-*field* or per-window profiles (process granularity only, same
  as `BlockedApps`).
- No per-app model selection (model stays global; per-app model switching
  would thrash the loaded GGUF).
- No wildcard/glob process matching (keeps the resolver total and
  testable).

## Proposal outline (proposal.md)

```markdown
## ADDED Requirements

### Requirement: Per-app setting overrides

The app MUST support per-process-name overrides of suggestion settings,
resolved deterministically at focus time. A missing profile MUST behave
exactly like the global settings. Overrides MUST apply only to suggestion
behavior; credential-field suppression MUST remain global.

#### Scenario: Profile overrides acceptance mode for one app
- **WHEN** focus enters a process with an `AcceptanceMode=phrase` profile
- **THEN** Tab accepts phrases there and words elsewhere

#### Scenario: Unlisted app inherits globals
- **WHEN** focus enters a process with no profile
- **THEN** global settings apply unchanged

#### Scenario: Legacy settings file without profiles
- **WHEN** an old `settings.json` is loaded
- **THEN** behavior is identical to the previous version
```

## Source files touched (verified)

| File | Change |
| --- | --- |
| `src/Cotabby.App/Settings/AppProfile.cs` | **new** — override record |
| `src/Cotabby.Core/Settings/AppProfileResolver.cs` | **new** — pure resolver |
| `src/Cotabby.App/Settings/AppSettings.cs:73-78` | add `AppProfiles` list (additive) |
| `src/Cotabby.Core/Suggestions/SuggestionCoordinator.cs:257-291` | re-resolve on focus change |
| `src/Cotabby.App/Hosting/AppHost.cs:133-169` | resolve initial snapshot |
| `src/Cotabby.App/UI/SettingsWindow.xaml.cs:854-932` | Apps-pane profile editor |
| `tests/Cotabby.Core.Tests/AppProfileResolverTests.cs` | **new** |
| `tests/Cotabby.Core.Tests/DoubleTapSettingsTests.cs` | extend (profiles absent → defaults) |

## Risks

- Focus flapping (UIA re-fires) re-resolving per event: resolver is pure
  and cheap; re-apply only on `ProcessName`/`StableFieldIdentity` change
  (the coordinator already cancels/refreshes on field change `:267-274`).
- Profile UI complexity creep: keep to the existing blocked-app row
  pattern; no nested trees.
- Profile for Cotabby's own process name: self-exclusion already exists
  (`SuggestionAvailability.cs:36`) and must not be profile-overridable —
  resolver refuses profiles targeting the current process.

## Test plan (unit)

`tests/Cotabby.Core.Tests/AppProfileResolverTests.cs`:

- `NoProfiles_ReturnsGlobalSnapshot`
- `MatchingProfile_OverridesListedFieldsOnly`
- `NonMatchingProfile_Ignored`
- `CaseInsensitiveProcessMatch`
- `SelfProfile_IsRefused`
- `EmptyOverrideInheritsGlobal` (null members)
- `ProfileForBlockedApp_DoesNotResurrectSuggestions` (blocklist wins)
- `ConflictingProfiles_FirstMatchWinsDeterministically`

`DoubleTapSettingsTests.cs` additions:

- `LegacySettingsWithoutProfiles_KeepGlobalBehavior`

## Manual Windows smoke steps

1. Add a profile for `notepad` with phrase acceptance; confirm Tab accepts
  phrases in Notepad and words elsewhere (e.g., a browser input).
2. Add a profile with `Enabled=false` for a chat app; confirm no ghost
  text there while other apps work.
3. Restart the app; confirm profiles persist in
  `%APPDATA%\Cotabby\settings.json` and apply identically.
4. Confirm a profile named `cotabby` is refused (no self-suggestions).

## Rollback

- Remove all profiles from the Apps pane (or delete `AppProfiles` from
  `settings.json`) → pure global behavior; resolver with an empty list is
  the identity function.
- `BlockedApps` behavior is untouched, so pre-existing disable rules keep
  working even mid-rollback.

## Privacy & security constraints

- Profiles contain user-chosen configuration values only — no typed text,
  no field content, no credentials.
- Credential-field suppression (`IsCredentialField`) is **not**
  profile-overridable: the resolver only touches suggestion-behavior
  knobs, and the gate in `SuggestionAvailability`/`TriggerGeneration`
  runs before profile resolution can matter.
- No cloud calls; profiles persist locally in the existing settings file.
