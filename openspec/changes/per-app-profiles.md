# Per-app suggestion profiles

## Problem

The global suggestion controls and per-process deny list cannot customize safe
suggestion behavior for different applications. This implements the first slice
of Phase 2 in `openspec/audit/PHASE0-CHANGE-INDEX.md`, not the rest of Phase 2.

## Scope

- Additive local `AppProfiles` settings, empty by default, with nullable overrides.
- Pure Core DTO resolver plus App-settings mapper; no App dependency in Core.
- Exact case-insensitive process keys; UI-only trim/`.exe` normalization. Reject
  paths, wildcard keys and Cotabby's own process. Invalid entries are ignored.
- Duplicate keys rejected by UI; multiple valid runtime matches use global
  settings regardless of order, resolving the older first-match/no-ambiguity
  proposal conflict in favor of predictable baseline behavior.
- Acceptance, preset, display, multiline, debounce and explicit token budgets.
  Explicit budgets win over the selected preset; null inherits.
- Actual Apps-pane add/edit/remove controls and existing local persistence.
- Retire obsolete work/output on target and effective-settings transitions.

## Non-goals

No model switching, wildcard rules, window/field profiles, cloud calls,
telemetry, clipboard context, typing history, writing memory or changes to the
credential metadata-before-text boundary. No inference policy or display-mode
parity is inferred merely from a persisted property.

## Acceptance criteria

The detailed contract and acceptance gates are in
`docs/tabtype-per-app-profiles.md`. In particular:

1. Legacy/empty settings behave globally; null profile collections and entries
   are safe; bad keys, enums and numeric ranges do not grant new behavior.
2. Switching app/field or editing profiles cannot retain stale results or make
   app-specific settings bleed into unrelated processes.
3. Global disable, blocklist, self PID, credential protection, energy saver and
   battery restrictions cannot be bypassed by profiles.
4. Actual Windows Settings proof covers controls, validation, add/edit/remove,
   inheritance and persisted/reloaded values. Core tests alone are insufficient.
5. Full tests/builds and Windows workflow/package proof must pass at the final
   SHA. Live keys, models and application-specific accessibility remain owner gates.

## Rollback

Remove profiles in Apps or remove `AppProfiles` from settings with Cotabby
stopped. Existing globals and blocklist remain in force.
