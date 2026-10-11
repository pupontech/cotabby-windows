# Cotabby project history and provenance

## Original project

Cotabby began as the macOS project [FuJacob/cotabby](https://github.com/FuJacob/cotabby).
The original application used Swift, SwiftUI/AppKit, the macOS Accessibility API,
and local Apple/llama-based completion engines.

## Windows port

This repository started from [Msde-7/cotabby-windows](https://github.com/Msde-7/cotabby-windows).
The Windows port replaces the macOS platform layer with:

- WPF for the desktop UI and settings windows;
- Windows UI Automation for focused-field and caret tracking;
- `SetWindowsHookEx` for the global keyboard hook;
- `SendInput` and UIA text APIs for insertion;
- LLamaSharp/llama.cpp with CPU and Vulkan backends for local inference;
- `H.NotifyIcon` for the Windows system tray.

## This fork

This fork is maintained at [pupontech/cotabby-windows](https://github.com/pupontech/cotabby-windows).
The fork currently adds:

1. A reliable folder-based self-contained Windows x64 release format. The native
   LLamaSharp DLLs remain under `runtimes\win-x64`, avoiding single-file native
   loading failures.
2. A GitHub Actions workflow that tests, publishes, zips, and attaches releases.
3. Current model choices: Qwen3.5 4B Base for general use and Qwen3.5 9B Coder
   for programming, while retaining the existing Qwen and Gemma choices.
4. Documentation and an AI-agent handoff contract in `AGENTS.md`.

## Change boundaries

The fork keeps inference local. Model files are downloaded from Hugging Face only
when the user explicitly selects a model; no application telemetry or cloud
completion service has been added.
