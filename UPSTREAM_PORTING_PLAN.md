# Upstream feature-porting plan

This document starts the next Cotabby Windows project: selectively port useful,
platform-appropriate changes from the original Cotabby repository while keeping
the Windows implementation stable.

## Current baseline

- Windows fork: [pupontech/cotabby-windows](https://github.com/pupontech/cotabby-windows)
- Windows source port: [Msde-7/cotabby-windows](https://github.com/Msde-7/cotabby-windows)
- Original macOS project: [FuJacob/cotabby](https://github.com/FuJacob/cotabby)
- Current Windows baseline: commit `b33a552`
- Windows parent baseline: commit `f0c81ac`
- The original repository has a separate history and must not be merged wholesale into the Windows branch.

## Objective

Bring high-value, compatible improvements from the original Cotabby project into Cotabby for Windows without replacing the WPF, UI Automation, Win32 hook, SendInput, or LLamaSharp architecture.

## Priority work

1. Compare upstream changes and identify portable core/inference behavior.
2. Port prompt-budget and autocomplete improvements first.
3. Port model/runtime improvements where they work with LLamaSharp and the existing folder-based native runtime package.
4. Add Windows GPU/performance reporting only after confirming reliable metrics.
5. Port settings and UI ideas as WPF equivalents when they provide clear value.
6. Skip Apple-only features unless a Windows equivalent is explicitly designed.

## Feature status

| Feature | Upstream source | Windows status |
| --- | --- | --- |
| Whole-request output reservation | [#851 / `80f46e6`](https://github.com/FuJacob/cotabby/commit/80f46e64c7a25c11f3f035c3db697612a7e4e868) | Merged (`046d7cf`); Windows live-validated by the owner on a test build, tagged release still pending |
| Performance metrics (GPU readouts + request latency; CPU/RAM graphs not ported) | [#858 / `8cdbea2`](https://github.com/FuJacob/cotabby/commit/8cdbea2d2619b0f89a73d46eb0bb856504d07343) | Implemented on `feat/upstream-performance-metrics`; local tests/build/publish; Windows CI and owner live proof pending |

See [the implementation and acceptance note](docs/upstream-output-budget.md).
The adaptation counts actual loaded-model tokens rather than porting the Swift
estimator; FIM uses the loaded context too. No new UI/settings, model catalog,
telemetry, cloud inference, or native decoding algorithm changes are included.
Do not mark full runtime parity until the owner runs the extracted Windows test
ZIP with a small model and a relevant larger model and verifies real suggestions.
The earlier output-budget PR was merged after owner acceptance. For this performance-metrics feature, branch/PR/CI test-ZIP delivery is allowed; merge and release remain gated on new owner acceptance.

See [the implementation and acceptance note](docs/upstream-performance-metrics.md).
Windows reads the same contract from the Windows "GPU Engine" / "GPU Adapter
Memory" performance counters instead of macOS IOKit; the portable parsing,
aggregation and share arithmetic live in `Cotabby.Core/Metrics`, and the
counter adapter is fully best-effort (an unavailable counter set shows as
"no data", never an error). Latency tracking is opt-in and off by default,
matching upstream. Live GPU numbers and per-adapter behaviour remain owner
gated.

## Explicit non-goals

- Do not merge `FuJacob/cotabby` wholesale.
- Do not replace the Windows WPF app with SwiftUI or AppKit code.
- Do not remove local-only inference or add telemetry/cloud completions.
- Do not commit GGUF files, model caches, credentials, or raw runtime logs.
- Do not publish a single-file executable; LLamaSharp needs the complete `runtimes\\win-x64` directory beside the executable.
- Do not claim parity until the packaged Windows build has been run.

## Required workflow for each feature

1. Open an issue or branch named `codex/upstream-<short-feature-name>`.
2. Record the original commit URL and summarize the behavior being ported.
3. Identify whether the change belongs in `Cotabby.Core`, `Cotabby.Inference`, `Cotabby.Win32`, or `Cotabby.App`.
4. Implement the smallest Windows-compatible slice.
5. Add or update focused tests before broad verification.
6. Run the verification commands below in order.
7. Review the diff for platform leakage, generated files, secrets, and model artifacts.
8. Update `CHANGELOG.md`, the README parity table, and this plan's status.
9. Commit one coherent feature and open a pull request or push the approved branch.

## Verification commands

```powershell
dotnet restore Cotabby.sln
dotnet test tests\Cotabby.Core.Tests\Cotabby.Core.Tests.csproj -c Release --no-restore
dotnet build src\Cotabby.App\Cotabby.App.csproj -c Release --no-restore
dotnet publish src\Cotabby.App\Cotabby.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o artifacts\Cotabby-win-x64
Compress-Archive -Path artifacts\Cotabby-win-x64\* -DestinationPath artifacts\Cotabby-win-x64.zip
git diff --check
```

For runtime verification, launch the published app, exercise a suggestion in a normal text field, and confirm `C:\tmp\cotabby-live.log` contains a model-loaded message and a successful constrained decode. Test both a small model and the feature's relevant larger model when memory permits.

## Copy-paste prompt for the next AI coding agent

```text
You are working in the GitHub repository pupontech/cotabby-windows, a Windows WPF port of the macOS project FuJacob/cotabby. Read AGENTS.md, PROJECT_HISTORY.md, CHANGELOG.md, and UPSTREAM_PORTING_PLAN.md before editing.

Mission:
Selectively port one useful, platform-appropriate feature from the original Cotabby repository into the Windows application. Do not merge the original repository wholesale. Preserve the existing Windows architecture:

- src/Cotabby.Core: platform-independent completion logic and contracts
- src/Cotabby.Inference: LLamaSharp model loading and constrained decoding
- src/Cotabby.Win32: UI Automation, keyboard hook, caret tracking, and text insertion
- src/Cotabby.App: WPF tray application, settings, overlay, and composition

Start by inspecting the current branch, git status, recent commits, project files, tests, and the relevant upstream commit or source file. Never overwrite user changes. Never commit GGUF files, model caches, credentials, raw logs, or telemetry. Keep inference local. Do not replace WPF/Win32 code with SwiftUI, AppKit, or macOS APIs. Do not create a single-file publish because LLamaSharp requires runtimes\\win-x64 native files beside Cotabby.exe.

Feature selection order:
1. Prompt-budget or autocomplete behavior that belongs in Cotabby.Core or Cotabby.Inference.
2. Model/runtime improvements compatible with LLamaSharp.
3. GPU/performance reporting with a verified Windows implementation.
4. Settings and UI improvements rewritten as WPF equivalents.
Skip Apple-only features unless you can define and justify a Windows equivalent.

Before coding, write a short implementation note containing:
- the upstream commit URL;
- the exact behavior to port;
- affected Windows files and why;
- compatibility risks and a rollback plan;
- acceptance criteria and tests.

Implement one focused feature only. Add or update focused tests. Update the README parity table, CHANGELOG.md, and the relevant project documentation. Use a branch named codex/upstream-<short-feature-name> if branch work is requested. Do not push, create releases, or modify GitHub settings unless the user explicitly requests that delivery step.

Run these commands sequentially and report exact outcomes:

dotnet restore Cotabby.sln
dotnet test tests\Cotabby.Core.Tests\Cotabby.Core.Tests.csproj -c Release --no-restore
dotnet build src\Cotabby.App\Cotabby.App.csproj -c Release --no-restore
dotnet publish src\Cotabby.App\Cotabby.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o artifacts\Cotabby-win-x64
git diff --check

If the published app can be run, verify a real suggestion in a Windows text field and inspect C:\tmp\cotabby-live.log for model loading and successful constrained decoding. Do not claim runtime verification if it was not done.

Finish with a handoff report containing:
- branch and commit;
- original upstream commit and feature;
- files changed and purpose;
- tests, build, publish, and runtime results;
- unresolved risks or decisions;
- whether packaging or release remains to be done.
```

## Handoff checklist

Every completed feature should leave behind:

- a focused commit and a clean reviewable diff;
- a link to the original upstream change;
- tests and exact command outcomes;
- runtime evidence or an explicit statement that runtime testing is pending;
- updated README parity and changelog entries;
- a note describing any Windows-specific deviation from upstream behavior.
