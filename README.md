# Cotabby for Windows

**Current release: v0.5.0** (see [`CHANGELOG.md`](CHANGELOG.md); the version lives
in [`VERSION`](VERSION) and is stamped into the shipped build).

Local AI autocomplete for Windows. A system-wide ghost-text suggestion engine that
runs entirely on-device via [llama.cpp](https://github.com/ggerganov/llama.cpp), with
no cloud calls and no telemetry.

This is a native Windows port of [FuJacob/cotabby](https://github.com/FuJacob/cotabby)
(macOS, Swift). The macOS app uses AppKit, SwiftUI, and the Accessibility API to read
the focused text field, render ghost text near the caret, and insert accepted
suggestions on `Tab`. This port replaces that stack with WPF, UI Automation (UIA),
`SetWindowsHookEx`, and `SendInput` to provide the same experience on Windows 10/11.

## What you get

- **System-wide ghost text** — works in any focused text field exposed via UI Automation
  (Notepad, VS Code, Slack, Discord, Outlook, browser inputs, …).
- **Word-by-word acceptance** — press `Tab` to take the next word (or whole phrase, in
  phrase mode) and keep pressing to take more, exactly like the macOS app; press
  `` ` `` (backtick) to take the entire suggestion at once by default. Optionally
  choose **Double-tap Tab** in Settings > Shortcuts: the second distinct Tab within
  300 ms accepts the rest. Rejected captured keys are not replayed; see the
  [acceptance safety contract](docs/upstream-double-tap.md).
- **Local completion models** — general-purpose base models plus a code-focused
  Qwen3.5 Coder option. Base models continue
  your text instead of chatting at it, which is what makes the suggestions read
  like *your* next words.
- **The upstream prompt pipeline** — persona ("Written by <you>."), language
  conditioning, budgeted context sections, greedy constrained decode with a
  no-repeat-3-gram guard, sentence-boundary stops, and a one-shot normalizer
  (prompt-echo strip, trailing-duplication filter, insertion safety gate).
- **GPU inference** — llama.cpp's Vulkan backend offloads every layer to your GPU
  (NVIDIA/AMD/Intel); falls back to CPU automatically. A persistent context keeps
  time-to-suggestion in the low hundreds of milliseconds.
- **Inline `:emoji:` autocomplete** — type `:smile`, `:tada`, `:+1` and a small popup
  shows matching glyphs; close the trigger with `:` to commit.
- **First-run welcome wizard** — mirrors the macOS onboarding: template cards
  (Quick / Everyday / Powerful), profile, keybinds, model download.
- **Settings window with parity panes** — sidebar navigation like the macOS app:
  General, Engine & Model, Writing, Shortcuts, Apps, Advanced, About.
- **Launch at login** — opt-in via the General pane; written to `HKCU\…\Run` so no
  admin is required.
- **Customizable ghost text** — the upstream color preset palette, opacity slider,
  optional `Tab` hint chip; automatic light/dark theme follow.
- **All inference local** — llama.cpp via LLamaSharp; no network calls except the
  one-time model download from Hugging Face. No telemetry.

## Models

| Model          | File                             | Size    | Notes                        |
| -------------- | -------------------------------- | ------- | ---------------------------- |
| `tabby-2-plus` | `Qwen3.5-4B-Base.i1-Q4_K_M.gguf` | ~2.7 GB | best general default         |
| `tabby-2-nano` | `Qwen3.5-0.8B-Base.i1-Q6_K.gguf` | ~0.6 GB | fastest, low-memory          |
| `tabby-2-mini` | `Qwen3.5-2B-Base.i1-Q4_K_M.gguf` | ~1.3 GB | recommended default          |
| `tabby-2-code` | `Qwen3.5-9B-Coder.i1-Q4_K_S.gguf` | ~5.5 GB | best for programming         |
| `tabby-2-base` | `gemma-4-E2B.i1-Q6_K.gguf`       | ~3.9 GB | higher quality               |
| `tabby-2-pro`  | `gemma-4-E4B.i1-Q4_K_M.gguf`     | ~5.3 GB | best quality, needs 16 GB+   |

The Qwen3.5 models support fill-in-middle (used when the caret is mid-line).
The code model is optimized for programming; use `tabby-2-plus` for general
writing and mixed use.

## Stack

| Concern                    | macOS (Swift)                  | Windows (this port, C# / .NET 9)        |
|----------------------------|--------------------------------|------------------------------------------|
| UI                         | SwiftUI + AppKit               | WPF                                      |
| Focus / caret detection    | Accessibility (AX) API         | UI Automation (UIA)                      |
| Global keyboard hook       | `CGEventTap`                   | `SetWindowsHookEx` (WH_KEYBOARD_LL)      |
| Text insertion             | `CGEvent` + AX                 | `SendInput` + UIA `TextPattern`          |
| Inference                  | llama.cpp (Swift bindings)     | [LLamaSharp](https://github.com/SciSharp/LLamaSharp) |
| Apple Intelligence engine  | `FoundationModels`             | n/a — llama.cpp only                     |
| Menu bar                   | NSStatusItem                   | `H.NotifyIcon` system tray               |
| Overlay window             | NSPanel (borderless, topmost)  | WPF window (transparent, topmost, click-through) |

## Project layout

```
src/
  Cotabby.Core/        Pure logic: state machines, value types, contracts.
  Cotabby.Inference/   LLamaSharp runtime + suggestion engine.
  Cotabby.Win32/       UIA focus tracking, keyboard hook, caret geometry, SendInput.
  Cotabby.App/         WPF host: tray icon, settings, overlay window, DI composition.
```

## Porting more upstream features

The Windows port is intentionally maintained separately from the original
macOS repository. To continue bringing over useful improvements safely, follow
the [upstream feature-porting plan](UPSTREAM_PORTING_PLAN.md). It includes the
project baseline, feature priorities, verification commands, and a complete
copy-paste prompt for the next AI coding agent.

`Cotabby.Core` and `Cotabby.Inference` target plain `net9.0` so the testable logic
stays decoupled from the Windows desktop runtime. `Cotabby.Win32` and `Cotabby.App`
target `net9.0-windows`.

## Requirements

- Windows 10 1809+ / Windows 11
- .NET 9 SDK
- x64 (ARM64 support is on the roadmap once LLamaSharp ARM64 native packages stabilize)

## Build

```powershell
dotnet restore
dotnet build -c Release
```

## Download a release

Windows releases are published as a self-contained `win-x64` ZIP. Extract the
complete ZIP and run `Cotabby.exe`. Keep the `runtimes\win-x64` folder beside
the executable: LLamaSharp loads its native CPU/Vulkan libraries from that
folder. The release intentionally uses a folder publish instead of a
single-file executable for reliable native-library loading.

To create the same package locally:

```powershell
dotnet publish src\Cotabby.App\Cotabby.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o artifacts\Cotabby-win-x64
Compress-Archive -Path artifacts\Cotabby-win-x64\* -DestinationPath artifacts\Cotabby-win-x64.zip
```

## Run

```powershell
dotnet run --project src\Cotabby.App
```

On first run, Cotabby will register a global keyboard hook and place an icon in the
system tray. There is no UAC elevation; UI Automation and `WH_KEYBOARD_LL` both work
under standard user rights, though some elevated processes (e.g. Task Manager) will
not surface AX trees.

The first launch also opens the **welcome wizard** to capture a couple of preferences
and let you pick a model — every step is skippable. You can re-run the wizard any time
from `Tray → Run welcome again…`.

## Configuration

- Settings file: `%APPDATA%\Cotabby\settings.json`
- Models cache:  `%LOCALAPPDATA%\Cotabby\models`
- Log file:      `C:\tmp\cotabby-live.log`

Open them from `Tray → Settings… → Advanced` (each has a one-click "Open" button).

## Parity status vs the macOS port

| Area                              | Windows port              |
|-----------------------------------|---------------------------|
| Base-model catalog and current Qwen additions | ✅ |
| Base-completion prompt pipeline (persona / language / budgeted sections) | ✅ |
| Whole-request output reservation ([upstream #851](https://github.com/FuJacob/cotabby/commit/80f46e64c7a25c11f3f035c3db697612a7e4e868)) | ✅ (live-validated by the owner on the `bb3469f` test build) |
| Performance pane: live GPU readouts + opt-in request latency (partial port; no CPU/RAM graphs) ([upstream #858](https://github.com/FuJacob/cotabby/commit/8cdbea2d2619b0f89a73d46eb0bb856504d07343)) | Merged; Windows CI passed, owner confirmed tracking/Clear; physical GPU accuracy remains unconfirmed |
| Sign-in and verification fields left alone ([upstream #848](https://github.com/FuJacob/cotabby/commit/d81d304f1ea0a5b4bb5feff9b71407ebee56b13c)) | ✅ metadata-based (password flag, accessible label, automation id, address shape); browser metadata coverage varies, not full DOM parity |
| Correctable-misspelling completion guard ([upstream #814](https://github.com/FuJacob/cotabby/commit/ac2699e4ab40b68cf7429b7d13d5a7206c99dbbc)) | ✅ pure guard live for junk punctuation runs; seam/leading-word spelling checks pluggable (`ISpellAssessor`), dormant until a backend is registered. [Scope](docs/upstream-seam-guard.md) |
| Low Power Mode pause ([upstream #812](https://github.com/FuJacob/cotabby/commit/d4c4d23410dc8a720c30725ae63451dad598f525)) | ✅ opt-in Settings → General toggle; Windows energy saver read via `GetSystemPowerStatus`. [Scope](docs/upstream-low-power-mode.md) |
| Held-presentation rapid-Tab acceptance ([upstream #838](https://github.com/FuJacob/cotabby/commit/7724926b3e93f14e3b576ba46ff52c4f64d9712d)) | ✅ coordinator presentation mark authorizes the rapid second Tab. [Scope](docs/upstream-rapid-tab.md) |
| Opt-in typing history ([upstream #841](https://github.com/FuJacob/cotabby/commit/c3d17c1a6f709d8181a8d888f33e23f03014c428)) | ❌ deliberately not ported; storing user text on disk needs a separate privacy decision |
| Constrained greedy decode (top-20 argmax, no-repeat-3-gram, sentence stops) | ✅ |
| One-shot output normalizer (echo strip, dup filter, safety gate) | ✅ |
| Fill-in-middle for mid-line carets | ✅ (Qwen3.5 models)       |
| Word-by-word / phrase acceptance   | ✅ (`Tab`, mode in Shortcuts) |
| Accept-whole key                   | ✅ (`` ` `` backtick default) |
| Double-tap Tab ([upstream #840](https://github.com/FuJacob/cotabby/commit/3ad86f3ff0c7fa4abe250fde348611f162027650)) | Global-only opt-in in v0.4.1; synthetic Windows hook/UIA and production-inserter proofs. Physical keys/live models remain owner-verified. [Scope and acceptance](docs/upstream-double-tap.md) |
| Ghost text overlay (color/opacity, hint chip, theme-aware) | ✅ |
| Settings window (sidebar: General / Engine & Model / Writing / Shortcuts / Apps / Advanced / Performance / About) | ✅ |
| First-run welcome wizard (template cards) | ✅                 |
| Inline `:emoji:` autocomplete      | ✅ (built-in catalog)     |
| Per-app blocklist                  | ✅                        |
| Launch at login                    | ✅ (HKCU Run key)         |
| Completion length presets (2–4 / 4–7 / 7–12 / 12–20 words) | ✅ |
| GPU offload                        | ✅ Vulkan (vs Metal)      |
| Apple Intelligence engine          | n/a (llama.cpp only)      |
| Visual-context / screenshot OCR    | not yet                   |
| Custom rules editor                | not yet                   |

## License

GNU AGPL v3.0 — same as upstream Cotabby.
