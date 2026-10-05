# Deterministic offline verification

Run fixed commands directly with this runner; delegate implementation fixes and judgments to the assigned worker/manager. Repeated agent calls are unnecessary for tool invocation, count parsing and evidence collection. Requires PowerShell 7.2+, locally installed tools and previously restored .NET/npm dependencies. It never restores, installs, enables live tests, mutates Git, or reuses cached evidence.

```powershell
pwsh -NoProfile -File scripts/agents/verify.ps1 -Profile All
pwsh -NoProfile -File scripts/agents/verify.ps1 -Profile Dotnet -TestProject RLoop.Tests -TestFilter 'FullyQualifiedName~CheckpointWriteContentionTests'
pwsh -NoProfile -File scripts/agents/verify.ps1 -Profile Jsx
pwsh -NoProfile -File scripts/agents/verify.ps1 -Profile JsxContract
pwsh -NoProfile -File scripts/agents/verify.ps1 -Profile Inspect -SourceRef agent/example
pwsh -NoProfile -File scripts/agents/verify.tests.ps1
```

| Profile | Selected work |
| --- | --- |
| All (default) | Fresh .NET Debug build, offline tests, one TypeScript compile, Node tests, cross-language contract |
| Dotnet | `dotnet build ResoLoop.slnx --no-restore --no-incremental`, then tests of RLoop.Tests, RLoop.Workbench.Tests and RLoop.IntegrationTests with `--no-build --no-restore` |
| Jsx | `node node_modules/typescript/bin/tsc -p tsconfig.json`, then `node --test --test-reporter=tap "dist/test/**/*.test.js"`; no dotnet dependency |
| JsxContract | Jsx plus `node dist/scripts/contract.js`; requires a prebuilt CLI and dotnet |
| Inspect | Source snapshot and tool observation only; optional ref resolution, merge base and overlapping changed paths |

.NET test filters always include `Category!=Integration`, including the offline isolation tests in the Integration assembly. Optional filters are ANDed with that exclusion. `-TestProject` selects All (default) or one of the three named test projects, only for All/Dotnet profiles. A filter must execute at least one passing test in every selected project; use the selector for a narrow filter. Filters mentioning Integration or command/live options are refused. Every child process receives an environment with `RESOLOOP_RUN_*`, `RESOLOOP_*LIVE*`, `RESOLOOP_*INTEGRATION*` and `RESONITE_LINK_URL` removed; the parent environment stays unchanged. Live is always skipped and unverified. Existing npm scripts remain unchanged. All refuses the contract if its newest-DLL selection would choose anything other than the freshly built `src/RLoop.Cli/bin/Debug/net10.0/resoloop.dll`. JsxContract records the selected prebuilt DLL path/hash/time without claiming a fresh build or source correspondence.

Each run creates a unique `TestResults/agent-verification/run-<UTC>-<UUID>/` directory. Full stdout/stderr stream separately to files; command arguments, directories, UTC times, exit codes, counters, tool version logs, CLI identity and statuses are recorded in `summary.json`. `before.json`/`after.json` contain exact HEAD, committed tree, branch, Git status and SHA-256 fingerprints of tracked/untracked nonignored files. Normal build outputs still go to the tools' existing ignored bin/obj/dist directories. The persistent lock file under `TestResults/agent-verification/` is held exclusively by the process and released on exit/crash; the runner never deletes directories. Interrupted command cleanup kills and joins a surviving child process tree and observes output-copy tasks before releasing the lock. Abruptly terminating the runner itself cannot guarantee child cleanup.

Fingerprint exclusions are explicit: Git-ignored files, `TestResults/`, `.vscode/`, `.aws/`, `.codex/`, `.agents/`, `.devin/`, `.ccg/`, `.claude/settings.local.json`, and basenames `.env`, `.env.*`, credential/credentials/secret/secrets (including dot suffixes). Excluded paths and Git status are recorded without opening those files. Their contents are **not** byte-protected by this runner. Source reparse points fail closed. No environment values are recorded.

Exit 0 means selected steps passed with a stable source snapshot; Inspect instead reports `inspected`, with build/test unselected. Nonzero process exits, missing/unparseable counters, zero execution, failures, or source drift produce exit 1. TRX XML rejects DTD/external entities. Skipped test counts stay separate from passed counts. A failure stops later commands and marks them `unrun`; evidence is retained. Missing prerequisites never trigger fallback build/install. No test removal is automated.

Inspect is observation, not approval, a conflict guarantee or merge decision. Merge decisions, Git mutations and live authorization remain with the user/manager. The lock coordinates only this runner: unrelated builds/edits can still interfere, which source/CLI checks catch only where observable. The focused fixture suite tests gating without running the repository pipeline; integration verification remains the manager's responsibility.
