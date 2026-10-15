# Phase 0-A — cotabby-windows implementation audit (part 2)

**Repo:** `/root/Projects/cotabby-windows`
**Audited state:** branch `fix/identity-collision-and-hook-io`, base commit `8c0bcd2`
("Merge pull request #6 from pupontech/port/upstream-round5"), plus a dirty
working tree (6 modified files, 2 untracked paths).

> **Scope note.** Part 1 of this audit already covered `SuggestionCoordinator`,
> `LlamaSuggestionEngine`, `LlamaRuntimeCore`, `UiaFocusTracker`,
> `SendInputTextInserter`, `AppSettings`, `SuggestionSessionReconciler`,
> `KeyboardHook` and `AppHost`. This document is the *complement*: it inventories
> **every** file in `src/` (so the inventory is complete even for part-1 files),
> and goes deep on the files part 1 did not read — `Support/*`, `Emoji/*`,
> `Overlay/GhostOverlayWindow`, `UI/SettingsWindow`, `App.xaml.cs`, `Tray/*`,
> `Win32/Interop/NativeMethods.cs`, `tools/*`, `tests/*`.
>
> **Verification actually run:** `dotnet test tests/Cotabby.Core.Tests` →
> **319 passed, 0 failed, 0 skipped** (2 s). `Cotabby.Win32.Tests` is
> Windows-targeted (`net9.0-windows`) and does **not** run on this Linux host —
> see §3.3. Every `file:line` below is from the audited working tree; the tree
> advanced once mid-audit (upstream-round5 merge landed), and citations were
> re-verified after that.

---

## 0. Repository state

| Item | Value |
| --- | --- |
| HEAD | `8c0bcd2` merge of `port/upstream-round5` |
| Branch | `fix/identity-collision-and-hook-io` |
| Modified (uncommitted) | `Cotabby.sln`, `App/Hosting/AppHost.cs`, `App/Hosting/FileLoggerProvider.cs`, `Core/Suggestions/SuggestionCoordinator.cs`, `Win32/Cotabby.Win32.csproj`, `Win32/Focus/UiaFocusTracker.cs` |
| Untracked | `openspec/audit/`, `tests/Cotabby.Win32.Tests/` |
| Solution projects | 12 (4 src, 2 test, 6 tools) |
| Core tests | **319 passing** (verified locally) |

Uncommitted changes, all narrow and correct-in-intent:

1. `SuggestionCoordinator.cs` — 4 changes: (a) bounded 2 s wait on the captured
   accept-intent dispatch, returning `AcceptanceIntentStatus.DispatchFailed`
   instead of hanging forever if the dispatcher stops servicing posts
   (`:864-889`); (b) `OperationCanceledException` now maps to
   `InsertionCancelled` rather than `DispatchFailed` (`:890-892`); (c) the
   session-exhaustion continuation path now runs `SuggestionAvailability.ShouldRequest`
   with a neutral `ContinuationKeyEvent` (`:1131-1140`, `:1227-1231`) — closes a
   hole where a continuation could fire into a credential/blocked/self field;
   (d) `Post()` wraps `_ui.Post` in try/catch so a dead dispatcher cannot throw
   on the hook thread (`:1387-1397`).
2. `UiaFocusTracker.cs` — `FallbackFieldIdentity` now routes through a new
   internal `FallbackIdentityFromDescriptor(descriptor)` (`:364-407`) which
   grants each distinct descriptor a `|seq=N` suffix exactly once and returns
   **null on repeat**. A repeated descriptor means two anonymous controls in one
   window are indistinguishable, so ownership becomes UNPROVEN (Tab passes
   through) instead of colliding (insert into the wrong field).
3. `FileLoggerProvider.cs` — constructor failure now degrades to a disabled file
   sink instead of throwing at startup (`:22-37`); minimum level raised
   `Debug`→`Information` (`:64`). Paired with `AppHost.cs:65` raising the root
   logger minimum to `Information`.
4. `Cotabby.Win32.csproj` — `InternalsVisibleTo("Cotabby.Win32.Tests")` (`:24-28`).
5. `Cotabby.sln` — registers `Cotabby.Win32.Tests`.

---

## 1. Per-file inventory of `src/`

### 1.1 `Cotabby.Core` (`net9.0`, no WPF, no UIA — pure logic + contracts)

| File | Namespace | Public types | Threading model |
| --- | --- | --- | --- |
| `Focus/FocusedField.cs` | `Core.Focus` | `FocusedField` (record, `required` members), `ScreenRect` (readonly record struct) | Immutable snapshot; no threading |
| `Focus/IFocusTracker.cs` | `Core.Focus` | `IFocusTracker` | Contract only |
| `Focus/CredentialFieldDetector.cs` | `Core.Focus` | `CredentialFieldDetector` (static) | **Pure/static, no state, no locks.** Safe from any thread |
| `Input/KeyboardEvent.cs` | `Core.Input` | `KeyboardEvent`, `KeyKind` | Immutable value |
| `Input/IKeyboardHook.cs` | `Core.Input` | `IKeyboardHook`, `KeyEventArgs` | Contract only |
| `Insertion/ITextInserter.cs` | `Core.Insertion` | `ITextInserter` | Contract only |
| `Overlay/IOverlayPresenter.cs` | `Core.Overlay` | `IOverlayPresenter` (`Show`/`Update`/`Hide`) | Contract only |
| `Models/ModelCatalog.cs` | `Core.Models` | `CotabbyModel` (record), `ModelCatalog` (static table) | Static readonly; safe |
| `Metrics/GpuUsageSample.cs` | `Core.Metrics` | `GpuUsageSample`, `IGpuUsageProvider`, `IGpuUsageProviderResettable`, `GpuUsageTracker` | `GpuUsageTracker` is **stateful and unsynchronized** — must be called from one thread only (the sampling session guarantees this) |
| `Metrics/GpuCounterSampler.cs` | `Core.Metrics` | `GpuEngineCounterReading`, `GpuCounterSampler` | Pure/static |
| `Metrics/MetricsAggregator.cs` | `Core.Metrics` | `GpuEngineIdentity`, `GpuEngineInstance`, `MetricsAggregator` | Pure/static; compiled `Regex` is thread-safe |
| `Presentation/PerformanceSamplingSession.cs` | `Core.Presentation` | `PerformanceCounterAvailability` (enum), `PerformanceSample` (record struct), `PerformanceSamplingSession` | **Lock-guarded** (`_gate`, `:60`) single-flight + epoch; the one non-trivial concurrency primitive in Core |
| `Suggestions/ISuggestionEngine.cs` | `Core.Suggestions` | `ISuggestionEngine`, `SuggestionChunk` | Contract |
| `Suggestions/SuggestionRequest.cs` | `Core.Suggestions` | `SuggestionRequest` | Immutable |
| `Suggestions/SuggestionRequestFactory.cs` | `Core.Suggestions` | `SuggestionRequestFactory` (static) | Pure |
| `Suggestions/SuggestionSession.cs` | `Core.Suggestions` | `SuggestionSession` (record) | Immutable snapshot |
| `Suggestions/SuggestionSessionReconciler.cs` | `Core.Suggestions` | `SuggestionSessionReconciler` (static), `Outcome`, `Result` | Pure; part 1 covered |
| `Suggestions/SuggestionWorkController.cs` | `Core.Suggestions` | `SuggestionWorkController` | **Lock-guarded** (`_gate`, `:31`); owns debounce + CTS chain |
| `Suggestions/SuggestionAvailability.cs` | `Core.Suggestions` | `SuggestionAvailability` (static) | Pure. Single funnel for every availability gate |
| `Suggestions/ILowPowerModeProvider.cs` | `Core.Suggestions` | `ILowPowerModeProvider` | Contract; keeps the OS read out of Core |
| `Suggestions/DoubleTapAcceptanceState.cs` | `Core.Suggestions` | `DoubleTapAcceptanceState`, `SessionToken` | Stateful but only touched from the coordinator's UI post; no internal lock |
| `Suggestions/AcceptanceIntentOutcome.cs` | `Core.Suggestions` | `AcceptanceIntentStatus`, `AcceptanceKeyDisposition`, `AcceptanceIntentOutcome` | Immutable |
| `Suggestions/PerformanceMetricsStore.cs` | `Core.Suggestions` | `PerformanceEntry` (record), `PerformanceMetricsStore` | Lock-guarded; sync IO under lock (see smell S-4) |
| `Suggestions/SuggestionCoordinator.cs` | `Core.Suggestions` | `SuggestionCoordinator` | Hook thread → `Post()` onto the UI `SynchronizationContext`; part 1 covered, dirty-tree diffs in §0 |
| `Support/SentenceBoundaryClassifier.cs` | `Core.Support` | `SentenceBoundaryClassifier` (static) | Pure |
| `Support/CompletionPromptBudget.cs` | `Core.Support` | `CompletionPromptBudget` (static, 2 consts) | Pure |
| `Support/PromptSectionBudget.cs` | `Core.Support` | `SectionTruncation` (enum), `PromptSection` (record), `PromptSectionBudget` (static) | Pure |
| `Support/CompletionPromptBuilder.cs` | `Core.Support` | `CompletionPromptBuilder` (static) | Pure; tokenizer injected |
| `Support/BaseCompletionPromptRenderer.cs` | `Core.Support` | `BaseCompletionPromptRenderer` (static) | Pure |
| `Support/CompletionSeamGuard.cs` | `Core.Support` | `SeamGuardVerdict`, `SpellingAssessment`, `StreamedLeadingVerdict`, `CompletionSeamGuard` | Pure; spell backend injected as `Func<string,SpellingAssessment>` |
| `Support/MidWordContinuationPolicy.cs` | `Core.Support` | `MidWordContinuationPolicy` | Pure |
| `Support/RepetitionGuard.cs` | `Core.Support` | `RepetitionGuard` | Pure |
| `Support/SuggestionTextNormalizer.cs` | `Core.Support` | `SuppressionReason`, `SuggestionTextNormalizer` (partial), `InsertionSafetyGate` | Pure |
| `Support/TrailingDuplicationFilter.cs` | `Core.Support` | `TrailingDuplicationFilter` | Pure |
| `Emoji/EmojiCatalog.cs` | `Core.Emoji` | `EmojiEntry` (record), `EmojiCatalog` | Static readonly list; safe |
| `Emoji/EmojiMatcher.cs` | `Core.Emoji` | `EmojiMatcher` (static) | Pure |
| `Emoji/EmojiTriggerStateMachine.cs` | `Core.Emoji` | `EmojiTriggerStateMachine`, `Outcome` | Stateful, **not** thread-safe by design — owns no IO, driven from one hook callback at a time (`:20-45`) |

### 1.2 `Cotabby.Inference` (`net9.0`, LLamaSharp)

| File | Types | Threading |
| --- | --- | --- |
| `LlamaRuntimeCore.cs` | `LlamaGenerationOptions` (record), `LlamaRuntimeCore` | `SemaphoreSlim _gate(1,1)` (`:45`) serializes every decode; CPU work pushed to `Task.Run` (`:108`). Owns the one persistent llama.cpp context (`:23-32`) |
| `LlamaRuntimeManager.cs` | `LlamaRuntimeManager` | `SemaphoreSlim _gate(1,1)` (`:19`) around load/unload; singleton in DI |
| `LlamaSuggestionEngine.cs` | `ISpellAssessor`, `LlamaSuggestionEngine` | No internal lock — relies on the runtime core's gate. `SpellAssessor` setter is not synchronized (`:161-172`) |
| `ModelDownloader.cs` | `ModelDownloader`, `Progress` (record) | Async HTTP streaming; `HttpClient` is a DI singleton |

Key methods worth naming for the extension work:
- `LlamaRuntimeCore.SupportsFillInMiddle` (`:66-68`) — all three FIM markers present.
- `LlamaRuntimeCore.BuildFimTokens(prefix, suffix, maxPromptTokens)` (`:77-98`) — half the budget to the suffix head, remainder to the prefix tail.
- `LlamaRuntimeCore.ContextSizeTokens` (`:63`) — real allocated context, not a catalog guess.
- `LlamaRuntimeManager` sets `GpuLayerCount = 999` (full Vulkan offload) at `:70` and warms the decode path at `:89`.
- `LlamaSuggestionEngine.GenerateAsync` (`:46-152`) — no token streaming by design (`:27-32`); emits one text chunk then a final empty chunk.

### 1.3 `Cotabby.Win32` (`net9.0-windows`)

| File | Types | Threading |
| --- | --- | --- |
| `Focus/UiaFocusTracker.cs` | `UiaFocusTracker` | `lock (_gate)` for start/stop; `_current` published with `Volatile.Read` (`:37`); `lock (_descriptorGate)` for the new descriptor map (`:393`). Runs UIA on whatever thread calls `Refresh()` |
| `Input/KeyboardHook.cs` | `KeyboardHook` | Dedicated **background STA-free thread** running `GetMessage`/`DispatchMessage` (`:75-113`); delegate pinned at `:46` to prevent GC of the hook proc; `KeyEvent` raised **synchronously on the hook thread** |
| `Insertion/SendInputTextInserter.cs` | `SendInputTextInserter` | `SemaphoreSlim _gate(1,1)` (`:40`); clipboard path marshalled to a fresh **STA thread** (`OnStaThreadAsync`, `:176-188`); `BlockInput(true/false)` on one native thread (`:117-130`) |
| `Interop/NativeMethods.cs` | `NativeMethods` (internal static partial) | All `LibraryImport` (source-generated) except `ToUnicodeEx`, which must stay `DllImport` for `StringBuilder` (`:144-154`). `BlockInput` at `:131-133` |

### 1.4 `Cotabby.App` (`net9.0-windows`, WPF)

| File | Types | Threading / notes |
| --- | --- | --- |
| `App.xaml.cs` | `App` | `OnStartup` (`:26-119`): global mutex single-instance guard (`:37-48`), pre-realizes overlay + emoji popup HWNDs (`:50-62`), wires `EmojiPickerController` onto the **same** hook (`:77-99`), fires model load, welcome wizard, and a `COTABBY_SELF_TEST` headless probe (`:127-302`). Three global exception handlers (`:30-32`). `OnExit` is `async void` (`:342`) |
| `App.xaml` | — | No `StartupUri` — deliberately tray-only/headless |
| `AssemblyInfo.cs` | — | Theme info |
| `Hosting/AppHost.cs` | `AppHost` | DI composition root; singleton graph. `ApplyRuntimeSettings()` (`:139-176`) is the **single settings→runtime push point**. Part 1 covered |
| `Hosting/FileLoggerProvider.cs` | `FileLoggerProvider`, nested `FileLogger` | `lock (_gate)` per line, `AutoFlush = true`, `FileShare.Read`. **No rotation** (see smell S-1) |
| `Settings/AppSettings.cs` | `AppSettings`, `CompletionLengthPreset`, `SettingsStore` | `SettingsStore.Save` uses temp-file + `File.Move(overwrite:true)` (`:176-181`) |
| `Settings/LaunchAtLogin.cs` | `LaunchAtLogin` (static) | HKCU `Run` key; idempotent; falls back to `AppContext.BaseDirectory` guess under `dotnet run` (`:47-66`) |
| `Overlay/GhostOverlayWindow.xaml.cs` | `GhostOverlayWindow : Window, IOverlayPresenter` | All public entry points self-marshal via `Dispatcher.CheckAccess()`/`BeginInvoke` (`:76-80, 111-115, 150-154, 168-172`). Positioned with raw `SetWindowPos` in **physical pixels** (`:293-296`) to dodge WPF's DIP-per-primary-monitor math. Per-session caret-height floor (`:47`, `:194-201`) stops UIA flicker shrinking ghost text mid-completion. `CurrentGhostText` is an `internal` test seam (`:164`) |
| `Overlay/GhostOverlayWindow.xaml` | — | Transparent window markup |
| `Tray/TrayController.cs` | `TrayController : IDisposable` | H.NotifyIcon `TaskbarIcon`, `ForceCreate()` at `:42` (without it a tray-only app never shows an icon). `SetStatus` marshals to `Application.Current.Dispatcher` (`:221-222`). Owns a **Diagnostics** submenu with three live probes (`:99-108, 140-219`) |
| `Emoji/EmojiPickerController.cs` | `EmojiPickerController : IDisposable` | Subscribes to the same `IKeyboardHook` (`:62`). Consumes events only while a trigger is active; **never suppresses** except on commit (`:97`). On commit it backspaces `:query:` via raw `SendInput` then pastes the glyph (`:130-158`, `:160-188`). Refuses credential/secure trigger fields twice (`:81-85`, `:135`). Logs the catalog name, never the typed query (`:148-152`) |
| `Emoji/EmojiPopupWindow.xaml(.cs)` | `EmojiPopupWindow : Window` | Same WS_EX click-through flags as the ghost overlay; self-marshals (`:50-54, 140-144`); positions below the caret so it doesn't fight the ghost text band (`:154-157`) |
| `Metrics/WindowsGpuUsageProvider.cs` | `WindowsGpuUsageProvider : IGpuUsageProviderResettable` | Never throws — every path returns `GpuUsageSample.Unavailable`. Reads the category **once** per sample so paired rate counters stay valid (`:70-75`); keeps per-instance `CounterSample` baselines (`:23-24`) |
| `Power/LowPowerModeMonitor.cs` | `LowPowerModeMonitor : ILowPowerModeProvider` | `GetSystemPowerStatus` P/Invoke (`:69-71`), cached for `RefreshWindowSeconds` (2 s, `:21`). **Reads are unsynchronized** — `_lastPollUtc`/`_lastState` mutated without a lock (`:35-53`); benign in practice (called from the UI thread only) but see smell S-6 |
| `UI/SettingsWindow.xaml.cs` | `SettingsWindow : Window` | Singleton via `ShowOrFocus` (`:135-144`). `_loaded` gate (`:40`) prevents persist-on-seed. **No Save button** — every control commits to `AppHost.Settings` + `PersistSettings()` live. GPU sampling bound to pane-visible-and-window-not-minimized (`:1045-1056`) with epoch-tagged stale-read rejection (`:1123-1130`) |
| `UI/SettingsWindow.xaml` | — | 8 panes; brushes swapped at runtime for light/dark (`ApplyTheme`, `:151-165`); title-bar tint via `DwmSetWindowAttribute` (`:188-203`) |
| `UI/WelcomeWindow.xaml(.cs)` | `WelcomeWindow : Window` | First-run wizard; `ShowFor` static entry (`:81`); writes `UserName`, `Languages`, `AcceptanceMode`, `ActiveModelId`, clears `ShowFirstRunWelcome` (`:229, 242, 320, 563`) |
| `UI/GhostTextPalette.cs` | `GhostTextPalette`, `Choice` (record) | `Resolve` accepts palette ids *or* raw `#RRGGBB` (`:39-56`) — the settings UI stores hex ids so palette revisions stay compatible (`SettingsWindow.xaml.cs:57-58`) |

### 1.5 AppSettings keys — who reads/writes each

`AppHost.ApplyRuntimeSettings()` (`AppHost.cs:139-176`) is the only place settings
become runtime behavior. Reads/writes per key:

| Key | Default | Read by | Written by |
| --- | --- | --- | --- |
| `Enabled` | `true` | `AppHost.cs:141` | Tray `TrayController.cs:226`; Settings `SettingsWindow.xaml.cs:245` |
| `ActiveModelId` | `null` | `AppHost.cs:214-220`, `:190`, `:207` | Tray `:233`; Settings `:555, 568`; Welcome `:320`; `AppHost.ResolveActiveModel` self-heals `:218` |
| `DebounceMs` | `50` | `AppHost.cs:107` (builds `SuggestionWorkController`) | Settings `:949` |
| `LaunchAtLogin` | `false` | `AppHost.cs:175` → `LaunchAtLogin.Set` | Settings `:246`. **Registry is source of truth**: `SettingsWindow.xaml.cs:230` reads `LaunchAtLogin.IsEnabled() \|\| s.LaunchAtLogin` |
| `AllowMultiLine` | `true` | `AppHost.cs:148` → `Coordinator.AllowMultiLineSuggestions` | Settings `:247` |
| `AcceptPunctuationWithWord` | `true` | `AppHost.cs:153` | Settings `:248` |
| `AcceptanceMode` | `"word"` | `AppHost.cs:152` | Settings `:850`; Welcome `:563` |
| `ShowAcceptanceHint` | `true` | `App.xaml.cs:70`; `SettingsWindow.xaml.cs:271` | Settings `:250` |
| `FastMode` | `false` | **nobody** | **nobody** — dead key (S-2) |
| `ShowFirstRunWelcome` | `true` | `App.xaml.cs:106` | Welcome `:242` |
| `GhostTextColor` | `"auto"` | `App.xaml.cs:68`; `SettingsWindow.xaml.cs:271, 279, 314` | Settings swatch click `:314` |
| `GhostTextOpacity` | `1.0` | `App.xaml.cs:69`; `SettingsWindow.xaml.cs:271` | Settings `:261` |
| `DisplayMode` | `"auto"` | **nobody** | **nobody** — dead key (S-2) |
| `UserName` | `""` | `AppHost.cs:154` | Settings `:663` (persist on focus loss, `:668-673`); Welcome `:229` |
| `Languages` | `["English"]` | `AppHost.cs:156` | Settings `:771, 779-781`; Welcome `:438-447` |
| `CompletionLengthPreset` | `"long"` | `AppHost.cs:145` | Tray `:129`; Settings `:653` |
| `AcceptWordKey` | `"Tab"` | **nobody** (only asserted by `DoubleTapSettingsTests`) | **nobody** — vestigial (S-2) |
| `AcceptSuggestionKey` | `"Backtick"` | `AppHost.cs:149-151` | Settings `:842` |
| `GlobalToggleKey` | `""` | **nobody** | **nobody** — dead key (S-2) |
| `BlockedApps` | `[]` | `AppHost.cs:163-170` → `Coordinator.BlockedApps` | Settings `:901, 927` |
| `EmojiPickerEnabled` | `true` | `AppHost.cs:160`; `App.xaml.cs:92` | Settings `:249` |
| `PauseInLowPowerMode` | `false` | `AppHost.cs:143` | Settings `:251` |
| `EnablePerformanceTracking` | `false` | `AppHost.cs:120`; `SettingsWindow.xaml.cs:983, 1002` | Settings `:1001` |

---

## 2. Feature matrix

Legend: **implemented** · **partial** · **untested** (code exists, no test) ·
**absent** · **better** (implemented better than the TabType comparison point).

| Feature | Status | Evidence |
| --- | --- | --- |
| Word-by-word acceptance | **implemented** | `SuggestionSessionReconciler.NextAcceptanceChunk` `:84-113`; wired at `SuggestionCoordinator.cs:979`; `AcceptPunctuationWithWord` honored at `:979-980`. Tests: `SuggestionSessionReconcilerTests.cs` (8) |
| Phrase acceptance (whole sentence per press) | **implemented** | `NextAcceptancePhrase` `:115-140`; `AcceptanceGranularity` `SuggestionCoordinator.cs:195`, set from settings `AppHost.cs:152`. Tests: `SuggestionSessionReconcilerTests.cs` |
| Whole-suggestion acceptance (backtick/tilde/backslash) | **implemented** | `AlternateAcceptKey` `SuggestionCoordinator.cs:188`, parsed `AppHost.ParseAlternateAcceptKey :236-248`. Tests: `DoubleTapSettingsTests.cs` (5) |
| Double-tap Tab full acceptance | **implemented** | `DoubleTapAcceptanceState.cs` (0.3 s window, `:11`); `SuggestionCoordinator.DoubleTapFullAcceptanceEnabled :202`. Tests: `DoubleTapAcceptanceStateTests.cs` (4), `SuggestionCoordinatorDoubleTapTests.cs` (23) |
| Type-through (keep typing while a suggestion is visible) | **implemented — better** | The suggestion is *reconciled* rather than merely dismissed: `SuggestionSessionReconciler.Apply :35` returns `Keep/Shrink/Invalidate`, and `ConsumedChars` tracking (`SuggestionSession.cs:31`) means the visible text follows the caret. Because `SuggestionWorkController.Submit` cancels the prior request on every keystroke (`:41-58`), a generation the next keystroke invalidated is never shown. Tests: `SuggestionSessionReconcilerTests.cs`, `SuggestionCoordinatorDoubleTapTests.cs` |
| Fill-in-the-middle (FIM) | **implemented** | `LlamaRuntimeCore.BuildFimTokens :77-98`; gated on `SupportsFillInMiddle && HasMeaningfulSuffix` `LlamaSuggestionEngine.cs:71-86`. **Model-conditional**: only fires when the loaded model exposes all three infill markers (`:66-68`) and real text follows the caret on the same line |
| Persistent inference context | **implemented** | One long-lived `LlamaRuntimeCore` singleton owning one llama.cpp context (`LlamaRuntimeCore.cs:23-32, 38-50`); `LlamaRuntimeManager` `:38-45` holds it for the process lifetime |
| Prefix reuse / prediction continuation | **partial — better in mechanism, untested as a metric** | Mechanism is present and explicit: persistent context so KV state is transformer-reusable (`LlamaRuntimeCore.cs:56`), plus an explicit *session-exhaustion continuation* that immediately re-triggers when a suggestion is fully consumed (`SuggestionCoordinator.cs:1224-1228`). There is **no prefix-cache hit-rate measurement or prefix-hash reuse key**, and no test asserting continuation actually fires. The dirty-tree change at `:1224-1228` is itself currently only covered indirectly |
| Emoji autocomplete (`:smile:` → 😄) | **implemented** | `EmojiTriggerStateMachine.cs`, `EmojiMatcher.cs`, `EmojiCatalog.cs` (~180 entries), `EmojiPickerController.cs`, `EmojiPopupWindow`. Tests: `EmojiTriggerStateMachineTests.cs` (11), `EmojiMatcherTests.cs` (8) |
| Emoji: credential-field refusal | **implemented** | Refuses to *start* in secure/credential fields `EmojiPickerController.cs:81-85`; re-checks before commit `:135` |
| Emoji: no typed-query logging | **implemented** | Logs catalog name only `EmojiPickerController.cs:148-152` (comment at `:148-149` states the rule) |
| Per-app blocklist | **implemented** | `SuggestionAvailability.ShouldRequest` `blockedApps` param `:41-46`; `Coordinator.BlockedApps :157`; case-insensitive `HashSet` built in `AppHost.cs:163-170`; UI add/remove `SettingsWindow.xaml.cs:917-930, 899-904`. Tests: `SuggestionAvailabilityTests.cs` (9) |
| Credential-field protection | **implemented — better** | Guard runs **before any payload read**: `UiaFocusTracker.cs:164-167` (`isPassword` + detector evaluated ahead of TextPattern/ValuePattern). Single funnel `SuggestionAvailability.cs:30-33` refuses `IsSecure`/`IsCredentialField` for every caller. `FocusedField.IsCredentialField` documented as a hard contract (`FocusedField.cs:47-53`). **Real-UIA proof harness** `tools/Cotabby.Tools.CredentialSmoke` drives the production tracker against a synthetic fixture process with a payload-read spy, negative control, and eligibility control (`Program.cs:116-404`, case table `:1105-1153`). Tests: `CredentialFieldDetectorTests.cs` (14), `SuggestionAvailabilityCredentialTests.cs` (3) |
| Own-window self-exclusion | **implemented** | `SuggestionAvailability.cs:36-39` (`field.ProcessId == Environment.ProcessId`) |
| Low Power Mode pause | **implemented** | `SuggestionAvailability.ShouldPauseForLowPowerMode :62-63` (both halves required); `Coordinator.LowPowerModePause :164`, gate `:449-453`; `LowPowerModeMonitor.cs`; wired `AppHost.cs:143`. Tests: `LowPowerModeGateTests.cs` (2) |
| Seam-completion guard (junk runs / misspelling at the caret seam) | **implemented — better** | `CompletionSeamGuard.cs` (337 lines, pure, pluggable spell backend); applied as the last gate before display `LlamaSuggestionEngine.cs:129-142`. Fail-open on unknown vocabulary, suppresses only *correctable* typos. Tests: `CompletionSeamGuardTests.cs` (30) — the largest single test file |
| Vulkan GPU offload | **implemented** | `GpuLayerCount = 999` (all layers) `LlamaRuntimeManager.cs:67-70`; decode-path warm-up at `:83-89` to avoid per-keystroke pipeline compilation |
| Performance pane (latency table) | **implemented** | `PerformanceMetricsStore` (bounded 200, `:25`), recorded at `SuggestionCoordinator.cs:567`; UI `SettingsWindow.xaml.cs:971-1153`. Tests: `PerformanceMetricsStoreTests.cs` (14), `SuggestionPerformanceMetricsTests.cs` (5) |
| Performance pane (live GPU readout) | **implemented — better** | Sampling only while the pane is on screen in a visible non-minimized window (`:1045-1056`); epoch-tagged stale reads rejected (`:1123-1130`); availability inferred from *returned data*, never from a category probe (`:1137-1139`, `PerformanceSamplingSession.cs:158-166`). Tests: `PerformanceSamplingSessionTests.cs` (11), `GpuCounterSamplingTests.cs` (5), `MetricsAggregatorTests.cs` (11), `PerformancePaneMarkupTests.cs` (1) |
| Keyboard hook pipeline | **implemented** | Dedicated thread + message loop `KeyboardHook.cs:75-113`; injected events dropped unless `COTABBY_ALLOW_INJECTED=1` `:37, 175`; `FireSyntheticKey` test seam `:159`. Tests: `KeyboardHookInteropTests.cs` (1, source-contract only) |
| SendInput insertion (Unicode path) | **implemented** | `SendUnicodeString` `:228-237`, chunks capped at 24 chars `:52`. Real-host readback proof: `tools/Cotabby.Tools.RegressionSmoke/Program.cs:79-87` |
| Clipboard insertion | **implemented — better** | Full clipboard **preservation** including non-text formats (`SendInputTextInserter.cs:94-110, 136-145`); verified against a rich `DataObject` and an originally-empty clipboard (`RegressionSmoke/Program.cs:88-109`) |
| Stale-target insertion refusal | **implemented** | `TargetStillFocused` `SendInputTextInserter.cs:153-174`; proven `RegressionSmoke/Program.cs:70-73` |
| Post-dispatch cancellation accounting | **implemented** | `RegressionSmoke/Program.cs:110-118` |
| Overlay UI (ghost text) | **implemented — better** | DPI-correct multi-monitor placement via raw `SetWindowPos` + `MonitorFromPoint` (`GhostOverlayWindow.xaml.cs:264-321`); caret-height-derived font size with per-session floor (`:186-218`); light/dark tracking (`:220-262`); user color/opacity override (`:74-91`) |
| Single-instance guard | **implemented** | `Global\Cotabby.SingleInstance.{guid}` mutex `App.xaml.cs:37-48` |
| Launch at login | **implemented** | `LaunchAtLogin.cs` |
| Secret sanitizer (log scrubbing) | **absent** | No sanitizer exists. Privacy is instead enforced *structurally*: credential text never enters a request, and `CredentialSmoke` never prints payloads (`Program.cs:11-13`). See §4.6 |
| Log rotation | **absent** | `FileLoggerProvider.cs:6-13` explicitly documents "no rotation"; fixed path `C:\tmp\cotabby-live.log` |
| Statistics funnel (shown/accepted/rejected) | **absent** | Only latency is recorded (`PerformanceMetricsStore`). Every funnel transition exists as a log line — see §4.1 |
| Beam search / alternatives popup | **absent** | Greedy decode only (`LlamaRuntimeCore`, `TopK=20` at `:19`) |

---

## 3. Test inventory

### 3.1 By area — `tests/Cotabby.Core.Tests` (28 files, 317 of the 319)

| Area | File | Tests | What it pins |
| --- | --- | --- | --- |
| Seam guard | `CompletionSeamGuardTests.cs` | 30 | Junk runs, mid-word splice, leading-word probe, CJK/digit/name exemptions, streaming verdicts |
| Coordinator / double-tap | `SuggestionCoordinatorDoubleTapTests.cs` | 23 | Ownership, double-tap window, Shift+Tab, **key-diagnostics never record typed character codes** (`:13-20`) |
| Prompt rendering | `BaseCompletionPromptRendererTests.cs` | 18 | Persona/language preface, whitespace, exhausted budget must not restore raw prefix |
| Metrics persistence | `PerformanceMetricsStoreTests.cs` | 14 | Off-by-default, bound at 200, corrupt-file tolerance |
| Credential detection | `CredentialFieldDetectorTests.cs` | 14 | Google sign-in, username/OTP/card labels, address-shaped text, prose exemptions; Windows Edit/ComboBox/Document vocabulary (`:5-9`) |
| Degeneration guards | `DegenerationGuardSmokeTests.cs` | 12 | `RepetitionGuard` 3-gram blocking + normalizer suppression |
| GPU sampling | `PerformanceSamplingSessionTests.cs` | 11 | Single-flight, epoch staleness, reset-before-first-read, `Stop` never blocks (deterministic gate, no sleeps) |
| GPU aggregation | `MetricsAggregatorTests.cs` | 11 | Instance-name parsing, busiest-adapter arithmetic, malformed instances |
| Emoji trigger | `EmojiTriggerStateMachineTests.cs` | 11 | Colon start/extend/commit, cancel rules, modifier rejection |
| Token budgeting | `TokenBudgetedPromptTests.cs` | 10 | Zero/negative budget → empty; counter required |
| Availability | `SuggestionAvailabilityTests.cs` | 9 | Empty/secure/self/blocklist/modifier gates |
| Reconciliation | `SuggestionSessionReconcilerTests.cs` | 8 | Keep/shrink/invalidate, chunk vs phrase |
| Emoji matching | `EmojiMatcherTests.cs` | 8 | Exact > prefix > substring, alias scoring, `:smile` beats `:smile_eyes` |
| Request building | `SuggestionRequestFactoryTests.cs` | 6 | Prefix/suffix windows, token clamps |
| Perf metrics wiring | `SuggestionPerformanceMetricsTests.cs` | 5 | Nothing recorded when tracking off or output cancelled |
| Shift+Tab | `ShiftTabTests.cs` | 5 | Distinct binding; passes through, inserts nothing, doesn't disturb a pending pair |
| GPU counters | `GpuCounterSamplingTests.cs` | 5 | Utilization/memory availability independence |
| Field identity | `FieldIdentityTests.cs` | 5 | "No identity" is UNPROVEN, never unowned (review finding F3) |
| Double-tap settings | `DoubleTapSettingsTests.cs` | 5 | Legacy/missing JSON keeps Backtick default; choices round-trip |
| Double-tap state | `DoubleTapAcceptanceStateTests.cs` | 4 | Window boundary, repeat suppression, cross-session |
| Debounce | `SuggestionWorkControllerTests.cs` | 3 | Runs after debounce, cancels prior |
| Credential gate | `SuggestionAvailabilityCredentialTests.cs` | 3 | One policy flag, every caller |
| Rapid Tab | `RapidTabAcceptanceTests.cs` | 2 | Held-presentation authorization (#838) |
| Low power gate | `LowPowerModeGateTests.cs` | 2 | Both halves required |
| Prompt budget | `CompletionPromptBudgetTests.cs` | 2 | Whole-output reserve + safety margin, `int.MaxValue` overflow |
| Pane markup | `PerformancePaneMarkupTests.cs` | 1 | GPU readouts in a separate column (source-wiring check, explicitly not live-rendering proof) |
| Hook interop | `KeyboardHookInteropTests.cs` | 1 | Asserts `PostThreadMessageW` binding via regex over `NativeMethods.cs` source |
| Prompt builder | `CompletionPromptBuilderTests.cs` | 1 | Rendered prompt + output + margin fits the context |

### 3.2 Live/behavioural harnesses — `tools/` (6 projects, **not** in the unit suite)

| Tool | What it proves | Runs on |
| --- | --- | --- |
| `CredentialSmoke` | **Real UIA**: production `UiaFocusTracker.Refresh()` refuses credential fields with **zero payload reads**, while ordinary/email/search/document fields stay available. Negative control + eligibility control per case; `SKIP` (exit 4) when UIA focus is unavailable rather than reporting a false failure (`Program.cs:60-63, 185-191`) | Windows |
| `RegressionSmoke` | Real settings window interaction, **production** `SendInputTextInserter` against a real WPF `TextBox`: Unicode + clipboard readback, rich-clipboard preservation, empty-clipboard restore, post-dispatch cancellation, stale-target refusal (`Program.cs:33-121`) | Windows |
| `DoubleTapSmoke` | 9 live double-tab cases incl. delayed dispatcher, stale snapshot, same-process control change, Shift+Tab (`Program.cs:38-48`) | Windows |
| `CoordinatorSmoke` | Real `LlamaSuggestionEngine` + real coordinator with faked hook/focus; realistic typing cadence, window-switch mid-generation, repeated session cycles (`Program.cs:60-286`) | Windows |
| `InferenceSmoke` | Downloads/loads a model and prints a completion with throughput (`Program.cs`) | Windows |
| `GpuSmoke` | Windows open-temp negative control for metrics persistence, paired-counter calculation, adapter-safe read; **explicitly labels physical-GPU accuracy "Not tested; owner hardware validation required"** (`Program.cs:48-56`) | Windows |

Plus an in-app self-test: `COTABBY_SELF_TEST=1` runs `App.RunSelfTestAsync`
(`App.xaml.cs:127-302`) — three probes (DI engine, fresh runtime, synthetic typing
through the coordinator) tracing to `C:\tmp\cotabby-selftest.txt`.

### 3.3 `tests/Cotabby.Win32.Tests` — 2 tests, **untracked, not verified here**

`FieldIdentityCollisionTests.cs` (2 tests) pins the new
`FallbackIdentityFromDescriptor` seam via `InternalsVisibleTo`: identical
descriptors are refused for later fields, distinct descriptors stay usable.
Targets `net9.0-windows`, so **it cannot run on this Linux host** — I could not
verify it. It is also **untracked and only registered in the dirty `Cotabby.sln`**,
so a CI job that builds a clean checkout would silently skip it. See smell S-3.

---

## 4. Extension seams

Where each proposed extension integrates. All are additive; none requires
changing an existing contract.

### 4.1 Statistics funnel (shown / accepted / rejected)

The aggregation does not exist, but every transition already has a log line — each
gains a parallel counter bump, no new IO on the hot path:

| Funnel stage | Exact site |
| --- | --- |
| Field passed availability | `SuggestionCoordinator.cs:469` (`"Triggering suggestion"`) |
| Request scheduled | `:515` (`"Scheduled request"`) |
| Engine fired post-debounce | `:524` (`"Firing request"`) |
| Overlay shown | `:662` (`"Overlay shown"`) |
| Request finished | `:692` (`"Request … finished"`) |
| Session cancelled | `:1240` (`"Cancel session"`) |
| Acceptance outcome | event `AcceptanceIntentCompleted` `:128`, raised `:1195-1196` |
| Low-power suppression | `:452` |
| Credential/availability suppression | `:455-460` |

Two natural integration shapes:
- **New store mirroring `PerformanceMetricsStore`** — copy its exact contract
  (`PerformanceMetricsStore.cs:8-25`: opt-in `TrackingEnabled`, bounded
  `MaximumEntries = 200`, `%APPDATA%\Cotabby\*.json`, corrupt-file tolerance).
  Attach via the existing nullable-property pattern:
  `Coordinator.PerformanceMetrics` `:122` → add `Coordinator.SuggestionStatistics`.
  Surface in the Performance pane beside `RefreshPerformanceLog`
  (`SettingsWindow.xaml.cs:1013-1026`) with a Clear button mirroring
  `OnClearPerformanceLog` (`:1007-1011`).
- **Or subscribe to the existing event** `AcceptanceIntentCompleted` (`:128`) for
  the acceptance half, plus a counter at `:469` for the trigger half. The event
  carries `AcceptanceIntentOutcome` (`AcceptanceIntentOutcome.cs:47`) with
  `AcceptanceIntentStatus` — zero coordinator change needed for accepted counts.

**Blocking smell:** `PerformanceMetricsStore.Record` does synchronous file IO under
a lock on the coordinator's UI post (S-4). A second store on the same pattern
doubles that cost — make the persistence async/batched first.

### 4.2 Battery monitor (battery-aware inference)

Already **half-built**, and the seam is exactly right:
- Pure gate: `SuggestionAvailability.ShouldPauseForLowPowerMode(bool, bool)`
  `:62-63`.
- Platform adapter interface: `ILowPowerModeProvider` (`ILowPowerModeProvider.cs:9-13`).
- Windows implementation: `LowPowerModeMonitor` (`Power/LowPowerModeMonitor.cs`).
- Coordinator injection point: `Coordinator.LowPowerModePause` `:164`, evaluated at
  `:449-453`; bound in `AppHost.cs:143` behind `Settings.PauseInLowPowerMode`.

To extend from "pause on energy saver" to "degrade on battery" (e.g. drop
`MaxTokens`, skip GPU offload, lengthen debounce), the same gate site `:449` is
the place: it already has the field in hand and runs before any request is built.
`SYSTEM_POWER_STATUS.ACLineStatus`/`BatteryFlag`/`BatteryLifePercent` are already
marshalled in `LowPowerModeMonitor.cs:56-67` and simply unused — the adapter is
one property away from exposing charge level without a new P/Invoke.
**Blocking smell:** unsynchronized cache fields (S-6).

### 4.3 Slash-macro preprocessor

The clean insertion point is **`SuggestionRequestFactory.Build`**
(`SuggestionRequestFactory.cs:31-57`) — it is pure, has no IO, takes the focused
field, and already produces exactly the `Prefix`/`Suffix`/`UserName`/
`LanguageInstruction` tuple a macro expansion would rewrite. It is called once per
request at `SuggestionCoordinator.cs:499-501` with the prefix captured
synchronously at trigger time.

Caveat that shapes the design: the request prefix is captured **before** the
debounce (`:491-501`), and `SuggestionWorkController.Submit` cancels the prior
request on the next keystroke (`:41-58`). A macro that expands as the user types
would therefore be re-expanded per keystroke; expanding on a terminator key
(slash-command commit) rather than per-character is the safer fit.
The pure-string precedent to follow is `CompletionSeamGuard` — pure function +
injected backend, 30 tests, no IO.

### 4.4 Log rotator

Exactly one seam: `FileLoggerProvider` (`Hosting/FileLoggerProvider.cs`). It is
already the sole file sink, registered once at `AppHost.cs:75`. Rotation belongs
in `FileLogger.Log` (`:57-68`) behind the existing `_gate` lock — the class
already serializes every line through one lock and `AutoFlush = true`, so a
size check + roll is a contained change. Two things to fix while there:
- The path is hardcoded twice as a literal: `AppHost.cs:75` and
  `SettingsWindow.xaml.cs:940, 957` (and the AppSmoke default `:21`). A rotator
  needs one shared constant.
- `IsEnabled` is `logLevel >= LogLevel.Information` (`:64`) after the dirty-tree
  change; a verbosity setting needs a mutable level, which `FileLogger` currently
  cannot receive (level is read per-call from a `readonly`-ish expression, and
  `FileLogger` instances are created once in `CreateLogger` `:30`).

### 4.5 Secret sanitizer

**Do not add one yet — the codebase already prevents the leak structurally**, and
that is the stronger property:
1. Credential text never enters a `SuggestionRequest` (`SuggestionAvailability.cs:30-33`
   refuses the field for every caller; `UiaFocusTracker.cs:164-167` refuses it
   *before* any payload read).
2. `CredentialSmoke` proves zero payload reads against real UIA with a read spy
   (`Program.cs:289-404`).
3. Key diagnostics deliberately never record typed character codes — pinned by a
   test (`SuggestionCoordinatorDoubleTapTests.cs:13-20`).
4. The emoji controller logs the catalog name, not the typed query
   (`EmojiPickerController.cs:148-152`).

What the log *does* contain today is **ordinary user prose**: `SuggestionCoordinator`
logs full suggestion text (`:662`, `:692`), and `LlamaSuggestionEngine` logs raw
generations (`:118-121, 137-141`). Those are the sanitizer's real targets, and the
single chokepoint is `FileLogger.Log`'s `formatter(state, exception)` call
(`FileLoggerProvider.cs:62`) — one place to scrub, covering every category.
Note the honest limit: `FileLoggerProvider` was just made fail-soft
(`:22-37`), so if the sink is disabled there is nothing to sanitize; conversely a
sanitizer placed at the provider only protects the *file* sink — the Debug and
Console sinks registered at `AppHost.cs:66-71` bypass it.

### 4.6 Richer `SuggestionAvailability`

`SuggestionAvailability.ShouldRequest` (`SuggestionAvailability.cs:23-52`) is the
right and only chokepoint — every caller funnels through it. Two constraints:

1. **Its `KeyboardEvent` parameter is load-bearing.** The dirty-tree change
   (`SuggestionCoordinator.cs:1131-1140, 1227-1231`) synthesizes a
   `ContinuationKeyEvent` — a neutral printable key carrying field, modifiers and
   process — specifically so the exhaustion continuation path can reuse the same
   gate. Any new rule must be derivable from `(FocusedField, KeyboardEvent,
   blockedApps)` or it will not apply on that path.
2. **It must stay pure.** It is called on the coordinator's UI post before a
   request is built (`:455`), and is directly unit-tested (12 tests across
   `SuggestionAvailabilityTests` + `SuggestionAvailabilityCredentialTests`).
   Anything needing IO (a profile lookup, a time-of-day rule) belongs behind an
   injected interface like `ILowPowerModeProvider`, not inline.

Concretely, richer suppression would add: per-app *profiles* (currently a flat
`ISet<string>` blocklist, `:41-46`), a time/idle rule, and a field-shape rule —
the latter already has a pure precedent in `CredentialFieldDetector`
(`Focus/CredentialFieldDetector.cs`, 14 tests).

---

## 5. Code smells blocking these extensions

Ranked by how directly each blocks the work above.

**S-1 — Log rotation is explicitly unimplemented and the path is triplicated.**
`FileLoggerProvider.cs:6-13` documents "no rotation"; `AppHost.cs:75` and
`SettingsWindow.xaml.cs:940, 957` hardcode `C:\tmp\cotabby-live.log` as separate
literals (AppSmoke defaults it again at `Program.cs:21`). *Blocks §4.4.*
Fix: one `CotabbyLogPaths` constant; rotate inside `FileLogger.Log` under `_gate`.

**S-2 — Four dead `AppSettings` keys.** `FastMode`, `DisplayMode`,
`GlobalToggleKey`, `AcceptWordKey` are serialized (`AppSettings.cs:33, 47, 71, 63`)
and read by **no** production code (verified by grep across `src/`).
`AcceptWordKey` is only asserted by a test
(`DoubleTapSettingsTests.cs:16-17`), so it looks live to a reader. Users can set
values that silently do nothing, and any future per-app-profile work (§4.6) has to
decide whether these are the intended carriers. Fix: delete, or wire + test.

**S-3 — `Cotabby.Win32.Tests` is untracked and only in the dirty solution file.**
`tests/Cotabby.Win32.Tests/` is untracked; its only registration is the modified
`Cotabby.sln` (guid `49B65E75-…` in the diff vs `B020F5F5-…` at HEAD — the file was
edited twice). A clean-checkout CI build omits the project, and the
`InternalsVisibleTo` seam it depends on (`Cotabby.Win32.csproj:24-28`, also
uncommitted) would be dead weight. It also targets `net9.0-windows` and could not
be executed here, so its 2 tests are **unverified**. Fix: commit both together.

**S-4 — Synchronous file IO under a lock on the coordinator's UI post.**
`PerformanceMetricsStore.Record` writes JSON synchronously; called from
`SuggestionCoordinator.cs:567` on the UI context. `SettingsStore.Save` is likewise
synchronous (`AppSettings.cs:174-182`) and is invoked on **every** settings change
(`SettingsWindow.xaml.cs` calls `PersistSettings()` ~20 times, e.g. `:251, 262,
316, 655, 772, 928`). *Blocks §4.1* (a second store doubles the cost) and makes
slider drags do disk IO per tick (`OnOpacityChanged :255-264`,
`OnDebounceChanged :943-951`). Fix: batch/async the stores; coalesce
persist-on-change.

**S-5 — `Post()` swallows dispatcher failures with only a warning.**
The dirty-tree change wrapped `_ui.Post` in try/catch
(`SuggestionCoordinator.cs:1387-1399`) so a dead dispatcher can't throw on the hook
thread. Correct for robustness, but it means **silent feature loss**: if the
dispatcher is gone, suggestions stop with a log line and no user-visible state.
Combined with the new 2 s `DispatchFailed` path (`:865-899`), there are now two
independent "the UI is gone" outcomes with no shared signal. A statistics funnel
(§4.1) should count them, otherwise they are invisible.

**S-6 — `LowPowerModeMonitor` cache fields are unsynchronized.**
`_refreshWindow`/`_lastPollUtc`/`_lastState` are read and written with no lock and
no `volatile` (`Power/LowPowerModeMonitor.cs:35-53`), and `RefreshWindowSeconds`
is a mutable public setter (`:26-30`). Currently only touched from the UI thread,
so it is benign — but it becomes a real race the moment a battery-aware
degradation path (§4.2) polls it from a background thread. Fix: `volatile`/`lock`,
or freeze the window after construction.

**S-7 — `ISpellAssessor` is declared but never implemented or injected.**
`LlamaSuggestionEngine.cs:14-18` defines it; `SpellAssessor` (`:161-172`) is the
setter; **no production code assigns it** (grep across `src/` finds only the
declaration and the property). So `CompletionSeamGuard`'s spelling half — the
`SeamMisspelling` and `LeadingWordMisspelling` verdicts — is **inert in
production**: the default `_ => SpellingAssessment.Known` (`:157`) makes every word
"known", and the guard silently degrades to junk-run-only. The 30
`CompletionSeamGuardTests` pass by injecting a fake assessor, so tests cannot catch
this. The `#814` port is therefore only partially live. Fix: implement a Windows
spell backend (or document the degradation and gate the setting on availability).

**S-8 — Duplicated Win32 interop across three files.**
`GhostOverlayWindow.xaml.cs:299-379`, `EmojiPopupWindow.xaml.cs:148-207` and
`Win32/Interop/NativeMethods.cs` each redeclare `POINT`/`RECT`/`MONITORINFO`,
`MonitorFromPoint`, `GetMonitorInfoW`, `SetWindowLongW`, `SetWindowPos`. The
overlay even carries a comment explaining the `GetMonitorInfoW` suffix trap
(`:343-347`) that the second copy (`EmojiPopupWindow.xaml.cs:195`) repeats without
the explanation. `Cotabby.App` cannot see `NativeMethods` (it is `internal` to
`Cotabby.Win32`), which is why this exists — but any new App-layer Win32 call will
add a fourth copy. Fix: promote the shared structs/imports to an internal-visible
App-side interop file.

**S-9 — Two "is the system in dark mode" implementations, inverted semantics.**
`GhostOverlayWindow.IsSystemInDarkMode` (`:252-262`) and
`EmojiPopupWindow.IsSystemInDarkMode` (`:127-136`) return `v == 0`, while
`SettingsWindow.IsSystemLight` (`:174-186`) returns `v != 0` — same registry key,
opposite polarity, three copies. A theme bug in one will not show up in the others.

**S-10 — `EmojiPickerController` hand-rolls `SendInput` structs instead of reusing
`SendInputTextInserter`/`NativeMethods`.** It declares its own
`INPUT`/`INPUTUNION`/`KEYBDINPUT`/`MOUSEINPUT`/`HARDWAREINPUT` and a `DllImport`
`SendInput` (`EmojiPickerController.cs:190-217`) purely to emit backspaces, with a
comment acknowledging the missing primitive (`:139-143`). This duplicates the
insertion layer's interop and its `BlockInput` safety property is *not* applied to
the backspace burst. Fix: add a `DeleteCharsAsync` to `ITextInserter`.

**S-11 — `App.OnExit` is `async void` and the mutex is released by a lambda.**
`App.xaml.cs:342-353` awaits host disposal in `async void OnExit`; the
single-instance mutex is released from an `Exit +=` lambda (`:48`) that does not
await anything, so shutdown ordering between the mutex release and async teardown
is not guaranteed. Minor, but it is the kind of thing that makes a second launch
race during shutdown.

**S-12 — Self-test writes to a hardcoded `C:\tmp` path with swallowed failures.**
`App.xaml.cs:131-133` writes `C:\tmp\cotabby-selftest.txt`; every write is in a
`try {} catch {}` that discards the error, so a full disk or missing directory
makes the self-test report nothing while appearing to run. Same class of issue as
S-1 (hardcoded temp paths) but with a worse failure mode.

---

## 6. Verdict for implementation planning

**Safe to build on:** `SuggestionAvailability` (single pure chokepoint),
`SuggestionRequestFactory.Build` (pure request seam), `PerformanceMetricsStore`
(proven bounded-persistence pattern), `ILowPowerModeProvider` (proven
platform-adapter pattern), `CompletionSeamGuard` (proven pure-guard +
injected-backend pattern), and the coordinator's log lines / `AcceptanceIntentCompleted`
event (every funnel transition already observable).

**Fix before or alongside:** S-3 (commit the Win32 test project — its seam is
already uncommitted), S-4 (async the stores before adding a second one), S-7
(decide whether the seam guard's spelling half ships or is documented as
degraded), S-2 (resolve the four dead settings keys before per-app profiles
reuse that surface).

**Not needed:** a secret sanitizer as a *new* control — §4.5 explains the leak is
already prevented structurally; the only genuine target is ordinary prose in
existing log lines, reachable at one chokepoint.

**Unverified:** everything in §3.2/§3.3 is Windows-only and was **not executed**
on this host. The 319 passing tests are the `net9.0` Core suite only. No claim
here rests on a Windows run.
