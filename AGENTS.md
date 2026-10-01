# Development guide

## Agent workflow

For `/ccg:go` work, read [the agent workflow](docs/dev/agent-workflow.md). It is the tracked source for roles, optional Devin delegation, worktree ownership, and verification tiers. `CLAUDE.md` is the Claude entry point and may be absent in a worktree.

Preserve existing user changes and untracked files. Do not edit `.ccg/` managed files or `plan/` unless the user specifically asks; `feedback/` holds proposals and never replaces the plan. Do not commit, push, or release without a request.

A live run must be explicitly authorized for the task. Report pass, fail, skip, target, and evidence status; a skipped live test is unverified, not passed. When reviewing, identify the exact commit or diff examined and distinguish any changes made afterward.

## Architecture

Dependency direction is RLoop.Cli → RLoop.Core ← RLoop.ResoniteLink and RLoop.Cli → RLoop.Flux → IFluxDeployer ← RLoop.Flux.Deployer. Core must not reference ResoniteLink models or Flux-SDK types. Keep ResoniteLink Beta changes in the adapter and Flux-SDK API changes in the F# deployer.

## Commands

~~~powershell
dotnet build ResoLoop.slnx
dotnet test ResoLoop.slnx --no-build
dotnet run --project src/RLoop.Cli -- help
~~~

Live tests are opt-in:

~~~powershell
$env:RESOLOOP_RUN_INTEGRATION="1"
$env:RESONITE_LINK_URL="ws://localhost:<current-port>"
dotnet test tests/RLoop.IntegrationTests/RLoop.IntegrationTests.csproj --filter Category=Integration
~~~

## Resonite safety

- Never run destructive operations against Root or an unverified ID/path.
- Put experiments under an unmistakable ResoLoop_Test* Slot and clean only that exact Slot in finally.
- Do not change existing user content unless the task explicitly names it.
- CLI delete/remove must retain explicit --yes; do not add an implicit confirmation bypass.
- IDs are session-scoped. Re-observe after session restart.

## Upstream discipline

- Do not guess ResoniteLink messages, field wrappers, Component type names, or member names. Check the pinned upstream source/docs and verify through runtime Reflection.
- Keep official YellowDogMan.ResoniteLink behind IResoniteClient; never leak its raw JSON/models into Core.
- Do not implement a ProtoFlux compiler. Reuse Flux-SDK CLI or Papaltine.FluxSDK.Core.
- Public source and documented APIs may inform implementation. Local decompilation is for API behavior research only: never copy private Resonite implementation or extracted source into this repository.

## Tests and Skills

Maintain offline tests for argument parsing, configuration precedence, model mapping, serialization, value conversion, and error codes. Any live test must be opt-in and sandboxed.

When CLI behavior changes, update README examples and affected skills/codex/*/SKILL.md. Skills are workflows, not command mirrors: preserve Reflection-first behavior, bounded observation, exact-target destructive safety, and post-change inspection.

Apply shape declarations are the source for generated TypeScript types, scalar lists, copy functions, and C# property candidates. When adding a copied field, update the record and any exceptional shape attributes, an independent handwritten output fixture, README guidance, and affected workflow skills; regenerate once with `dotnet run --project tools/RLoop.ContractGen -- tools/resoloop-jsx/src/generated`. Do not edit generated files or generate expected fixtures. Behavior-changing fields also require C# execution changes and behavior tests. Ordinary npm tests/builds must continue to work without dotnet.
