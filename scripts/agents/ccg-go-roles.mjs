// Project hook: injects the agent role structure (docs/dev/agent-workflow.md: Sonnet single owner, optional Devin, high-impact-only review) whenever /ccg:go runs.
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
- Opus (you, main session) = producer/chair: goals, priorities, shared contracts (Core public types, IResoniteClient, CLI args, error codes, state), charters, acceptance, conflict resolution between owners, whether and against what live tests run. Do NOT do file-level implementation. Delegate your own lookups to the scout subagent. Ask the user only about goals, requirements, or tolerances.
- Standard unit = one Sonnet owner: spawn with Agent(subagent_type: "feature-manager") (fallback: general-purpose + model "sonnet", told to follow .claude/agents/feature-manager.md). Charter = goal, editable files, contracts, acceptance criteria, working tree/slot, tracker path .ccg/tasks/<task>/assignments.md, files owned by others, commit permission. The owner investigates, implements, runs relevant build/test, and reports briefly, working on an agent/* branch in a worktree slot and committing there. Full build/test logs go to .ccg/tasks/<task>/; report a ~10-line summary (counts, failed test names with one-line reasons, skip count, duration). Spawn independent owners in one message.
- Devin SWE-2 Max (devin --model swe-2-max --permission-mode accept-edits -p --prompt-file ...) is an optional tool the owner may choose for large parallel implementation or long builds/tests; when used, follow agent-workflow.md (self-contained prompt file, allowed exact commands only, cannot read outside its worktree, owner commits).
- Separate reviewers (Codex via smart-friend or codeagent-wrapper --backend codex) only for high-impact changes: deletions, ownership boundaries, state migration, lost responses, public API, multi-project design changes. Do not require chair->manager->implementer->reviewer for small changes; name the reviewed commit/diff; act on concrete findings without demanding extra rounds. For fix rounds or owner changes hand over only the tracker, diff, existing evidence, and open questions; do not re-run verification for the same commit, environment, and test set.
- One editor per file at a time; one building agent per working tree; contracts are settled before parallel work; only one live run at a time, only under a ResoLoop_Test* Slot, cleaned up by exact ID. Skipped live tests are reported as unverified, never as passed.
- When CLI behavior changes, README examples and skills/codex/*/SKILL.md (shipped product files) must be synced; assign them a single owner.
- Execution-mode selection (ccg strategies' "choose Agent Teams / Codex / Claude" gate): in this project the user has standing-approved the mode "Sonnet single owner (Devin optional), Codex for high-impact review". Announce it instead of asking; ask the user only if they requested a different mode or the task changes goals/requirements. All other HARD STOP gates stay as the strategy defines them.
- Keep ccg's own task lifecycle (.ccg/tasks/<task>/task.json phases, resume, completion). Never edit plan/ or ccg-managed files unless the user asks; feedback/ holds proposals and never replaces plan/. Assignees commit only on agent/* branches; merging to main (--no-ff) needs the user's approval. No push or release without a request.`;

process.stdout.write(
  JSON.stringify({
    hookSpecificOutput: { hookEventName: event, additionalContext: context },
  }),
);
