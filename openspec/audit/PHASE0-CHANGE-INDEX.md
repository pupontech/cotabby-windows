# Phase 0 audit — proposed Phase 1–4 change specifications

Spec-only audit of `cotabby-windows` (v0.4.1, Windows CI green at `bb3469f`,
115/115 tests). One `PHASE0-CHANGE-<slug>.md` per proposed change, ready to be
converted into `openspec/changes/<slug>/` proposals (or flat
`openspec/changes/<slug>.md` files in this repo's current convention) when the
owner approves a phase.

## OpenSpec convention audit (what actually exists here)

- `openspec/changes/` holds **flat markdown files**, not folders:
  `port-upstream-features.md`, `add-current-autocomplete-models.md`.
- Repo change-file convention (both existing files): `# Title`,
  `## Problem`, `## Scope`, `## Non-goals`, `## Acceptance criteria`,
  `## Rollback`. No `## ADDED/MODIFIED Requirements` delta blocks exist yet.
- There is **no `openspec/specs/` directory** and **no `openspec` CLI**
  installed (`which openspec` → not found). Every doc below therefore keeps
  the repo's flat style as the primary structure and adds a **proposal
  outline** in standard OpenSpec delta form (`## ADDED Requirements` /
  `### Requirement:` / `#### Scenario:`) so the change validates as-is if
  the repo later adopts `openspec validate <change> --strict`.
- Documentation discipline comes from `UPSTREAM_PORTING_PLAN.md:65-79`
  (branch `codex/upstream-<short-feature-name>`, upstream commit URL, tests
  before broad verification, `git diff --check`, update `CHANGELOG.md` +
  README parity table + plan status) and `AGENTS.md` (privacy rules,
  verification commands, handoff report).

## Proposed phasing (risk- and dependency-ordered)

| Phase | Theme | Changes (in order) |
| --- | --- | --- |
| 1 | Safety & observability foundations | `secret-sanitizer` → `log-rotation-verbosity` → `suggestion-statistics-funnel` → `smarter-field-suppression` → `battery-aware-inference` |
| 2 | Settings & interaction behavior | `per-app-profiles` → `insertion-strategies` → `tab-acceptance-retention` → `confidence-gating` → `prediction-cache` |
| 3 | Input expansion & writing aids | `token-healing` → `inline-slash-macros` → `writing-memory` |
| 4 | Advanced inference & tooling | `beam-search` → `alternatives-popup` → `benchmark-cli` |

Ordering rationale: the secret sanitizer must exist before anything else
writes new log lines; the statistics funnel is the measurement substrate
Phase 2/3 tuning needs; confidence gating precedes prediction-cache
admission; beam search must land before the alternatives popup that
consumes its candidates; the benchmark CLI validates the inference changes
last. `writing-memory` is deliberately last in Phase 3 because it is the
first feature that would persist user text and needs an explicit owner
privacy decision (upstream #841 was declined on exactly this ground).

## Files in this audit

| File | Change | Target phase | Upstream provenance |
| --- | --- | --- | --- |
| `PHASE0-CHANGE-secret-sanitizer.md` | Redact secret-shaped fragments in every log sink | 1 | none (response to v0.4.1 privacy fix) |
| `PHASE0-CHANGE-log-rotation-verbosity.md` | Size-capped rotation + verbosity setting | 1 | none |
| `PHASE0-CHANGE-suggestion-statistics-funnel.md` | Count-only triggered→accepted funnel | 1 | none |
| `PHASE0-CHANGE-smarter-field-suppression.md` | Better credential-field recall/precision | 1 | extends #848 / `d81d304` |
| `PHASE0-CHANGE-battery-aware-inference.md` | Defer/degrade inference on battery | 1 | none |
| `PHASE0-CHANGE-per-app-profiles.md` | Per-process setting overrides | 2 | none (extends per-app blocklist) |
| `PHASE0-CHANGE-insertion-strategies.md` | Selectable insertion method | 2 | none |
| `PHASE0-CHANGE-tab-acceptance-retention.md` | Retain suggestion across accept round-trips | 2 | extends #840 / `3ad86f3` |
| `PHASE0-CHANGE-confidence-gating.md` | Suppress low-confidence suggestions | 2 | none |
| `PHASE0-CHANGE-prediction-cache.md` | Bounded in-memory result reuse | 2 | none |
| `PHASE0-CHANGE-token-healing.md` | Repair token-boundary artifacts | 3 | none |
| `PHASE0-CHANGE-inline-slash-macros.md` | `/macro` text expansion | 3 | none |
| `PHASE0-CHANGE-writing-memory.md` | Opt-in local writing-style memory | 3 | related to #841 / `c3d17c1` (declined upstream) |
| `PHASE0-CHANGE-beam-search.md` | N-best constrained decode | 4 | none |
| `PHASE0-CHANGE-alternatives-popup.md` | Candidate picker popup | 4 | none |
| `PHASE0-CHANGE-benchmark-cli.md` | `tools/Cotabby.Tools.Benchmark` | 4 | none |

## Cross-change constraints (apply to every change)

- All new decision logic lands in `Cotabby.Core` so
  `tests/Cotabby.Core.Tests` can reach it (that project references only
  `Cotabby.Core`, `Cotabby.Inference`, compile-links
  `src/Cotabby.App/Settings/AppSettings.cs`, and embeds the two XAML files
  as resources — `Cotabby.App`/`Cotabby.Win32` code is not unit-testable
  today and must stay thin).
- New `AppSettings` fields are **additive with safe defaults** (missing keys
  deserialize to defaults per `SettingsStore.Load`); every risky feature
  ships **off by default** behind an `EnableXyz`-style flag.
- `IsCredentialField` remains the single policy flag; every new caller
  (popup, macros, memory, cache) re-checks it before touching a field.
- No typed-type persistence anywhere; no cloud calls; no telemetry;
  inference stays local.
- Every change follows the `UPSTREAM_PORTING_PLAN.md` workflow: focused
  commit, tests first, `dotnet test` → `dotnet build` → publish → package
  verification, `CHANGELOG.md` + README parity table + plan status updates.
