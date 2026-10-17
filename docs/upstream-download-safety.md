# Selective Windows-upstream download hardening

Source: [Msde-7/cotabby-windows e625947](https://github.com/Msde-7/cotabby-windows/commit/e625947), inspected through b1e4031c70aca1be21aae2c57b138fde5fc22bb1.
Only ModelDownloader behavior is adapted, not the endpoint engine, clipboard
context, custom models, settings rewrite, or typing history.

## Contract

- A 200 response replaces a partial instead of appending another full file.
- A 206 response must declare bytes beginning at the requested offset, ending
  at total minus one, with consistent Content-Length when supplied.
- Actual bytes must equal the declared total. Without a response length,
  they must equal the catalog size exactly.
- A 416 promotes only a nonempty partial with an explicit matching remote
  total and a plausible model signature/size. Otherwise it fails with guidance
  to remove the .part file and retry.
- Cached/final files need GGUF magic plus the historical 85% catalog minimum.
  A bad final file is replaced only after a fresh download passes validation.
- Cancellation or truncation does not promote a partial. Invalid model
  signatures delete the unusable partial. HTTP failures preserve it.
- No response bodies, redirected URLs, clipboard data or user text are logged
  by this downloader. All inference remains local.

These are sanity checks, NOT full GGUF parsing or authenticated checksums.
Same-length server file changes during resume are not detected. Delete the
partial and restart if the server model changed. Cache-size tolerance remains
for compatibility with earlier releases, and is not proof of completeness.
The existing UI serializes its download flow; multi-process writers to one
model cache are not supported by this change.

## Acceptance and rollback

14 deterministic tests cover ignored ranges, valid resumption, invalid offsets,
truncation, signatures, completed/invalid 416 partials, bad existing finals,
cached reuse, HTTP failure, cancellation and unknown lengths. Existing Windows
runtime proofs remain required. Rollback: revert the downloader port commit;
no settings or model-catalog migration is involved.

## Owner Windows test

Extract the complete new ZIP into a fresh folder; keep runtimes/win-x64 beside
Cotabby.exe. Run as a standard user. Existing settings/model cache are reused.
Confirm the current cached model still loads and gives suggestions. If testing
a fresh download, choose a model not already cached (nano is smallest). Cancel
partway through, retry, and confirm download completion and a model-loaded log
entry followed by a suggestion. Do not share raw logs. Do not delete your only
working model merely to test downloads. Live Hugging Face transfers, physical
keys and real-model inference remain owner validation, not CI claims.
