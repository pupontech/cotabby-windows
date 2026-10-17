# Content-free suggestion diagnostics

## Problem and current behavior
Coordinator presentation, inference suppression/output and production insertion log raw suggestion prose, including reversible UTF-16 hex dumps. Credential-field exclusion does not protect secrets embedded in ordinary documents. Lowering verbosity is insufficient because these events are Information level.

## Desired behavior and scope
Remove generated and inserted text from every production diagnostic level. Retain correlation IDs, lengths, durations, policy verdicts and backend metadata. Remove obsolete text-formatting helpers. Keep existing field protection unchanged.

## Non-goals
No raw-text capture mode, clipboard capture, history store, new telemetry, or decoder changes. A secret sanitizer does not make prose logging acceptable.

## Architecture and affected files
Change existing logger callsites in SuggestionCoordinator, LlamaSuggestionEngine and SendInputTextInserter; do not create a parallel logging framework. Add source-contract regression tests for forbidden content-bearing logger arguments across those components.

## Performance and privacy impact
Avoid string escaping/hex allocations and formatting on typing/insertion paths. No document content is written by these diagnostics. Existing historical logs are not migrated or copied; users should delete old logs explicitly.

## Acceptance and automated tests
Logger calls must contain neither raw/normalized/accumulated/text values nor reversible text dump helpers. Metadata diagnostics remain. Run the regression RED before changing callsites, then GREEN, full Core tests and Release application cross-build.

## Manual Windows verification
Generate and accept a unique sentence in Notepad with each diagnostic verbosity. Inspect local logs and console: no sentence, substrings or hex representation may appear. Verify duration/verdict events remain and Tab acceptance still works.

## Rollback
Revert callsite changes only if diagnostics require correction; do not restore raw-text logging. No settings changes or migrations.
