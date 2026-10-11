# Cotabby agent handoff guide

This file is the continuation contract for AI agents working on this repository.

## Project identity

- Product: Cotabby for Windows, local system-wide ghost-text autocomplete.
- Fork: `pupontech/cotabby-windows`.
- Windows-port source: `Msde-7/cotabby-windows`.
- Original macOS source: `FuJacob/cotabby`.
- License: GNU AGPL v3.0.

Read `PROJECT_HISTORY.md` before changing architecture or describing provenance.

## Architecture

- `src/Cotabby.Core`: platform-independent models, prompt pipeline, state machines.
- `src/Cotabby.Inference`: LLamaSharp model loading, downloading, and constrained decode.
- `src/Cotabby.Win32`: UIA focus tracking, keyboard hook, and text insertion.
- `src/Cotabby.App`: WPF tray app, settings, welcome flow, and overlay.
- `tests/Cotabby.Core.Tests`: fast unit tests.

## Safe working rules

1. Preserve user changes and inspect `git status` before editing.
2. Keep inference local; do not add telemetry or cloud completion calls.
3. Do not commit GGUF files, model caches, raw logs, tokens, or credentials.
4. Model catalog changes must include the exact Hugging Face URL, verified
   `Content-Length`, correct FIM capability, context length, and a README row.
5. Do not publish a single-file executable. LLamaSharp needs the complete
   `runtimes\\win-x64` native layout beside `Cotabby.exe`.

## Verification

Use a .NET 9 SDK, then run sequentially:

```powershell
dotnet restore Cotabby.sln
dotnet test tests\Cotabby.Core.Tests\Cotabby.Core.Tests.csproj -c Release --no-restore
dotnet build src\Cotabby.App\Cotabby.App.csproj -c Release --no-restore
dotnet publish src\Cotabby.App\Cotabby.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o artifacts\Cotabby-win-x64
Compress-Archive -Path artifacts\Cotabby-win-x64\* -DestinationPath artifacts\Cotabby-win-x64.zip
```

For runtime verification, use a cached model or the app's model downloader and
confirm `C:\tmp\cotabby-live.log` reports `Model ... loaded` and a successful
constrained decode. Large models can take several minutes and download through a
`.part` file so interrupted transfers can resume.

## Release process

- Push normal changes to `main`.
- Create a `v*` tag to trigger `.github/workflows/release-windows.yml`.
- The workflow runs tests, creates the folder-based self-contained ZIP, and
  attaches it to the GitHub release.
- Release notes should identify model additions, packaging changes, tests, and
  any known hardware or memory limits.

## Handoff format for the next agent

Report these items before stopping:

- current branch and commit;
- files changed and why;
- tests/build/publish commands and exact outcomes;
- runtime checks performed, including model name and log evidence;
- unresolved risks or user decisions needed;
- whether a release/tag was pushed and its URL.
