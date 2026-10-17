# Selective upstream repetitive-output suppression

Sources:
- [Original CompletionContentPolicy.swift at 8cdbea2](https://github.com/FuJacob/cotabby/blob/8cdbea2d2619b0f89a73d46eb0bb856504d07343/Cotabby/Support/Suggestion/Output/CompletionContentPolicy.swift)
- [Windows adaptation ff381f5](https://github.com/Msde-7/cotabby-windows/commit/ff381f53059f6904cfa70812332e3e53df3f388e)

## Scope and acceptance

Port only the repetitive-content rule: suppress a normalized suggestion when
one, two or three whitespace-separated words repeat at least three times
consecutively anywhere in it. Compare invariant-lowercased words, retaining
punctuation. Two copies remain valid emphasis. Repeated words separated by
other content remain valid. Apply after existing echo/line cleanup and the
insertion-safety gate, before a completion reaches the overlay or insertion.

Affected files: `RepetitionGuard.cs` holds the pure word-loop detector beside
the existing token n-gram guard; `SuggestionTextNormalizer.cs` returns a distinct
`RepetitiveContent` suppression reason; `DegenerationGuardSmokeTests.cs` exercises
the production normalizer and detector. No native decoding, field identity,
clipboard, settings, durable text storage or network changes are included.

This is NOT the full upstream content policy: punctuation-only, scaffolding and
recent-text copying rules are not added by this change. It does not replace the
token-level guard; token segmentation can differ even when visible words loop.

## Risks and rollback

Intentional triple repetition (including code) can be suppressed, matching the
upstream heuristic. Punctuation variants and unsegmented scripts are not folded
into words; this is not a semantic repetition detector. .NET invariant casing
and Unicode whitespace follow the host runtime rather than Swift Foundation.
Existing normalizer suppression precedence stays unchanged. Revert the focused
port commit to roll back; no migrations or settings changes are involved.

## Verification and owner test

Require regression cases for periods 1–3, loops after introductory text,
case/Unicode whitespace, multiline handling and ordinary emphasis. Require the
full core suite, Windows build/package and existing Windows smoke checks.

For owner validation, extract the complete test ZIP into a new folder, retain
`runtimes/win-x64`, and launch `Cotabby.exe` as a standard user. Confirm cached
models still produce ordinary suggestions and word/whole-suggestion acceptance
works. This deterministic filter cannot guarantee a model will naturally emit
a loop during a short live test. Real model inference and physical keys remain
owner-gated; CI's crafted-output tests prove the suppression rule.
