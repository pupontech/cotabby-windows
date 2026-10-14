# Changelog

## Unreleased

### Added

- In-progress, global-only opt-in [double-tap Tab acceptance (#840)](https://github.com/FuJacob/cotabby/commit/3ad86f3ff0c7fa4abe250fde348611f162027650). Default Backtick remains unchanged; selecting DoubleTap replaces the full-accept key choice. 274 portable tests pass and Windows CI runs 9/9 synthetic real-hook cases (including Shift+Tab pass-through). A key captured by the accept path whose insertion is then refused is NOT replayed; that press is reported, not re-delivered. See [scope and acceptance](docs/upstream-double-tap.md).

### Fixed

- Refuse suggestion insertion when the refreshed field belongs to another process, with a RED→GREEN regression. This does not establish same-process field identity.

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
