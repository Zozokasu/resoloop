// Keep this hook short; docs/dev/agent-workflow.md owns the operational policy.
// UserPromptSubmit and PostToolUse/Skill wiring stays in .claude/settings.json.
import { readFileSync } from 'node:fs';

let input;
try {
  input = JSON.parse(readFileSync(0, 'utf8'));
} catch {
  process.exit(0);
}

const event = input?.hook_event_name;
const isCcgGo =
  (event === 'UserPromptSubmit' && /(^|\s)\/ccg:go(\s|$)/.test(String(input.prompt ?? ''))) ||
  (event === 'PostToolUse' && input.tool_name === 'Skill' && String(input.tool_input?.skill ?? '') === 'ccg:go');

if (!isCcgGo) process.exit(0);

const context = `[ResoLoop policy 2026-10-02-delegation-v2; supersedes the 2026-10-01 single-owner implementation mode]
- Read docs/dev/agent-workflow.md: it owns roles, delegation, risk tiers, execution and the S3 live/feedback pilot. AGENTS.md owns architecture and Resonite safety. Preserve ccg's normal lifecycle and all other HARD STOP gates. Announce this user-approved execution mode; do not ask for mode selection again.
- Strict operating boundaries with flexibility inside them: preserve goals, shared contracts, granted rights, file ownership, verification and evidence-based acceptance. Adjust models, effort, task decomposition, context and concurrency only inside granted rights.
- Opus 5.5/high owns goals, shared contracts, grants, milestones and acceptance. Codex gpt-6.1-sol/high is the technical manager and follows .claude/agents/feature-manager.md. Neither writes product implementations or fixes. Delegate them, including product-code conflict resolution, to workers following .claude/agents/coding-worker.md. Routine information requests, local fixes and model changes stay manager-to-worker; escalate grant/contract/goal changes.
- Give workers self-contained, versioned contracts with separate read/write scopes, risk, dependencies/ownership, base commit and dirty-file hashes, acceptance, verification, limits and evidence paths. Start with 1-2 coding workers; use at most 3 when independent. Codex coding and fixes run on gpt-6-luna/high; log retrieval, build/test runs and their summaries run on gpt-6-luna (user decision 2026-10-08). Keep design, decomposition and focused review with the manager; write luna contracts short and explicit (one goal, few files, numbered steps, exact paths/commands, concrete expected values), include tests in every implementation task and keep test files small. Record effective model and effort; verify delegation/collection/stop capabilities rather than assuming them.
- Worktrees, workspace-write and small context do not enforce restricted reading. Record the actual enforcement. Strict-read tasks require a verified isolated environment or a tool-free snippet call; never silently downgrade. Missing context is requested and supplied within grants. Apply only against verified bases and reject out-of-scope changes.
- One editor/applicator per file, one builder per tree, one live run. Verify relevant tests and inspect diffs; high-impact state/ownership/deletion/session/concurrency/public-contract changes need focused independent review before integration. Keep phase-end review and distinguish exact reviewed commits from later fixes. DONE alone is not acceptance; skips and unrun live tests remain unverified.
- The first pilot is S3 live testing and feedback fixes. Record the exact S3 baseline, reproducible findings, fixes and offline/live evidence. This policy switch is not live authorization. Require an authorized session, exact ResoLoop_Test* target, operations and cleanup; re-observe after reconnect. Routine fixes proceed within the charter without repeated Opus decisions.
- Keep the direct authoring path and AGENTS.md's README/skill/ContractGen rules. Preserve user changes and untracked files. Do not edit openspec/ (the plan of record), ccg managed files or trust settings without authorization; plan/ is history and must not be changed. No unsolicited commit/push/release; main merge requires user approval. Do not bypass sandbox limits; report unavailable capabilities and dispatch needs.`;

process.stdout.write(JSON.stringify({
  hookSpecificOutput: { hookEventName: event, additionalContext: context },
}));
