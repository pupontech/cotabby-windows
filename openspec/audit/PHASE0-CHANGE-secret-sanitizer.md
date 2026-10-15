# Secret sanitizer for log sinks

**Target phase:** 1 (foundation — every later change writes new log lines)
**Upstream provenance:** none; direct response to the v0.4.1 privacy fix
(`CHANGELOG.md`: typed-character debug logging removed) and `AGENTS.md:37`
("Never log field text, request prefixes or credential metadata").
**Risk level:** Low (defense-in-depth; on by default). **Flag:** none needed
(always-on policy layer, with an escape hatch setting).

## Why

`FileLoggerProvider` (`src/Cotabby.App/Hosting/FileLoggerProvider.cs`) writes
every `LogDebug`+ line verbatim to `C:\tmp\cotabby-live.log`. The coordinator
deliberately logs rendered suggestions (`SuggestionCoordinator.cs:641-644`,
`:657-659`) and the inserter logs the text it sends
(`SendInputTextInserter.cs:65-67`, `:76-78`). Those strings are *suggestions*,
never typed field text — but a user's own prose routinely contains
email addresses, bearer tokens, API keys, card numbers, and URLs with
embedded credentials, and today any of those lands in the log file
byte-for-byte. The v0.4.1 release already had to tell users to delete old
logs containing reconstructable keystrokes; a sanitizer makes the same class
of leak structurally impossible instead of convention-dependent.

## What Changes

- New pure `Cotabby.Core.Support.SecretSanitizer` with deterministic,
  regex-based redaction of secret-shaped fragments:
  - email addresses → `<redacted:email>`
  - bearer/JWT tokens and `sk-`/`api-key`-shaped runs → `<redacted:token>`
  - URLs with `user:pass@` credentials → `<redacted:url-creds>`
  - payment card numbers (Luhn-valid, grouped digits) → `<redacted:card>`
  - `key=value` / `key: value` pairs whose key names a secret
    (`password`, `token`, `secret`, `authorization`, …) → `key=<redacted>`
- `FileLoggerProvider` runs every formatted line through the sanitizer
  before write; the simple-console and debug sinks in
  `AppHost.cs:59-72` get the same treatment via one shared sanitizing
  formatter.
- Redaction is fail-open for diagnostics safety: on regex failure the line
  is replaced wholesale with `<sanitizer-error>` rather than written raw.
- Sanitizer runs on the *formatted line*, so it also covers exception text.
- New `AppSettings.RedactSecretsInLogs` (default `true`) — the escape hatch
  for deep debugging, surfaced in the Advanced pane with a warning.

## Non-goals

- No redaction of ordinary prose, numbers, or identifiers (over-redaction
  destroys the diagnostic value the log exists for).
- No scanning of field text, UIA values, or typed input — sanitizer is a
  log-sink concern only.
- No changes to what is logged; this changes only how it is written.

## Proposal outline (proposal.md)

```markdown
## ADDED Requirements

### Requirement: Secret-shaped fragments are redacted from every log line

The app MUST replace email addresses, tokens, credentialed URLs,
Luhn-valid card numbers, and secret-named key/value pairs with
`<redacted:*>` placeholders before any log line reaches a file, console,
or debug sink. Redaction failure MUST suppress the line, never pass it
through raw.

#### Scenario: Suggestion containing an email is logged redacted
- **WHEN** a rendered suggestion containing `jane@example.com` is logged
- **THEN** the log line contains `<redacted:email>` and not the address

#### Scenario: Bearer token in an exception message is redacted
- **WHEN** an exception whose message contains `Authorization: Bearer eyJ…`
  is logged
- **THEN** the token is replaced and the remainder of the message survives

#### Scenario: Ordinary prose passes through unchanged
- **WHEN** a log line contains ordinary words and numbers
- **THEN** the line is written byte-identical
```

## Source files touched (verified)

| File | Change |
| --- | --- |
| `src/Cotabby.Core/Support/SecretSanitizer.cs` | **new** — pure redaction logic |
| `src/Cotabby.App/Hosting/FileLoggerProvider.cs:57-68` | sanitize `line` before `WriteLine` |
| `src/Cotabby.App/Hosting/AppHost.cs:59-72` | wire sanitizer into console/debug sinks |
| `src/Cotabby.App/Settings/AppSettings.cs` | add `RedactSecretsInLogs` (default `true`) |
| `src/Cotabby.App/UI/SettingsWindow.xaml.cs:932-970` (Advanced pane) | escape-hatch toggle + warning |
| `tests/Cotabby.Core.Tests/SecretSanitizerTests.cs` | **new** |

## Risks

- Regex over-matching mangles diagnostics → mitigated by narrow patterns,
  fail-open wholesale suppression, and an off switch.
- Performance: sanitizer runs per log line at Debug level (already the
  hottest path). Budget: compiled regexes, single pass, no allocations on
  the no-match path; benchmarked in `benchmark-cli` (Phase 4).
- The old `C:\\tmp\\cotabby-live.log` from prior builds still contains
  unsanitized history — rollout note, not a code risk.

## Test plan (unit)

`tests/Cotabby.Core.Tests/SecretSanitizerTests.cs` (repo naming style:
`PascalCase_Behavior`):

- `EmailInSuggestionText_IsRedacted`
- `BearerAndJwtTokens_AreRedacted`
- `UrlWithEmbeddedCredentials_IsRedacted`
- `LuhnValidCardNumber_IsRedacted`
- `NonLuhnDigitRun_IsNotRedacted`
- `SecretNamedKeyValuePair_RedactsValueOnly`
- `OrdinaryProseAndNumbers_PassThroughUnchanged`
- `EmojiAndSurrogatePairs_SurviveRedaction`
- `ExceptionText_IsSanitized`
- `EmptyAndNullInput_ReturnInputUnchanged`

## Manual Windows smoke steps

1. Build and run; confirm `C:\tmp\cotabby-live.log` still receives
   `Model … loaded` and constrained-decode lines (sanitizer must not break
   the existing evidence format).
2. Type a sentence containing an email into Notepad until a suggestion
   appears; grep the log for the address — expect `<redacted:email>`.
3. Paste a fake `Authorization: Bearer …` string into a field to get it
   into a suggestion; confirm redaction in the log.
4. Toggle the Advanced-pane escape hatch off; confirm raw lines return
   (proves the flag works and is user-visible).

## Rollback

- `RedactSecretsInLogs=false` restores verbatim logging without a rebuild.
- Code rollback is a revert of the sink changes; `SecretSanitizer` has no
  callers outside the sinks.

## Privacy & security constraints

- Sanitizer never reads field text itself; it only transforms formatted
  log lines. It does not weaken the "never log field text" rule — it
  hardens the cases where *rendered output* happens to embed secrets.
- No typed-type persistence: nothing the sanitizer touches is stored
  beyond the existing local log file.
- Credential-field protections untouched: `IsCredentialField` gating in
  `SuggestionAvailability.cs:32` and every coordinator caller is unchanged;
  credential fields produce no suggestions, so no sanitizer bypass exists
  on that path.
- No cloud calls: pure local string transformation.
