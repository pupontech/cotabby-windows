# Credential-field protection: Windows adaptation of upstream #848

Upstream source: https://github.com/FuJacob/cotabby/commit/d81d304f1ea0a5b4bb5feff9b71407ebee56b13c

## Scope

Leave sign-in, verification and payment-identity inputs alone, alongside password
fields. This is a focused Windows UI Automation adaptation, not a wholesale merge
of the Swift/macOS implementation and not complete browser DOM parity.

Recognize credentials from field metadata: password flag, whole-word accessible
labels, and bounded whole-vocabulary automation identifiers. Recognized metadata
must reject the field **before** reading its text/value/caret. Existing password
fields currently reach a text read before `IsSecure` is set; this port moves the
privacy boundary ahead of that read.

A neutral single-line input may require one text read to recognize a complete or
incomplete single-token email address. Rejected text must not be returned in a
`FocusedField`, sent to inference, used by emoji completion or logged by the new
policy. That fallback is not a promise that all credential content can be
identified without reading it.

Match whole words/identifier parts, not arbitrary substrings. Preserve ordinary
prose fields such as Email subject, Search email, Shipping notes and email bodies.
Document/Pane multiline inputs remain prose-capable. The Windows adapter does not
have every DOM/autocomplete attribute available to macOS; unknown browser metadata
and unsupported control providers remain explicit limitations.

No network inference, telemetry, typing-history storage, model changes, global
log migration or new user-data collection is authorized. Typing-history upstream
#841 is deferred pending a separate privacy decision.

## Owned changes

- `Cotabby.Core/Focus/CredentialFieldDetector.cs`: the platform-free policy,
  exercised directly by unit tests through the same entry point production
  calls (`IsCredentialField`).
- `Cotabby.Win32/Focus/UiaFocusTracker.cs`: UIA metadata collection (`IsPassword`,
  control type, `Name`, `HelpText`, `LabeledBy.Name`, `AutomationId`) and rejection
  before any payload read, followed by the address-shaped fallback for admitted
  fields. A blocked field returns a snapshot flagged `IsCredentialField` with an
  empty payload.
- `Cotabby.Core/Suggestions/SuggestionAvailability.cs`,
  `SuggestionCoordinator.cs`, `Cotabby.App/Emoji/EmojiPickerController.cs`: the
  gate at every caller — request, trigger, accept and emoji commit.
- Core tests: upstream recognition and negative-control cases, password override,
  metadata/no-read ordering and ordinary prose preservation.
- `tools/Cotabby.Tools.CredentialSmoke`: Windows-only synthetic fixture using real
  UIA and the production focus tracker, with payload-read instrumentation.
- Windows workflow: execute that proof, then the existing exact-SHA tests,
  self-contained package manifest/hash and native-load checks.

## Acceptance and verification

1. Password and metadata-recognized username/OTP/card fields never invoke the
   injected payload-read callback; UIA fixture spy confirms the same boundary.
2. Neutral synthetic partial-address fields are rejected after the necessary
   value read; no rejected payload enters a returned snapshot.
3. Ordinary prose fixtures remain capturable. Exact upstream label/id test rows
   are retained, including whole-word boundaries and prose exceptions.
4. A negative control explicitly reads a synthetic ordinary value and proves the
   spy is live. A rejection must be the guard's own decision: the proof requires a
   non-null snapshot carrying the fixture process id and `IsCredentialField`, with
   empty text and no caret offset, so focus loss or a swallowed exception can never
   be mistaken for a successful denial. The tool reports a skip only when every
   case failed for infrastructure reasons.
5. No fixture uses real credentials or prints values. All waits are bounded and
   cleanup targets only the fixture process and its own per-run files.
6. Local .NET 9 focused/full tests, Windows cross-build and diff checks pass;
   independent review precedes commit. Actual UIA proof runs on a Windows CI
   runner, not a provisioned VM or the owner's machine.

Owner browser/application testing remains useful because accessible metadata and
provider behavior vary. Use only synthetic values when testing credential fields;
never send actual credentials or raw application logs. A merge under the owner's
"continue onto more merges" instruction does not certify every website/provider
or authorize a tag/release.
