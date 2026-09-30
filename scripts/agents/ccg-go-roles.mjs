// Project hook: injects the agent role structure (docs/dev/agent-workflow.md: Codex single owner, Sonnet fallback, phase review) whenever /ccg:go runs.
// Wired in .claude/settings.json for UserPromptSubmit (user types /ccg:go) and PostToolUse on the Skill tool
// (the model invokes the ccg:go skill). It never edits ccg-managed files; it only adds context.
import { readFileSync } from 'node:fs';

let raw = '';
try {
  raw = readFileSync(0, 'utf8');
} catch {
  process.exit(0);
}

let input;
try {
  input = JSON.parse(raw);
} catch {
  process.exit(0);
}

const event = input.hook_event_name;
const isCcgGo =
  (event === 'UserPromptSubmit' && /(^|\s)\/ccg:go(\s|$)/.test(String(input.prompt ?? ''))) ||
  (event === 'PostToolUse' && input.tool_name === 'Skill' && String(input.tool_input?.skill ?? '') === 'ccg:go');

if (!isCcgGo) process.exit(0);

const context = `[ResoLoop role structure for /ccg:go — docs/dev/agent-workflow.md is authoritative; AGENTS.md owns architecture and Resonite safety]
- Direction: the main authoring path is direct (TSX / hand-written JSON / ProtoGraph -> ResoLoop -> RLoop.ResoniteLink on one selected session, or RLoop.Flux -> IFluxDeployer -> F# deployer -> Flux-SDK). The Workbench backend (src/RLoop.Workbench, --backend workbench, wb) is frozen: no new features, write adapters, 64-op batch, RPC export, or backend parity work; no automatic backend fallback; it is slated for removal in S5; old W4 is not resumed (plan/resoloop-slimming-plan.md S1-S5 replaced old W4-W7).
- Opus (you, main session) = producer/chair: goals, priorities, shared contracts (Core public types, IResoniteClient, CLI args, error codes, state), charters, acceptance, merge decisions, conflict resolution between owners, whether and against what live tests run. Do NOT do file-level implementation. Delegate your own lookups to the scout subagent. Ask the user only about goals, requirements, or tolerances.
- Standard unit (user decision 2026-10-01) = one Codex gpt-6.1-sol owner with high reasoning effort directly under Opus. .claude/agents/feature-manager.md remains authoritative for receiving charters, report format, branch/commit rules, and log locations; explicitly tell the Codex owner to read and follow it. Charter = goal, editable files, contracts, acceptance criteria, working tree/slot and branch, tracker path .ccg/tasks/<task>/assignments.md, files owned by others, commit permission, verification commands, log location and report file. The owner investigates, implements, runs relevant build/test, commits on agent/* in its worktree slot, and reports briefly. Full build/test logs go to .ccg/tasks/<task>/ unless the charter specifies another location; report a ~10-line summary (counts, failed test names with one-line reasons, skip count, duration). Sonnet subagents are only a fallback when Codex is unavailable: Agent(subagent_type: "feature-manager"), or general-purpose + model "sonnet" following the same rules.
- Confirmed launch form (codex-cli 0.159.2): codex exec --model gpt-6.1-sol -c model_reasoning_effort=high --sandbox workspace-write -C "<worktree slot>" -o "<report file>" - < "<charter.md>" & . A worktree's Git administration lives in the main repo's .git, which the Codex sandbox cannot write even with --add-dir (index.lock denied, verified 2026-10-01): the Codex owner leaves changes uncommitted and proposes commit splits and subjects; Opus inspects the diff and commits on the slot's agent/* branch on its behalf. Opus launches this in Bash background execution and accepts the -o report plus the slot commit after checking the diff and build/test evidence. There is no --effort argument. Write the charter to a file and pass it via stdin: on Windows a multiline argument delivers only its first line. Network is blocked inside workspace-write; Opus must run NuGet restore or npm ci outside the sandbox first, then charter dotnet build ResoLoop.slnx --no-restore.
- Devin SWE-2 Max (devin --model swe-2-max --permission-mode accept-edits -p --prompt-file ...) remains optional for large parallel implementation or long builds/tests outside the sandbox. It cannot be launched from the Codex sandbox because network is blocked; return the need to Opus for outside-sandbox coordination. When used, follow agent-workflow.md (self-contained prompt file, allowed exact commands only, cannot read outside its worktree, owner commits).
- Do not run Codex review after each implementation or fix. Accept implementation units with the owner's build/test and Opus's diff inspection. When a phase (S1, S2, ...) is substantially complete, run one heavy Codex review (via smart-friend or codeagent-wrapper --backend codex) over the entire phase diff, start commit..current main. Focus on contract violations, state/ownership, deletion, public JSON and error-code compatibility, and concurrency; name the exact start and main commit hashes and distinguish later changes. The user decides whether to enter a fix round. Re-review after fixes only when major findings changed the design; no Codex recheck for minor fixes. For fix rounds or owner changes hand over only the tracker, diff, existing evidence, and open questions; do not re-run verification for the same commit, environment, and test set.
- One editor per file at a time; one building agent per working tree; contracts are settled before parallel work; only one live run at a time, only under a ResoLoop_Test* Slot, cleaned up by exact ID. Skipped live tests are reported as unverified, never as passed.
- When CLI behavior changes, README examples and skills/codex/*/SKILL.md (shipped product files) must be synced; assign them a single owner.
- Execution-mode selection (ccg strategies' "choose Agent Teams / Codex / Claude" gate): in this project the user has standing-approved the mode "Codex gpt-6.1-sol/high single owner, Sonnet fallback, optional Devin outside the sandbox, Codex review at phase completion". Announce it instead of asking; ask the user only if they requested a different mode or the task changes goals/requirements. All other HARD STOP gates stay as the strategy defines them.
- Keep ccg's own task lifecycle (.ccg/tasks/<task>/task.json phases, resume, completion). Never edit plan/ or ccg-managed files unless the user asks; feedback/ holds proposals and never replaces plan/. Assignees commit only on agent/* branches; merging to main (--no-ff) needs the user's approval. No push or release without a request.`;

process.stdout.write(
  JSON.stringify({
    hookSpecificOutput: { hookEventName: event, additionalContext: context },
  }),
);
