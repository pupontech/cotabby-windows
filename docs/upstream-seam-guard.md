# Correctable-misspelling completion guard: upstream #814 Windows adaptation

## Source and scope

Source: [FuJacob/cotabby #814, `ac2699e4ab40b68cf7429b7d13d5a7206c99dbbc`](https://github.com/FuJacob/cotabby/commit/ac2699e4ab40b68cf7429b7d13d5a7206c99dbbc),
as refined by upstream #832's presentation decision (streaming leading-word
buffering). Windows implementation is based on v0.4.1, retaining the existing
output-budget, Performance pane, credential-protection and double-tap ports.
Merged into main as PR #6 and shipped in v0.5.0.

## Behavior

A pure post-generation `CompletionSeamGuard` (in `Cotabby.Core/Support`) runs
once after normalization, before display, and refuses three classes of visible
glitch:

- **Junk punctuation run**: four or more identical punctuation/symbol
  characters freshly introduced inside the completion. A run flush against the
  seam that extends a real preceding run of the same character (continuing an
  existing `----` divider) is legitimate and exempt; a sentence that merely
  ends in "." does not exempt "....".
- **Mid-word seam misspelling**: when the preceding text ends inside a word and
  the completion starts with word characters, the joined word formed across
  the seam must be known to the spell backend. Exempt: capitalized words
  (names and brands are routinely out-of-dictionary), joins shorter than four
  letters, tokens containing digits, and CJK text.
- **Correctable leading-word misspelling**: when the completion starts a new
  word, the first generated word is refused only when the backend both
  identifies it as a typo *and* offers a replacement. Unknown-but-uncorrectable
  vocabulary passes, which deliberately keeps names and jargon visible. Tokens
  containing digits never reach the backend at all.

For streamed partials the leading-word half buffers until its boundary arrives
(a prefix such as `ecr` would false-positive) and then resolves once per
generation so the backend never runs at token cadence. A connector prefix
(`don` + `'t`) continues the caret word and is never treated as a leading
word.

## Windows deviation: pluggable spell backend

Upstream consults `NSSpellChecker`, which is always available on macOS. Windows
has no direct equivalent, so the engine exposes an injectable
`ISpellAssessor`; the llama engine applies the final-verdict gate after
normalization. **No backend is registered in v0.5.0**, so every word assesses
as known: the seam and leading-word checks are dormant and the guard acts only
on its pure junk-run rule. When a backend is later injected (e.g. an
ICU/SysSpell-based one), the same pure rule activates with no further changes.
This is recorded in the README parity table and the changelog.

## Verification

- 31 new unit tests (`CompletionSeamGuardTests.cs`) pin every rule class,
  exemption and streaming case against an explicit assessment function.
- Windows CI run 38748405213 (main) green at the merge commit; the seam-guard
  tests passed on every run.
- Runtime effect on the live pipeline: junk-run suppression now applies to the
  final suggestion before the overlay is shown (previously only the 5+-char
  insert-time firewall truncated). The spelling checks act only when a backend
  is injected.
