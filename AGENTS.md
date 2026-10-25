# Instructions for every coding AI working on this repository

Read `AI-START-HERE.md`, `VERSION.json`, `ARCHITECTURE.md`, and `WINDOWS-ACCEPTANCE.md` before changing code. The user wants low-friction LAN file exchange for Windows 11, ultimately embedded in a downloader.

## Product invariants

- Each pane displays a real directory owned by one device. Listing or refreshing never writes or transfers files.
- No automatic bidirectional folder mirroring, cross-device mtime arbitration, or conflict-triggered reverse overwrite.
- Cross-device dragging copies by default and preserves the source. A later source deletion must not delete an independent received copy.
- Mutations use explicit operation IDs, owner-local identity/version checks, and durable deduplication. Old requests must not act on same-name replacement files or directories.
- Receive into same-volume staging outside the share; verify content before commit. Do not show incomplete data as a completed file.
- Automatically discover devices, but require one-time explicit pairing before shared-directory access. Never expose the whole drive merely because a peer is on the same LAN.
- Keep real Windows shell drag/drop. Do not silently replace it with URLs, remote path strings, a web-only prototype, or manual FTP steps.

## Workflow

1. `git fetch origin --tags`; inspect the current `origin/main`, working tree, other branches, and open PRs. Preserve others' work.
2. Start a focused `ai/<short-topic>` branch from the current development baseline. Never force-push shared branches or move a published version tag.
3. Implement a concrete change and meaningful regression coverage when the behavior warrants it. Avoid timeout-based correctness patches.
4. Run `dotnet run --project tests/LanBridge.Tests -c Release`; build the Windows project. Native API and shell changes additionally require the Windows CI job / drag COM self-test.
5. Open a PR with the problem, resulting behavior, evidence, and remaining limits. Do not ask KAMI to manually relay raw code between AIs.
6. For significant changes or a new distributable test version, update `VERSION.json`, `CHANGELOG.md`, and `docs/releases/<tag>.md` together. Use a new alpha version until Windows 11 dual-PC acceptance actually passes.
7. Push the branch and report the PR/commit/run URL. Once changes reach `main`, the workflow tests them. A new version is automatically published only after all required jobs pass.

The user has authorized uploading meaningful versions and test results to this repository. Do not repeatedly ask for upload permission for this authorized scope. This does not authorize deleting unrelated work, force-pushing, changing repository visibility, or disabling required checks.

## Evidence and releases

- `main` is the latest development state, not a blanket guarantee of stability.
- A published tag and its `release-manifest.json` identify an exact, reproducible build. Always record the exact SHA you read or test.
- `VERSION.json` declares the intended version; a missing release means publication has not completed. Never claim a candidate is already published or passed.
- Linux tests, Windows Server CI, Windows COM self-tests, and physical Windows 11 dual-PC acceptance are different evidence levels. Label them accurately.
- Failed checks remain failures. Fix the demonstrated issue; do not skip a required test, weaken an assertion, or claim manual validation to get a release.
- Published release assets and tags are immutable by project policy. A correction gets a new version; no silent asset replacement.
- Do not add exe/zip/bin/obj/runtime caches to Git history. Executables belong in Release assets; logs belong in CI artifacts and selected dated reports.
- Never commit identity certificates, paired-device data, local personal files, tokens, or private machine logs.

## Commands

```text
dotnet run --project tests/LanBridge.Tests -c Release
dotnet build src/LanBridge.Windows/LanBridge.Windows.csproj -c Release
dotnet publish src/LanBridge.Windows/LanBridge.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o release/win-x64
release\win-x64\LanBridge.exe --self-test-drag --headless
```

The final command requires Windows; inspect its process exit code and `drag-self-test.txt`. No new agent should restart the obsolete synchronization architecture described in the earlier conversation.
