# Selectively port upstream features

## Problem

The original Cotabby macOS project continues to evolve, but its history and
platform APIs are separate from the Windows port. A full merge would risk
breaking the WPF, UI Automation, Win32, and LLamaSharp implementation.

## Scope

- Maintain a documented, feature-by-feature upstream comparison process.
- Prioritize portable prompt, autocomplete, model, and inference improvements.
- Rewrite UI and platform behavior as Windows equivalents when justified.
- Preserve local inference and the folder-based native runtime packaging.
- Give future AI agents a repeatable implementation and handoff prompt.

## Non-goals

- Wholesale history synchronization.
- Importing SwiftUI, AppKit, or Apple-only features into the Windows build.
- Adding cloud inference, telemetry, or single-file native packaging.

## Acceptance criteria

- `UPSTREAM_PORTING_PLAN.md` identifies the repository lineage and current
  baselines.
- The plan defines priorities, non-goals, per-feature workflow, verification,
  and handoff requirements.
- A future agent can execute the embedded prompt without needing undocumented
  project context.
- README links to the plan and CHANGELOG records it.
- Documentation changes pass `git diff --check` and do not include generated
  artifacts or credentials.

## Rollback

Revert the documentation commit. Feature implementations must remain separate,
focused commits so an individual port can be reverted without affecting the
Windows packaging or existing model catalog.
