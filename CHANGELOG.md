# Changelog

## Unreleased

### Added

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
- Add Windows PR CI tests, build, folder publish, full package hash verification,
  and extracted CPU DLL loading. Real model/UI/GPU validation remains pending.

### Maintained

- Existing nano, mini, Gemma E2B, and Gemma E4B model choices.
- Folder-based self-contained Windows release packaging with native LLamaSharp
  runtime files beside the executable.
