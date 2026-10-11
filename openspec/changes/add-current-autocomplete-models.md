# Add current autocomplete models

## Problem

The original catalog was inherited from the Windows port and offered useful
small and large models, but it lacked a current middle-sized general model and
a dedicated coding model.

## Scope

- Add Qwen3.5 4B Base Q4_K_M as the general-purpose default.
- Add Qwen3.5 9B Coder Q4_K_S as the programming-focused option.
- Keep existing model IDs and cached files working.
- Document provenance, release packaging, and agent handoff rules.

## Non-goals

- No cloud inference.
- No removal or migration of existing cached models.
- No change to the prompt pipeline or LLamaSharp runtime in this change.

## Acceptance criteria

- Both new Hugging Face URLs return HTTP 200 and their catalog sizes match the
  served `Content-Length`.
- Both entries expose FIM capability to the existing prompt pipeline.
- Core tests pass.
- README, changelog, project history, and `AGENTS.md` describe the change.
- The release workflow continues to publish a folder-based self-contained ZIP.

## Rollback

Remove the two new entries and their documentation rows. Existing model IDs and
cache files remain unaffected.
