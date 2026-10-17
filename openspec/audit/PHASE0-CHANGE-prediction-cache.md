# Prediction cache

**Target phase:** 2 (after `confidence-gating` defines admission quality).
**Upstream provenance:** none. **Risk level:** Medium — caching can serve
stale text if keyed wrong. **Flag:** `EnablePredictionCache` (default
`false`), `PredictionCacheMaxEntries` (default `64`).

## Why

Every keystroke burst re-decodes from scratch. The existing KV prefix
reuse (`LlamaRuntimeCore.cs:134-155`) already avoids re-evaluating shared
prompt tokens, but when the user backspaces and retypes — or re-enters a
repeated context (boilerplate lines, function signatures, greeting
formulas) — the full decode runs again for a result the app computed
seconds ago. A small in-memory cache keyed on the exact request fingerprint
(prefix + suffix + options) collapses those repeats to a lookup, cutting
battery drain (Phase 1 `battery-aware-inference` makes this more
valuable) and time-to-suggestion.

## What Changes

- New pure `Cotabby.Core.Suggestions.PredictionCache`: bounded LRU keyed
  on a hash of the *request fingerprint* (`Prefix` + `Suffix` +
  `SingleLine` + `MaxTokens` + `UserName` + `LanguageInstruction` —
  everything `SuggestionRequestFactory.Build` varies,
  `SuggestionRequestFactory.cs:28-57`); values are normalized suggestion
  text + confidence score. **Memory-only** — never written to disk.
- Admission rules (tied to Phase 2 `confidence-gating`): only suggestions
  that passed normalization AND (when gating is enabled) cleared the
  confidence threshold are cached; suppressed results are never cached.
- Invalidation: entries evict LRU beyond `PredictionCacheMaxEntries`;
  clearing on model change (`AppHost.TryLoadCachedAsync`/`EnsureModelReadyAsync`
  `:177-201` set `RuntimeModelId`, which invalidates the cache) and on
  settings changes that affect requests (completion length, language,
  user name).
- `LlamaSuggestionEngine.GenerateAsync` (`:35-117`) consults the cache
  before building the prompt; a hit yields the cached chunk immediately
  (same `SuggestionChunk` contract, no fake streaming — one final chunk,
  matching the engine's existing one-shot semantics `:8-21`).
- Cache-hit/miss counters feed the Phase 1 statistics funnel
  (`CacheHit`/`CacheMiss`) — no text, counts only.
- `AppSettings.EnablePredictionCache` (default `false`),
  `PredictionCacheMaxEntries` (default `64`).

## Non-goals

- No disk persistence (a durable text cache would be typing-history in
  disguise — the exact thing upstream #841 was declined for; the cache
  dies with the process).
- No cross-field sharing: the fingerprint includes prefix/suffix, so the
  same *text* in a different app can collide only if the fingerprint
  matches — which is the point (same context → same suggestion) — but
  never across models or settings.
- No cache for credential fields (impossible: no requests are created
  there — `TriggerGeneration` `:460`).

## Proposal outline (proposal.md)

```markdown
## ADDED Requirements

### Requirement: Identical recent requests may reuse a cached suggestion

The app MAY, when the user enables the prediction cache, serve a
previously computed suggestion for a byte-identical request fingerprint
instead of re-decoding. The cache MUST be in-memory only, MUST be bounded,
MUST admit only non-suppressed suggestions, and MUST invalidate on model
or request-affecting settings changes.

#### Scenario: Repeated context hits the cache
- **WHEN** the user backspaces and retypes an identical prefix/suffix
- **THEN** the cached suggestion is returned without a decode

#### Scenario: Model change invalidates
- **WHEN** the active model changes
- **THEN** the cache is cleared and the next request decodes fresh

#### Scenario: Cache is never persisted
- **WHEN** the app exits
- **THEN** no suggestion text from the cache remains on disk
```

## Source files touched (verified)

| File | Change |
| --- | --- |
| `src/Cotabby.Core/Suggestions/PredictionCache.cs` | **new** — bounded LRU + fingerprint |
| `src/Cotabby.Inference/LlamaSuggestionEngine.cs:35-117` | cache consult before decode |
| `src/Cotabby.Core/Suggestions/SuggestionRequestFactory.cs:28-57` | fingerprint covers all varied fields |
| `src/Cotabby.App/Hosting/AppHost.cs:177-201` | invalidate on model change |
| `src/Cotabby.App/Settings/AppSettings.cs` | add `EnablePredictionCache`, `PredictionCacheMaxEntries` |
| `src/Cotabby.App/Hosting/AppHost.cs:133-169` | invalidate on settings change |
| `tests/Cotabby.Core.Tests/PredictionCacheTests.cs` | **new** |

## Risks

- **Stale-text serving**: fingerprint must include every field that can
  change the result; tests pin the fingerprint against each factory
  input. A fingerprint miss is safe (just a re-decode); a fingerprint
  *collision* is the hazard — SHA-256 over a canonical serialization,
  collision risk negligible.
- Confidence drift: a cached entry keeps its score; if the threshold
  setting changes, the cache invalidates (settings-change invalidation
  covers it).
- Memory: 64 entries × short strings is bounded and trivial; cap enforced
  by construction.

## Test plan (unit)

`tests/Cotabby.Core.Tests/PredictionCacheTests.cs`:

- `IdenticalFingerprint_ReturnsCachedText`
- `DifferentPrefix_Misses`
- `DifferentSuffix_Misses`
- `DifferentMaxTokens_Misses`
- `DifferentUserNameOrLanguage_Misses`
- `BeyondMaxEntries_LruEvictsOldest`
- `SuppressedSuggestion_IsNeverCached`
- `ClearOnInvalidation_RemovesAll`
- `CacheIsMemoryOnly_NoPathProperty` (reflection: no IO surface)
- `ConcurrentReadWrite_IsThreadSafe` (cache consulted from work thread,
  mutated from engine; lock discipline)

## Manual Windows smoke steps

1. Enable; in Notepad type `Hello wor` → note the suggestion; backspace
  to `Hello w` and retype `or` → the same suggestion returns instantly
  (log shows cache hit).
2. Change the completion length preset → next request decodes fresh
  (invalidation works).
3. Switch models → cache cleared; first suggestion after switch is a
  miss.
4. Confirm `%APPDATA%\Cotabby\` and `C:\tmp\` contain no new files after
  heavy cache use (memory-only).
5. Disable → every request decodes (funnel `CacheMiss` counts resume).

## Rollback

- `EnablePredictionCache=false` → engine skips the cache entirely; the
  class is never consulted.
- Code revert removes the cache and its consult; decode path returns to
  the v0.4.1 shape.

## Privacy & security constraints

- **Memory-only, process-lifetime**: the cache structurally cannot become
  a durable text store — the #841 privacy boundary is respected by
  construction (asserted by test: no `Path`, no IO).
- No credential-field exposure: requests for such fields never exist, so
  nothing to cache.
- Admission requires non-suppressed + confidence-passing output — never
  raw or echo-y text.
- No cloud calls; local memory only.
