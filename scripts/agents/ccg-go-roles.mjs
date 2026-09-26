// Project hook: injects the agent role structure (docs/dev/agent-workflow.md) whenever /ccg:go runs.
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
- Opus (you, main session) = producer/chair: goals, priorities, acceptance criteria, cross-feature contracts, manager charters, conflict resolution, whether and against what live tests run. Do NOT do file-level implementation or duplicate the managers' detailed investigation/decomposition. Delegate your own lookups to the scout subagent. Ask the user only about goals, requirements, or tolerances.
- Implementation phases: split the work into feature/area managers and spawn each with Agent(subagent_type: "feature-manager") (fallback: general-purpose + model "sonnet", told to follow .claude/agents/feature-manager.md). Charter = goal, editable files, contracts (types, IResoniteClient, CLI args, error codes), acceptance criteria, SWE-2 budget, whether it may build in the main tree, tracker path .ccg/tasks/<task>/assignments.md, files owned by others. Spawn independent managers in one message.
- Managers decompose, write self-contained Devin prompt files, run Devin SWE-2 Max (devin --model swe-2-max --permission-mode accept-edits -p --prompt-file ...) in parallel using worktree slots (scripts/agents/new-worktree.ps1), accept and integrate, then have SWE-2 run scoped verification (dotnet build ResoLoop.slnx / dotnet test ResoLoop.slnx --no-build). Manager-to-manager coordination is relayed by you. Managers return DONE / ESCALATE / BLOCKED.
- Codex = large cross-feature reviews and design/handoff docs (smart-friend skill or codeagent-wrapper --backend codex). Not a mandatory gate for small changes. Always name the reviewed commit or diff.
- Keep manager context small: one charter per manager. Give review-fix rounds or follow-ups to a FRESH manager, handing state over via tracker/review files; never keep extending one manager with SendMessage. Managers must not implement beyond tiny glue fixes (<10 lines).
- Log-heavy work (builds, offline/live tests, log digging, long shell output) is run by Devin SWE-2, never by Opus or managers. Devin returns a 10-line summary (counts, failed tests with one-line reasons, skip counts, duration). Claude models read only that summary and git diff --stat.
- One editor per file at a time; one building SWE-2 per working tree; contracts are settled before parallel work; only one live run at a time, only under a ResoLoop_Test* Slot, cleaned up by exact ID. Skipped live tests are reported as unverified, never as passed.
- When CLI behavior changes, README examples and skills/codex/*/SKILL.md (shipped product files) must be synced; assign them a single owner.
- Execution-mode selection (ccg strategies' "choose Agent Teams / Codex / Claude" gate): in this project the user has standing-approved the mode "feature-manager (Sonnet) + Devin SWE-2 Max implementers, Codex for large reviews/docs". Announce it instead of asking; ask the user only if they requested a different mode or the task changes goals/requirements. All other HARD STOP gates stay as the strategy defines them.
- Keep ccg's own task lifecycle (.ccg/tasks/<task>/task.json phases, resume, completion). Never edit plan/ or ccg-managed files unless the user asks; feedback/ holds proposals and never replaces plan/. No push or release without a request.`;

process.stdout.write(
  JSON.stringify({
    hookSpecificOutput: { hookEventName: event, additionalContext: context },
  }),
);
