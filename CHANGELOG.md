# Changelog

## Unreleased — TabType Phase 2

- Selectable insertion strategies: global Auto, Unicode and
  Clipboard choices with default transport behavior preserved. UIA text writes
  are explicitly deferred because TextPattern has no setter and whole-value
  replacement is not safe caret insertion. Malformed strategy JSON cannot reset
  unrelated safety settings. Exact verification is recorded in the feature PR;
  no release/live acceptance is implied. [Scope](docs/tabtype-insertion-strategies.md).

- Per-app suggestion profiles: nullable behavior overrides with global safety
  gates retained, an Apps-pane editor, and stale-target retirement. Verification:
  Core/Win32 suites and Windows real-window settings/insertion/privacy
  proofs passed. Independent review completed; the missing-caption resource
  and malformed-profile whole-settings reset were fixed with regressions.
  Not published or owner live-accepted.
  See [contract and verification gates](docs/tabtype-per-app-profiles.md).

## 0.6.0 — 2026-10-17

TabType Phase 1 (from this fork's own `openspec/audit/` plan), ported onto the
Windows architecture. [Scope and limits](docs/tabtype-phase1.md).

### Added

- **Bounded, sanitized diagnostics.** `C:\tmp\cotabby-live.log` rotates at
  8 MiB and keeps three backups; every file, console and debugger line is
  passed through the secret sanitizer; verbosity (Information / Debug /
  Warning / Error) is a live setting in Advanced. Rotation, an unwritable
  path and oversized records all fail soft.
- **Extra field protection** (opt-in, Settings → Apps): exact
  password-manager process defaults (1Password, KeePass, KeePassXC,
  Bitwarden, Enpass, Dashlane), a transient mixed-character pause in plain
  single-line fields, and four more credential labels.
- **Battery-aware inference** (opt-in, Settings → Advanced): throttles
  debounce and completion budget on battery, defers new requests below the
  configurable threshold with hysteresis, and resolves to normal behavior for
  AC, charging, desktops and unreadable power state.
- **Suggestion statistics** (opt-in, Settings → Performance): in-memory,
  count-only funnel with refresh and clear. No text, identifiers, timestamps,
  files or telemetry.

### Fixed

- `SYSTEM_POWER_STATUS` was declared 16 bytes with the energy-saver byte after
  `BatteryFullLifeTime`, so it was read at offset 12 instead of 3 and the
  Low Power Mode pause could never engage. The struct now matches the real
  12-byte layout and a test pins the size and offset.
- The Windows verification workflow now runs `tests/Cotabby.Win32.Tests`, so
  the field-identity regressions execute; previously that project was built
  but never tested.
- Two synthetic secret fixtures were repaired after their values lost their
  provider shape, which had been failing the sanitizer suite.

### Scope and verification

- 418 Core tests pass; Windows-targeted App and regression-tool builds are
  clean. The real Settings window is exercised on Windows CI for every new
  control.
- Nothing from upstream that was previously declined is imported here: no
  typing history, no cloud endpoint, no clipboard context, no telemetry.
- Owner live validation still pending: physical keys, real model output, a
  real battery/energy-saver transition, and browser credential-metadata
  recall. The entropy pause can also delay suggestions over code.

## 0.5.1 — 2026-10-16

### Fixed

- Selectively port model-download hardening from Windows upstream
  [Msde-7/cotabby-windows e625947](https://github.com/Msde-7/cotabby-windows/commit/e625947):
  restart rather than append when a server ignores HTTP Range; reject truncated
  responses and non-GGUF downloads; replace invalid existing final files.
- Validate 206 Content-Range offsets and total length before resuming. Promote a
  416 partial only when the server's explicit total equals the local file size.
- Remove download error-body and redirected-URL logging.

### Scope and verification

- 14 deterministic downloader cases supplement the existing 319 tests. Hosted
  Windows workflows also build, publish and exercise existing synthetic runtime
  proofs. See [download safety notes](docs/upstream-download-safety.md).
- No cloud endpoint, clipboard-context, typing-history or UI rewrite imported.
  All earlier v0.5.0 behavior remains.
- GGUF magic and size checks are sanity checks, not cryptographic validation.
  Existing caches retain the historical 85% size tolerance. A changed remote
  model during resumption cannot be detected without a trusted checksum/validator.
  Live Hugging Face downloads and real-model suggestions need owner testing.

## 0.5.0 — 2026-10-15

Upstream port round three from FuJacob/cotabby, merged into main as PR #6 and
shipped cumulatively with all five earlier ports.

### Added

- Port upstream [correctable-misspelling guard ([#814](https://github.com/FuJacob/cotabby/commit/ac2699e4ab40b68cf7429b7d13d5a7206c99dbbc))](docs/upstream-seam-guard.md):
  a pure post-generation `CompletionSeamGuard` now refuses three classes of
  visible glitch before display — junk punctuation runs ("....", "$$$$"),
  mid-word splices that turn the word being typed into a misspelling
  ("gre" + "atful"), and, when the completion starts a new word, a first word
  the spell backend can actually correct. The leading-word check is
  intentionally fail-open: names, jargon and model vocabulary without a
  replacement pass through. Windows registers no always-on spell checker
  equivalent, so the check is pluggable (`ISpellAssessor`); without one the
  guard stays at its pure junk-run rule and behavior is unchanged beyond junk
  runs. Spelling checks skip capitalized words, short joins, tokens with
  digits, and CJK text.
- Port upstream [Low Power Mode pause ([#812](https://github.com/FuJacob/cotabby/commit/d4c4d23410dc8a720c30725ae63451dad598f525))](docs/upstream-low-power-mode.md):
  a new opt-in **Pause in Low Power Mode** toggle in Settings → General.
  When enabled, new suggestion requests pause while the Windows energy saver
  is engaged (read via `GetSystemPowerStatus`, one cheap cached poll per
  evaluation, never throws). Off by default, matching upstream.
- Port upstream [held-presentation rapid-Tab acceptance ([#838](https://github.com/FuJacob/cotabby/commit/7724926b3e93f14e3b576ba46ff52c4f64d9712d))](docs/upstream-rapid-tab.md):
  while Cotabby's next overlay presentation is still in flight, a rapid
  follow-up Tab now accepts the tail Cotabby is about to paint instead of
  failing the stale-ghost check, tearing the session down, and leaking a real
  Tab into the host (which used to move focus to the page's next control).

### Fixed

- Preserve all earlier ports: full output-budget reservation (#851), partial
  GPU/request Performance pane (#858), credential-field protection (#848),
  opt-in double-tap acceptance (#840), and the cumulative insertion,
  clipboard, BlockInput and privacy regressions fixed on Windows in v0.3.0–
  v0.4.1.

### Verification

- 319 unit tests locally (281 baseline + 38 new: 31 seam-guard, 3 Low Power
  Mode, 2 rapid-Tab rig cases) and hosted Windows CI.
- Two timing-sensitive pre-existing tests flaked once on hosted CI and passed
  on rerun and deterministically in 30+ local runs; they are not related to
  this change set and are unchanged from v0.4.1.

### Known limits

- Physical keys, live-model suggestions, per-application accessibility, GPU
  accuracy and the real energy-saver engagement path remain owner-verified;
  CI uses synthetic input and a deterministic engine. The spell backend is
  not registered on Windows yet, so the new leading/seam checks only act when
  an `ISpellAssessor` is injected.
- Rejected captured keys are still not replayed; warnings make this visible
  without logging content.

## 0.4.1 — 2026-10-15

The v0.4.0 tag build was cancelled before publication when the final privacy
review found typed-character debug logging. v0.4.1 is the first published build
for this cumulative change set. No v0.4.0 release ZIP was published.

### Added

- Complete the global, opt-in [double-tap Tab acceptance port (#840)](https://github.com/FuJacob/cotabby/commit/3ad86f3ff0c7fa4abe250fde348611f162027650): first Tab accepts a word/phrase, second distinct Tab within 300 ms accepts the remainder once. Backtick remains the default; Shift+Tab stays navigation. Serialized acceptance preserves spacing and handles repeats, delayed/failed insertion, focus changes and disposal. See [scope and verification](docs/upstream-double-tap.md).
- Content-free application-log warnings for captured acceptance keys that cannot be inserted and are deliberately not replayed. No unsafe injection into an unknown foreground window.
- Actual Windows Settings dropdown/persistence and production text-inserter runtime regressions in both CI and the release workflow, in addition to nine synthetic hook/UIA cases.

### Fixed

- Stop recording typed-character codes in key diagnostics, including credential fields. Older builds' local `C:\tmp\cotabby-live.log` may contain reconstructable keystrokes: remove that old log when upgrading; no logs are included in this release ZIP.
- Prevent a retiring acceptance processor from draining a replacement queue started by a completion callback or concurrent keypress; queued recovery keys keep their own processor and spacing.
- Recheck field/process/credential ownership before the first overlay chunk, even when a focus-change notification has not arrived.
- Revalidate the actual focused UIA element and foreground process at production insertion dispatch; a stale target cannot intentionally direct text into the newly focused field.
- Keep input blocking, clipboard paste and unblocking on one STA thread instead of crossing async continuations, which could silently lose long completions or leave input blocked.
- Preserve non-text clipboard formats, restore an originally empty clipboard, and avoid replacing a clipboard changed by another application. If a format cannot be copied, refuse long insertion before modifying it.
- Respect SendInput's dispatched-event count rather than reporting success unconditionally. Finish cleanup after an irreversible dispatch even when cancellation arrives.
- Preserve all earlier ports: full output-budget reservation (#851), partial GPU/request Performance pane (#858), and credential-field protection (#848).

### Known limits

- Rejected captured keys are not replayed; that press can be lost. Warnings make this visible without logging its content.
- Physical keys, live-model suggestions, per-application accessibility and GPU accuracy still require owner testing. CI uses real Windows hooks/UIA with synthetic input and a deterministic engine; it is not live-model or universal host parity proof.

## 0.3.0 — 2026-10-13

### Added

- Port upstream [sign-in and verification-field protection (#848)](https://github.com/FuJacob/cotabby/commit/d81d304f1ea0a5b4bb5feff9b71407ebee56b13c):
  Cotabby leaves email, username, phone, one-time-code, card and password inputs
  alone. The Windows tracker decides from field metadata — password flag,
  whole-word accessible label (`Name`, `HelpText`, `LabeledBy.Name`) and bounded
  automation identifier — **before** reading any field text, and returns a
  snapshot with no text for a recognized field. A neutral single-line field may be
  read once for the address-shape fallback; rejected text never travels further.
  The gate applies to every caller: the request path, the trigger entry point (the
  post-exhaustion continuation previously bypassed the primary check), the
  accept/insert path, and the emoji trigger and commit.
- Port upstream [GPU usage in the Performance pane (#858)](https://github.com/FuJacob/cotabby/commit/8cdbea2d2619b0f89a73d46eb0bb856504d07343)
  as a partial Windows Performance pane (no CPU/RAM graphs): live Cotabby GPU
  share, busiest-engine machine GPU utilization and GPU memory read from the
  `GPU Engine` / `GPU Adapter Memory` performance counters, plus an opt-in log of
  per-request latency (timestamp, model, elapsed; newest 200 retained, off by
  default). Every GPU value is optional, so an unavailable counter set shows "no
  data" rather than a misleading zero, and sampling runs only while the pane is
  visible. Latency rows are recorded only for generations actually shown to the
  user.

- `tabby-2-plus`: Qwen3.5 4B Base Q4_K_M for general-purpose autocomplete.
- `tabby-2-code`: Qwen3.5 9B Coder Q4_K_S for programming-focused autocomplete.
- Project provenance, change boundaries, and AI-agent handoff documentation.
- `UPSTREAM_PORTING_PLAN.md`: staged plan and copy-paste prompt for selectively
  porting compatible improvements from the original Cotabby project.

### Changed

- Port upstream [full output-budget reservation (#851)](https://github.com/FuJacob/cotabby/commit/80f46e64c7a25c11f3f035c3db697612a7e4e868):
  reserve at least 50 output tokens, the full larger request allowance, and a
  64-token margin before rendering. Windows uses the loaded model's actual
  context size and tokenizer, including BOS and serialized separators.
- Trim low-priority prompt sections before caret text; preserve the caret tail
  without splitting UTF-16 surrogate pairs. Exhausted budgets produce no prompt.
- FIM prompt capacity now follows the loaded context rather than a fixed 4096.
- GPU readouts retain consecutive raw counter samples instead of discarding rate
  baselines, keep adapter/engine identity, and report unknown rather than a
  fabricated zero; a per-engine counter reset can no longer be hidden by growth
  on another engine.
- The release workflow verifies provenance, the full package manifest, the
  required native layout and the absence of model/log/secret files, and publishes
  a SHA-256 checksum beside the ZIP. The app reports its version from `VERSION`.
- Windows CI additionally runs a synthetic real-UI-Automation credential proof
  (password, username, verification-code and card fields must be refused with zero
  payload reads) and a GPU counter/persistence smoke test.

### Maintained

- Existing nano, mini, Gemma E2B, and Gemma E4B model choices.
- Folder-based self-contained Windows release packaging with native LLamaSharp
  runtime files beside the executable.
- Inference stays local: no telemetry, no cloud completion, no typing-history
  storage. Upstream's typing-history feature (#841) is deliberately not ported.

### Known limits

- GPU accuracy, per-adapter behavior and minimize/restore on real hardware are
  owner-verified only. Hosted CI proves API compatibility, packaging and the
  synthetic credential guards — not live readings or live autocomplete.
- Browser/application accessibility metadata varies; credential recognition is
  metadata-based and is not complete DOM/autofill parity.
- The local live log records rendered suggestions, not typed text, and should not
  be shared.
