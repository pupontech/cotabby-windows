# Conservative local secret sanitization

## Problem and existing behavior
No reusable sanitizer protects optional future writing imports or metadata diagnostics from high-confidence secrets embedded in ordinary prose. Existing credential-field gates remain mandatory and unchanged. Removing raw prose logging is a separate prerequisite, not replaced by this sanitizer.

## Scope and desired behavior
Add a pure C# helper adapted from TabType SecretSanitizer.swift at b75ea0e. Recognize provider-specific credentials, private PEM blocks, authorization headers, explicit password/token assignments, sensitive URL query parameters and Luhn-valid card numbers. Preserve ordinary prose, code identifiers and arbitrary hashes. Omit generic entropy and unchecked IBAN heuristics. Include the original MIT notice.

## Non-goals
No capture, automatic history, cloud services, field reads, or permission to log sanitized document content. No integration with a nonexistent writing store. No false claim of exhaustive secret detection.

## Architecture and files
Cotabby.Core/Support/SecretSanitizer.cs: pure helper with fixed regex rules, per-rule timeout and maximum input length. Tests in Cotabby.Core.Tests/SecretSanitizerTests.cs; attribution in THIRD_PARTY_NOTICES.md. No new dependency, UI or settings defaults.

## Performance and privacy
Bound input to 256 Ki UTF-16 code units and regex processing to 50 ms per rule. If oversized or a match times out, return a redacted placeholder for the entire input rather than allowing unsanitized fallback. No persistence or background threads. This helper is not called from the keyboard hook.

## Acceptance and automated tests
Recognized keys and PEM content are removed; explicit assignments preserve labels, quoted values with spaces are fully removed; sensitive URL values are removed; Luhn validation distinguishes valid card candidates from invalid numbers and preserves all-identical digits. Prose/hashes survive, Unicode survives, empty/null, repeated sanitization and oversized input are safe. Run tests RED/GREEN and full core suite; Release build.

## Manual Windows verification
With future opt-in import integration: import a synthetic corpus containing fake secrets, inspect the encrypted-store plaintext before encryption via a test harness, and verify all recognized secrets excluded. No real user secrets in fixtures. Current helper has no user-facing behavior to smoke-test.

## Rollback
Remove helper and tests if unused. Any future consumer must fail closed rather than bypass sanitization. Existing secure-field protections and settings remain untouched.
