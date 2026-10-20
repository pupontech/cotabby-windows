# October upstream PR review and Windows test build

## Scope and verdict

Reviewed the 13 PRs opened in FuJacob/cotabby since October 5: #864, #866–#877
(excluding nonexistent #865 in this PR set). Main remains
`8cdbea2d2619b0f89a73d46eb0bb856504d07343`. Windows baseline is
`d10b9c2` (v0.6.0 main). Other Windows feature PRs are not included.

**Verdict: FIX-FIRST for importing the entire upstream set.** This test build
ports #866's metadata-only search suppression and fixes a related Windows
late-presentation hole. It does not import MLX, rewriting, memory, answer cards,
localization or the latency patch. A green upstream check is not an approval to
ship its source or evidence of Windows performance.

This is a source/contract and Windows-applicability review, with focused
execution where possible; it is not an exhaustive audit of every line in the
large stacked Swift branches. Swift/macOS app tests cannot run on this Linux
host. Findings below are checked against the pinned source, not copied blindly
from review bots. No upstream branches were modified.

## Disposition of every PR

| PR | Pinned head | Review / Windows disposition |
|---|---|---|
| [#864](https://github.com/FuJacob/cotabby/pull/864) | `c966207a5bb077c7537d660f3126bd66666fcd4e` | Localization tooling validated 734 source keys / 15 languages; all five Python tooling tests passed. No concrete blocker found in that tooling pass. Defer WPF localization: an xcstrings catalog and SwiftUI relaunch path do not localize WPF, and translations still need human review. |
| [#866](https://github.com/FuJacob/cotabby/pull/866) | `90fd178c0f13a1feaa14ded37bb3a363f6d7deef` | Adapted metadata-only suppression. Do not copy the missing-DOM-attributes browser exception (F1). Browser search/address bars with search labels are intentionally suppressed in this Windows candidate. |
| [#867](https://github.com/FuJacob/cotabby/pull/867) | `3ee0fd75d579f708e16e4445089387d0e4b8ef28` | Defer: floor bypass and incomplete containment check (F2). Windows has a different non-wrapping WPF layout; copying the Swift rows would not fix it. No field-bottom clipping is claimed in this build. |
| [#868](https://github.com/FuJacob/cotabby/pull/868) | `e595546fd9ed4f0c8d91b062086668bdfa4f7989` | Apple-Silicon MLX is not a Windows backend. Process/toolchain races remain in the stack (F3). Historical cache-completeness/custom-download findings were fixed; do not report them as current. |
| [#869](https://github.com/FuJacob/cotabby/pull/869) | `b816264e6b5030771b719ceb55c3d494cbdab15b` | Defer selection-rewrite subsystem. F4 is current; earlier numeric-separator and missing-selection-context findings are fixed in this head. Its cloud providers conflict with this fork's local-only rule and must not be silently imported. |
| [#870](https://github.com/FuJacob/cotabby/pull/870) | `0e4a4e7118d26ebe8b9c20456709bb19b4328581` | Stacks rewriting and MLX, not an independent feature. Inherits F4 and requires a Windows selection/replacement design. Full endpoint URL comparison is present: the earlier same-host/changed-port finding is fixed. No cloud or MLX rewrite path imported. |
| [#871](https://github.com/FuJacob/cotabby/pull/871) | `0de77692843c5997a90755c62e009b385a51471b` | Defer memory readers/store/model and permissions. F5 and F6 remain; Keychain, EventKit and Mac app stores are not Windows APIs. Needs a separate Windows encryption, identity, retention, deletion and resource-budget design. |
| [#872](https://github.com/FuJacob/cotabby/pull/872) | `4cc9f628a248b3af6882fd3162a62ded3a76afbd` | Stacks on memory. Cache privacy stamps are present (old forgotten-cache finding fixed), but F7 remains. Do not imply answer-card support without the memory/identity/consent foundation. |
| [#873](https://github.com/FuJacob/cotabby/pull/873) | `d9da9cab821f235ecdcdb73de077751074fcc904` | Two pinned download-artifact changes in the Mac DMG release workflow. No application behavior. Windows has no corresponding DMG/notarization jobs; no mechanical version bump applied. |
| [#874](https://github.com/FuJacob/cotabby/pull/874) | `d377057124a097ff526038f758338688c176b465` | Five pinned upload-artifact changes in the same Mac release workflow. No application behavior. Windows workflow upgrade is separate; existing tested upload path retained. |
| [#875](https://github.com/FuJacob/cotabby/pull/875) | `4212ad223fba4ea02448afb4d62e7e81d847c9e2` | Defer latency bundle: F8 is a correctness blocker. Optimistic typing also needs a Windows host-publication/IME/key-ownership proof before porting. Reported 57–58 ms median is the author's Mac result, not this build's measurement. |
| [#876](https://github.com/FuJacob/cotabby/pull/876) | `6feec96081992a10d363333ff47f64bfb1550d80` | Two incremental cache commits on #868, not another independent engine. Mac-only and inherits F3; full caches grow with prompt length. Runtime monkey-patch compatibility must be verified against every supported mlx-lm version. |
| [#877](https://github.com/FuJacob/cotabby/pull/877) | `9f4be3ba142a6147bda32919b2933c0935b2de70` | Incremental memory-sync optimization on #871. F9/F10 block adoption; project.yml consistency check also fails while build/tests/lint pass. No Outlook/cloud-drive importer shipped. |

## Confirmed findings

Line references below refer to the pinned upstream heads above, unless explicitly
marked Windows. Severity describes impact, not a promise that a branch was run.

### F1 — MEDIUM: missing DOM attributes exempt actual browser search fields (#866)

`Cotabby/Support/Accessibility/SearchFieldDetector.swift:40–42,52` returns false
for browser chrome before considering the explicit search role/subrole.
`Cotabby/Services/Focus/Resolution/FocusSnapshotResolver.swift:423–425` infers
chrome from missing DOM attributes. Firefox page controls can lack those same
attributes. Result: search fields still receive prose completions.
**Fix:** prove an address-bar identity separately; explicit search semantics
must not be overridden by absence of optional metadata. Windows adaptation has
no broad browser exception and evaluates metadata before reading text.

### F2 — HIGH: empty first row bypasses the field floor (#867)

`Cotabby/Support/Presentation/Geometry/GhostTextLayout.swift:227–236` lowers the
baseline and continues when no word fits on the caret row, bypassing the floor
check at `240–243`. The next row can be drawn below the field. Independently,
`GhostWrapBandPolicy.swift:67–69` verifies vertical but not horizontal
containment, trusting an adjacent box at the same height.
**Fix:** check every row before drawing/moving to it; require x and y
containment. Add empty-caret-row and horizontally displaced-field fixtures.
Static control-flow proof, not a CoreText runtime reproduction here.

### F3 — HIGH: stopping MLX processes can lose their shutdown owner (#868/#876)

In #876 `Cotabby/Services/Runtime/MLX/MLXServerManager.swift:394–405` moves the
broken server into one `stoppingProcess` slot. `460–462` returns from
`stopCurrent` when `current` is nil without awaiting that slot; a later
`473` overwrites it. Model changes can leave an older process in its grace
period outside the synchronous shutdown owner's reference.
**Fix:** serialize launch behind all pending stops, or track every stopping
process until exit. Test reportBroken → multiple model switches → immediate quit.
Also `MLXToolchainProvisioner.swift:61–66,120–137` permits an older asynchronous
refresh to publish after remove, because only activeInstall is checked.
**Fix:** version all locate/refresh/remove operations, not just installs.

### F4 — MEDIUM: unchanged hyphenated names produce rewrite change warnings (#869/#870)

`Cotabby/Support/Rewrite/RewriteSpecificsCheck.swift:49–50` splits source words
at all non-alphanumeric characters, but `55–63` retains `Jean-Luc` as a name
in output. At `21–23`, that name cannot match source tokens `jean` and `luc`.
An unchanged name is marked invented, disabling ordinary Replace/Return.
**Fix:** use the same tokenizer for both sides, preserving internal name
hyphens/apostrophes; regression: `send it to Jean-Luc` → `Send it to Jean-Luc.`
Do not regress the fixed `Sam`/`same` substring case.

### F5 — HIGH: memory restart does not await the stop it just started (#871)

`Cotabby/Services/Memory/MemoryEngineController.swift:133–135` calls stop then
start; `149–155` clears startTask and returns a detached stop task. The next
start at `199–206` sees nil instead of the pending stop. A new store/model can
open before the old engine finishes releasing resources. The same ordering
needs protection through deletion, not merely startup-generation checks.
**Fix:** retain one ordered lifecycle task covering start, stop and deletion;
queue a new start behind it. Test with a gated stop and toggle/restart/delete.

### F6 — HIGH: shared arrival cursor can permanently skip cloud edits (#871)

`Cotabby/Services/Memory/Readers/CloudDocuments/CloudDriveReader.swift:90–95`
changes evicted online-only files to modification-time keys, then compares them
with the drive-wide after cursor. A file arriving with modification time 5500
after another file advanced the cursor to 6000 is excluded even if its local
arrival time is 7000. Later syncs keep excluding it.
**Fix:** remember each file's content version/last-read modification time,
separately from arrival pagination; test offline edits and restored versions.
#877 retains the same class of issue at `96–100` for every seen file.

### F7 — MEDIUM: discarded answer preparation is permanently marked prepared (#872)

`Cotabby/App/Coordinators/Answers/AnswerCoordinator.swift:223–232` correctly
withholds an offer when memory's stamp changes during drafting. But the task
completion at `189–193` sets preparedIdentity even after that discard, and
`180` suppresses future attempts for the unchanged reply.
**Fix:** return a typed preparation outcome; retry under the new stamp rather
than marking a stale attempt complete. Test memory change while draft is gated.
This is not the earlier unversioned cached-answer privacy bug, which is fixed.

### F8 — HIGH: latency optimization treats changed facts as OCR noise (#875)

`Cotabby/Support/Context/VisualContextChangePolicy.swift:21,46–54` treats lines
with Jaccard overlap >= 0.5 as unchanged; `62` drops lines with fewer than three
words. `Auto sync disabled` → `Auto sync enabled` has overlap 0.5; a changed
price in a longer sentence also passes. A new `Sounds good` line disappears
entirely. These are actual fact changes, not safely recognizable OCR noise.
**Fix:** preserve numeric/status changes and short meaningful messages; impose
a maximum stale age even when filtering noise. Do not buy cache continuity by
keeping old facts. Source-level policy examples independently evaluated with
Python; not a Swift runtime or Windows latency benchmark.

### F9 — HIGH: incremental Outlook cache can pair an old summary with new bytes (#877)

`Cotabby/Services/Memory/Readers/OutlookHxStore/HxStoreFile.swift:153–167`
decodes the shared mapped record, summarizes it, then hashes the mapping.
A concurrent Outlook write can occur between those steps. The cache then
matches the new digest but retains old metadata; `186–187` can exclude updated
mail under sentAfter on later scans.
**Fix:** decode an immutable copied record, or hash before/after and only cache
when equal; a deterministic mutation seam should change the record between
decode and digest and prove later scans recover it. Static race analysis only.

### F10 — HIGH: cloud pagination fails to advance seen-state with each page (#877)

`Cotabby/Services/Memory/Readers/CloudDocuments/CloudDriveReader.swift:96,163`
persists the incoming seen set on intermediate pages without adding attempted
new files. Hydration/eviction can update their arrival time so subsequent pages
see them as new again and redownload. At `146,164`, incomplete listings replace
the old set with the partial listing, forgetting temporarily inaccessible files.
**Fix:** add attempted keys per page (keep failures retryable); replace the
full seen set only after a complete listing. Test page limits, hydration ctime
changes and temporarily unreadable accounts.

### W1 — HIGH, FIXED in this Windows candidate: unsupported live focus still displays old output

`src/Cotabby.Core/Suggestions/SuggestionCoordinator.cs` previously rejected a
changed live field only when Refresh returned a non-null snapshot. A focus move
to an unsupported/search field made Refresh return null, so the old field's
completion appeared at its old anchor. The controlled delayed-engine regression
failed before the fix (overlay visible) and passes after rejecting null/secure
live focus before first presentation. No keyboard event is replayed or invented.

## What the candidate changes

- Adds pure SearchFieldDetector with whole-word labels and punctuation/camel-case
  identifier segmentation. Search/Find/Filter/Look for/Jump to and Turkish
  Ara/Arama are recognized; Research and Finder are not substring matches.
- UIA rejects those Edit/ComboBox fields before TextPattern/ValuePattern payload
  reads and publishes no completion snapshot. This is an unsupported field, not
  a mislabelled credential field. Existing credential rules remain unchanged.
- Adds the W1 delayed-result regression and fail-closed first-presentation fix.
- Adds a real Windows UIA fixture with an ordinary-field positive control.
- Keeps existing settings, completion-length defaults, native runtime layout and
  local-only inference. No new dependencies, installers or background services.

Known adaptation limits: UIA control types do not reliably distinguish every
multiline editor; unlabeled or differently localized search boxes may evade the
heuristic. Search-labelled browser address bars are intentionally left alone too;
no DOM parity or universal search detection is claimed.

## Verification and testing

- Baseline local Core suite: 418 passed.
- Detector RED: nine expected behavioral failures against the false-return stub;
  five negative cases already passed. GREEN: all 14 detector cases passed.
- W1 RED: unsupported-field case showed a visible overlay; GREEN: all four
  delayed-focus cases passed.
- Full local Core suite after fixes: 433 passed, zero skipped.
- Upstream #864: catalog validation and five Python tests passed. An initial
  unittest discovery in scripts/ found zero tests; the correct scripts/tests/
  command was then run. Zero discovery is not counted as coverage.
- Windows CI must compile/test the exact candidate, run the production UIA/
  insertion/settings/hook fixtures, publish self-contained x64, extract the ZIP,
  verify its member manifest and load the CPU native DLL. BUILD-INFO.txt records
  the packaged source commit and test count. Only that passing run is deliverable.
- No physical keyboard, live model, real Teams/Outlook/browser, GPU correctness,
  or latency benchmark has been exercised by the agent. Those remain owner tests.
- Independent Nous worker startup was blocked by `Gateway draining:
  update_paused`; this review is parent-executed, not independent-model coverage.

## Standards / spec axis (separate from defects)

- Local-only inference rule (AGENTS.md / PROJECT_HISTORY.md): preserved; upstream
  cloud rewrite paths are excluded. This is a policy boundary, not a claim that
  cloud support is itself an upstream bug.
- Metadata-before-payload credential boundary: preserved; search policy also runs
  before the value read and does not weaken credential detection.
- Folder-based publish: retained; no single-file executable or native flattening.
- No MLX/memory/answer/localization/latency feature is advertised as present.
- Judgement: full stack adoption would introduce several new lifecycle owners at
  once. Split rewriting, memory and latency into separately approved vertical
  slices rather than a single huge port. This is a maintainability recommendation.

## Next design decisions (not blockers for testing this limited candidate)

Round 1: Should a future Windows rewrite feature be strictly local-only (current
policy), and copy-only when selection identity cannot be proved? Which real apps
must be supported first?

Round 2: Which memory sources are actually wanted, what Windows encryption and
consent model is acceptable, and how should deletion work while a model is off?
No store reader should be enabled merely because an upstream PR implements one.

Round 3: Define a Windows latency benchmark (model, GPU/CPU, input cadence,
p50/p90 and output correctness). Optimize against that result, not Mac numbers.
