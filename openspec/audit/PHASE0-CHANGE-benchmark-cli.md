# Benchmark CLI

**Target phase:** 4 (validates every inference change that precedes it).
**Upstream provenance:** none. **Risk level:** Low (new tool project; no
app-source changes). **Flag:** none (developer tool; ships in the repo but
not the release ZIP unless the workflow opts in).

## Why

Every Phase 2–4 tuning decision — confidence threshold, cache admission,
beam width, token healing — currently lacks a local measurement harness.
`tools/Cotabby.Tools.InferenceSmoke` (`tools/Cotabby.Tools.InferenceSmoke/Program.cs`)
proves the decode works end-to-end on one fixed prefix and prints chars/
sec; the existing seven `Cotabby.Tools.*` projects (`Cotabby.sln`, 24
projects) show the convention. What's missing is a repeatable corpus
runner that quantifies latency, acceptance proxies, and the effect of
each flag — the instrument the earlier phases' rollback notes keep
referencing.

## What Changes

- New `tools/Cotabby.Tools.Benchmark` console project (added to
  `Cotabby.sln` like its siblings): 
  - loads a model from the catalog (or `--model <id>`), warms up, then
    runs a bundled corpus of ~50 representative prefixes (prose, code,
    email, mixed) through `LlamaSuggestionEngine` via
    `SuggestionRequestFactory`;
  - flags under test: `--beam-width`, `--cache`, `--healing`,
    `--confidence-threshold`, `--debounce` — each toggled through the same
    `AppSettings`/engine seams the app uses, so benchmark numbers
    translate directly;
  - reports per-corpus-item latency (prefill/decode split, mirroring the
    `LlamaRuntimeCore.cs:236-238` log line), tokens/sec, confidence-score
    histogram, suppression counts, cache hit rate, and an aggregate JSON
    report written to a caller-chosen path (`--out`);
  - `--compare <baseline.json>` diffs two runs (flag A/B for threshold
    tuning).
- Corpus lives in the tool project as an embedded resource — no user
  text, no network.
- Reuses the exact model-download + runtime-load pattern from
  `InferenceSmoke` (`Program.cs:19-52`); a missing model is a clear
  error, never an auto-download surprise in CI.
- Optionally wired into `windows-ci.yml` as a *small-model* smoke
  (nano) behind a workflow input — CI cost stays bounded; full corpus
  runs are owner/developer-invoked.

## Non-goals

- No bundled user data; the corpus is synthetic and committed.
- No network benchmarking; no telemetry; the report never leaves the
  machine unless the user copies it.
- No changes to `src/` — the tool consumes public seams only
  (`ISuggestionEngine`, `SuggestionRequestFactory`, `AppSettings`).

## Proposal outline (proposal.md)

```markdown
## ADDED Requirements

### Requirement: A benchmark CLI measures inference behavior locally

The repo MUST provide a console benchmark tool that loads a catalog model,
runs a committed synthetic corpus through the suggestion engine under
configurable flags, and writes a local JSON report with latency, quality
proxies, and flag effects. The tool MUST NOT download models implicitly,
MUST NOT make network calls, and MUST NOT include user data.

#### Scenario: Baseline run produces a report
- **WHEN** the tool runs against a cached model with default flags
- **THEN** a JSON report with per-item latency and aggregates is written

#### Scenario: Flag A/B comparison
- **WHEN** two runs are compared
- **THEN** the diff shows latency and quality-proxy deltas per flag

#### Scenario: Missing model fails cleanly
- **WHEN** the requested model is not cached
- **THEN** the tool exits with an error and does not download
```

## Source files touched (verified)

| File | Change |
| --- | --- |
| `tools/Cotabby.Tools.Benchmark/Cotabby.Tools.Benchmark.csproj` | **new** (pattern: `tools/Cotabby.Tools.InferenceSmoke/Cotabby.Tools.InferenceSmoke.csproj`) |
| `tools/Cotabby.Tools.Benchmark/Program.cs` | **new** (pattern: `tools/Cotabby.Tools.InferenceSmoke/Program.cs:19-94`) |
| `tools/Cotabby.Tools.Benchmark/Corpus.cs` (+ `corpus.json` embedded) | **new** — synthetic corpus |
| `tools/Cotabby.Tools.Benchmark/BenchmarkReport.cs` | **new** — report schema + compare |
| `Cotabby.sln` | add project entry (24 → 25 projects) |
| `.github/workflows/windows-ci.yml` | optional small-model smoke behind an input |
| `tests/Cotabby.Core.Tests/BenchmarkCorpusTests.cs` | **new** — corpus validity (non-empty, no secrets-shaped strings) |

## Risks

- CI cost: gated behind a workflow input and restricted to the nano
  model; the release workflow stays untouched.
- Report files accidentally committed: `.gitignore` entry for
  `*.bench.json` (the repo already ignores logs/caches —
  `.gitignore`).
- Model-download surprise: explicit `--model` + cached-only policy
  (mirrors `AppHost.TryLoadCachedAsync` `:177-185` semantics: never
  auto-download).

## Test plan (unit)

`tests/Cotabby.Core.Tests/BenchmarkCorpusTests.cs`:

- `CorpusIsNonEmpty`
- `CorpusItemsAreWellFormed` (prefix/suffix/language fields)
- `CorpusContainsNoSecretShapedStrings` (reuses the Phase 1
  `SecretSanitizer` as the checker — dogfooding the sanitizer)
- `ReportSchemaRoundTrips` (JSON serialize/deserialize)

Smoke coverage for the tool itself runs in the CI job (not unit tests):
nano model, default flags, exit 0, report written.

## Manual Windows smoke steps

1. `dotnet run --project tools/Cotabby.Tools.Benchmark -- --model tabby-2-nano`
   with the nano model cached: completes, prints per-item latencies,
   writes the JSON report.
2. Re-run with `--beam-width 3`: latency delta visible; compare against
   the baseline with `--compare`.
3. Run with `--confidence-threshold 0.5` after `confidence-gating` ships:
   suppression histogram matches the app-side funnel over the same
   corpus.
4. Confirm no network calls during the run (cached model only) and no
   files created outside the `--out` path.
5. CI input smoke (once wired): verify the workflow job passes on the
   nano model within the CI budget.

## Rollback

- The tool is additive: deleting the project + solution entry removes it
  with zero app impact.
- No settings flags, no app-source changes — nothing to default off.
- The optional CI input defaults to off.

## Privacy & security constraints

- Corpus is synthetic and committed; the tool never reads user documents,
  `settings.json`, logs, or the writing-memory store.
- No network calls (cached models only; no implicit download — the same
  contract as `TryLoadCachedAsync`).
- Reports contain latencies/scores/counts only; the corpus-validity test
  (secret-shaped-string check) keeps secrets out of the committed corpus
  and the sanitizer dogfoods here.
- No telemetry: reports are local files the user controls.
