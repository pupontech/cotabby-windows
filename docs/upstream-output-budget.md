# Full request output-budget reservation

## Source and scope

Upstream: https://github.com/FuJacob/cotabby/commit/80f46e64c7a25c11f3f035c3db697612a7e4e868

Reserve the larger of 50 output tokens or the request's entire decode allowance,
plus a 64-token safety margin, before rendering a continuation prompt. Keep
section priority, whole-section ordering, and the tail nearest the caret instead
of relying on the runtime's emergency raw-token crop.

The Windows factory does not render prompts; the inference engine does. Adapt
the behavior there using the actual loaded context size and the loaded model's
plain-text tokenizer, including BOS. This avoids importing upstream's token
estimator or its Apple/cloud engines. FIM retains its existing prefix-tail,
suffix-head, and three-marker assembly but uses the same actual-context budget.

## Affected files

- `Cotabby.Core/Support/CompletionPromptBudget.cs`: pure reservation arithmetic.
- `Cotabby.Core/Support/CompletionPromptBuilder.cs`: pure Windows request-to-prompt
  integration seam used by the inference engine and focused saturation tests.
- `Cotabby.Core/Support/BaseCompletionPromptRenderer.cs`: optional exact-token
  budget, serialized separator accounting, and Unicode-safe section trimming.
- `Cotabby.Inference/LlamaRuntimeCore.cs`: expose the actual context size.
- `Cotabby.Inference/LlamaSuggestionEngine.cs`: apply reservation before both
  continuation and FIM tokenization.
- Core tests: arithmetic, renderer bounds, ordering, Unicode, and refusal cases.
- Windows PR workflow: restore, test, build, publish, verify native layout,
  archive, and retain ZIP and test evidence without publishing a release.
- README, changelog, and porting plan: source attribution and pending live proof.

## Acceptance criteria

1. Default and 50-token requests retain the standing output reservation; larger
   requests reserve their whole output, with a zero floor for exhausted context.
2. Exact serialized prompt tokens (including BOS and separators), plus the
   output reservation and safety margin, do not exceed the loaded context.
3. Caret text wins over persona/style/language/notes, remains last, and preserves
   its tail. A zero/negative character or token budget must not revive the prefix.
4. Unicode truncation never splits a surrogate pair. Counting operates on the
   complete rendered prompt, not summed per-section estimates.
5. Existing calls without a token budget retain their default behavior.
6. FIM no longer assumes a 4096-token context; no macOS APIs, cloud inference,
   model files, raw logs, or single-file packaging are introduced.
7. .NET 9 local tests/cross-build/publish pass, followed by exact-commit Windows
   CI tests/build/publish and extracted native-layout proof.
8. Package bytes and SHA-256 are verified. Real small/large-model suggestions in
   a Windows text field remain explicitly owner-gated, not claimed from CI.

## Compatibility and rollback

Prompt sizing adds one exact-tokenizer preflight per continuation request;
bounded fitting needs additional tokenizations only when the rendered prompt is
oversized. Binary fitting may conservatively underfill when tokenizer counts
are non-monotonic; the returned prompt must always be measured and bounded.
No settings, model catalog, UI, insertion, or on-disk schema changes are needed.
Rollback is a revert of the focused feature commit. The existing runtime hard
crop remains a defense for direct callers, not the main prompt-sizing strategy.

## Delivery authority

Owner approved pushing the feature branch, opening a PR, and producing a Windows
CI ZIP. Do not merge, create a tag/release, or change repository settings.
