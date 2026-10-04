using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RLoop.Core;

namespace RLoop.Flux;

public sealed record FluxBindingSpec(string Target, string Mode);
public sealed record FluxResolvedBinding(string Name, string Mode, string Selector, string TargetId,
    string TargetKind, string? TargetType);
public sealed record FluxResolvedModuleBindings(IReadOnlyList<FluxResolvedBinding> Bindings)
{
    public IReadOnlyDictionary<string, string> InputMap => Bindings.Where(binding => binding.Mode == "source")
        .ToDictionary(binding => binding.Name, binding => binding.TargetId, StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> OutputMap => Bindings.Where(binding => binding.Mode == "drive")
        .ToDictionary(binding => binding.Name, binding => binding.TargetId, StringComparer.Ordinal);
}
public sealed record FluxModuleSpec(string Name, string Source, string Module, IReadOnlyList<string>? DependsOn = null,
    IReadOnlyDictionary<string, FluxBindingSpec>? Bindings = null);
public sealed record FluxModuleManifest(string? SchemaVersion, IReadOnlyList<FluxModuleSpec> Modules, string? ProjectDirectory = null,
    string? Parent = null, string? WorldState = null, string? DeployState = null);

/// <summary>
/// What the deploy guard settled for one module (ROADMAP-9): added to the existing result shapes of
/// <c>flux deploy</c>, <c>deploy-manifest</c> and <c>watch</c> as the <c>deploy</c> item.
/// </summary>
/// <param name="PreviousRootSlotId">The recorded previous module root the guard trusted and removed by exact ID, or null.</param>
/// <param name="WriterCheck">Same value as <see cref="FluxDeployGuardPreconditions.WriterCheck"/>.</param>
public sealed record FluxDeployGuardSummary(
    string StateFile,
    string OperationId,
    string ModuleName,
    string ParentSlotId,
    string NewRootSlotId,
    string? PreviousRootSlotId,
    FluxPreviousRootRemoval PreviousRootRemoval,
    FluxDeployStage Stage,
    FluxDeploySessionRecord Session,
    FluxDeployGuardPreconditions Preconditions,
    string BindingReadback,
    IReadOnlyList<FluxDeployBindingReadback> BindingEvidence,
    string WriterCheck,
    IReadOnlyList<FluxDiagnostic> Diagnostics)
{
    public IReadOnlyList<FluxWriterIdentityObservation> WriterObservations { get; init; } = [];
    public FluxDeployWriterIdentity? ExpectedWriterIdentity { get; init; }
    public static FluxDeployGuardSummary From(FluxDeployGuardResult result) => new(result.StateFile, result.OperationId,
        result.ModuleName, result.ParentSlotId, result.NewRootSlotId, result.Preconditions.PreviousRootSlotId,
        result.PreviousRootRemoval, result.Stage, result.Session, result.Preconditions, result.BindingReadback,
        result.BindingEvidence, result.Preconditions.WriterCheck, result.Diagnostics)
        { WriterObservations = result.WriterObservations, ExpectedWriterIdentity = result.ExpectedWriterIdentity };
}

/// <param name="ModuleSlotIdBefore">
/// The module root recorded in the deploy state before this run (for a settled deployment: the recorded root the guard
/// trusted and replaced). Never a Slot found by name.
/// </param>
/// <param name="ModuleSlotIdAfter">The new module root ID from the deployer's creation answer, or the unchanged root of a no-op.</param>
/// <param name="Deploy">The guard's evidence for a settled deployment; null for a no-op or a stop.</param>
/// <param name="Build">How the Flux-SDK build was judged (P9); null when no build ran (a no-op, or a stop before the build).</param>
/// <param name="BuildDiagnostics">The build's diagnostics (errors and warnings, read from stdout); null when no build ran.</param>
/// <param name="BindingTypes">The target type check of each binding; null when the module declares no binding.</param>
public sealed record FluxModuleDeployment(string Name, string Action, string Reason, bool BuildSucceeded,
    bool Deployed, string? ModuleSlotIdBefore, string? ModuleSlotIdAfter, string? Error = null,
    IReadOnlyList<FluxResolvedBinding>? Bindings = null, FluxDeployGuardSummary? Deploy = null,
    FluxBuildVerdict? Build = null, IReadOnlyList<FluxDiagnostic>? BuildDiagnostics = null,
    IReadOnlyList<FluxBindingTypeCheck>? BindingTypes = null);

public static class FluxBindingTypeStatus
{
    /// <summary>The target's type was read and is the port's type.</summary>
    public const string Matched = "matched";
    /// <summary>
    /// The target's type could not be read, or the read could not show that it is the port's type. Reported as a
    /// warning; the deployment continues and the guard's readback after it remains the final check. A target whose
    /// type was read and differs stops with FLUX_BINDING_TYPE_MISMATCH instead.
    /// </summary>
    public const string Unknown = "unknown";
}

/// <summary>The pre-deployment type check of one binding target (ROADMAP-9 unit 5). Nothing is written.</summary>
/// <param name="PortType">The port type as declared in the module source.</param>
/// <param name="TargetType">The target type as resolved from the world (null when it was not read).</param>
/// <param name="ExpectedType">The port type, normalized (assembly, namespace and scalar aliases removed).</param>
/// <param name="ActualType">
/// The target's actual type, normalized: <c>Slot</c> for a Slot, the component type for a component, and for a member
/// the <c>T</c> it offers as <c>IValue&lt;T&gt;</c> / <c>IField&lt;T&gt;</c> (a field's value type, or the target
/// type of a reference). Null when it could not be read.
/// </param>
/// <param name="Status">One of <see cref="FluxBindingTypeStatus"/>.</param>
public sealed record FluxBindingTypeCheck(string Binding, string Mode, string PortType, string TargetKind,
    string? TargetType, string ExpectedType, string? ActualType, string Status, string Detail);

/// <summary>Why a manifest run or a watch stopped: the code and message of the error it ended with.</summary>
/// <param name="Module">The manifest module being placed, or null when the run stopped before any module.</param>
public sealed record FluxManifestStop(string? Module, string Code, string Message);

/// <param name="StoppedBy">Set when the run (or the watch) ended with an error from the deploy state or the guard.</param>
public sealed record FluxManifestResult(bool Success, string Manifest, string ParentSlotId,
    IReadOnlyList<FluxModuleDeployment> Modules, bool Atomic, string Recovery, bool WatchStopped = false,
    FluxManifestStop? StoppedBy = null);

/// <summary>
/// Result of the single-module <c>flux deploy</c>. The first seven items keep the names of the former result
/// (<see cref="FluxResult"/>); <paramref name="OutputPath"/> is the new module root ID. <paramref name="Deploy"/> is added.
/// </summary>
public sealed record FluxSingleDeployResult(bool Success, int ExitCode, string StandardOutput, string StandardError,
    string OutputPath, IReadOnlyList<FluxDiagnostic> Diagnostics, IReadOnlyList<FluxDiagnostic> PrimaryDiagnostics,
    FluxDeployGuardSummary Deploy);

/// <summary>The single-module <c>flux deploy</c>: the same guard as the manifest, with its own default state (P4).</summary>
public static class FluxSingleDeploy
{
    /// <summary>
    /// Places one module below an exact parent through <see cref="FluxDeployGuard"/>. The state is
    /// <see cref="FluxDeployStateStore.ResolveSingleDeployStatePath"/>. A missing parent or Root is refused by the
    /// guard (FLUX_PARENT_ROOT_REFUSED). Returns only for a settled deployment; every other end is the guard's error.
    /// </summary>
    /// <param name="parentSlotId">Exact parent Slot ID, or null/empty when none was given.</param>
    public static async Task<FluxSingleDeployResult> DeployAsync(IResoniteClient client, IFluxDeployer deployer,
        string projectDirectory, string module, string? parentSlotId, Uri url, string? libraryPath, string? helperPath,
        string? sdkVersion = null, CancellationToken cancellationToken = default)
    {
        var project = Path.GetFullPath(projectDirectory);
        var statePath = FluxDeployStateStore.ResolveSingleDeployStatePath(project, module);
        // The single deploy has no no-op, so no input hash is computed; the record keeps an empty one.
        var result = await new FluxDeployGuard(client, deployer).DeployModuleAsync(new FluxDeployGuardRequest(
            project, module, FluxDeployStateStore.SingleDeployModuleKey(module), parentSlotId ?? string.Empty, url,
            libraryPath, helperPath, null, null, string.Empty, null, statePath, sdkVersion,
            FluxDeployExecuteRequest.DefaultDeadline), cancellationToken);
        return new FluxSingleDeployResult(true, 0, result.StandardOutput, result.StandardError, result.NewRootSlotId,
            result.Diagnostics, result.Diagnostics.Where(diagnostic => diagnostic.IsPrimary).ToArray(),
            FluxDeployGuardSummary.From(result));
    }
}

public sealed record FluxManifestModuleValidation(string Name, string Source, string Module,
    int Ports, int Bindings, IReadOnlyList<string> DependsOn);
public sealed record FluxManifestValidationResult(bool Valid, string Manifest, string ProjectDirectory,
    string? WorldState, string DeployState, IReadOnlyList<FluxManifestModuleValidation> Modules);

/// <summary>
/// Builds the modules of a manifest and places each through <see cref="FluxDeployGuard"/> (ROADMAP-9). The guard owns
/// the session lock, the Flux deploy state (v2), the observations and the pending record; this class only decides the
/// order, the no-op and how a run is reported. It never looks a module up by name and never writes the state itself.
/// </summary>
public sealed class FluxManifestOrchestrator(IFluxTool flux, IFluxDeployer deployer, IResoniteClient client)
{
    /// <summary>The context key under which a manifest error carries the run's <see cref="FluxManifestResult"/>.</summary>
    public const string ReportContextKey = "report";

    /// <summary>
    /// Deploys the manifest's modules in dependency order. A build failure is reported in the result (nothing was
    /// sent). An error from the deploy state or the guard ends the run as an <see cref="RLoopException"/> with the
    /// guard's code and context plus <see cref="ReportContextKey"/>.
    /// </summary>
    /// <param name="parentSlotId">Exact parent Slot ID. Null/empty (no parent given) or Root is refused by the guard (P8).</param>
    public async Task<FluxManifestResult> DeployAsync(string manifestPath, string? parentSlotId, Uri url,
        string? libraryPath, string? helperPath,
        IReadOnlyDictionary<string, FluxResolvedModuleBindings>? resolvedBindings = null,
        string? sdkVersion = null, CancellationToken cancellationToken = default)
    {
        var loaded = Load(manifestPath);
        var parent = parentSlotId ?? string.Empty;
        var statePath = ResolveStatePath(loaded.Manifest, loaded.Path);
        var results = new List<FluxModuleDeployment>();
        FluxManifestResult Report(bool success, FluxManifestStop? stop = null) => new(success, loaded.Path, parent, results.ToArray(), false,
            $"Module replacement is non-atomic. Settled modules are recorded in {statePath}; an unsettled module stays pending there until it is inspected and discarded. Fix the error and re-run to converge.",
            StoppedBy: stop);

        // A v1 state is read as an unconfirmed migration (the guard confirms or drops each record, P6). An unknown
        // version or kind stops here, before anything is built.
        FluxDeployState state;
        try
        {
            state = FluxDeployStateStore.Load(statePath);
            if (state.Pending.Count > 0) throw FluxDeployStateStore.PendingExists(statePath, state);
        }
        catch (RLoopException error) { throw WithReport(error, Report(false, new(null, error.Code, error.Message))); }

        var hashes = ComputeHashes(loaded.Manifest, loaded.Directory);
        foreach (var module in Topological(loaded.Manifest.Modules))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.GetFullPath(module.Source, loaded.Directory);
            state.Modules.TryGetValue(module.Name, out var previous);
            FluxResolvedModuleBindings? moduleBindings = null;
            _ = resolvedBindings?.TryGetValue(module.Name, out moduleBindings);
            var preflight = await ValidateResolvedBindingsAsync(module, moduleBindings, source, cancellationToken);
            var bindingTypes = preflight.TypeChecks;
            var hash = EffectiveHash(hashes[module.Name], moduleBindings);
            var action = previous is null ? "create" : "update";
            var recordedRoot = previous?.RootSlotId;
            if (await IsUnchangedAsync(state, previous, hash, parent, cancellationToken))
            {
                results.Add(new FluxModuleDeployment(module.Name, "no-op",
                    "source and transitive dependency hashes match the deploy state, and the recorded root is still the parent's child with its declared name",
                    true, false, recordedRoot, recordedRoot, Bindings: moduleBindings?.Bindings, BindingTypes: bindingTypes));
                continue;
            }
            // P9: the build is an early check judged by its error diagnostics (warnings are reported and the run
            // continues). What is placed is decided by the deployer's own compile inside the guard.
            var build = await flux.BuildAsync(new FluxBuildRequest(source, loaded.ProjectDirectory, null, libraryPath), cancellationToken);
            var verdict = FluxBuildJudgement.Of(build);
            var buildDiagnostics = build.Diagnostics ?? FluxDiagnostics.Parse(build.StandardOutput, build.StandardError);
            if (!verdict.Success)
            {
                results.Add(new FluxModuleDeployment(module.Name, action, "build failed; deploy was skipped",
                    false, false, recordedRoot, recordedRoot, FluxBuildJudgement.DescribeFailure(verdict, build), moduleBindings?.Bindings,
                    Build: verdict, BuildDiagnostics: buildDiagnostics, BindingTypes: bindingTypes));
                return Report(false);
            }
            if (Regex.IsMatch(build.StandardOutput + "\n" + build.StandardError,
                    @"Packing\s+0\s+ProtoFlux\s+nodes", RegexOptions.IgnoreCase))
                throw new RLoopException("FLUX_EMPTY_MODULE",
                    $"Module '{module.Name}' compiled successfully but contains zero ProtoFlux nodes.", ExitCodes.ValidationFailed,
                    new Dictionary<string, object?> { ["module"] = module.Name, ["source"] = source },
                    ["Keep a reachable entrypoint such as CallInput, a Dynamic Impulse receiver, LocalUpdate, or another consumer of the graph."]);

            // The guard compiles in the deployer (errors send nothing), takes the URL lock, compares the parent's
            // children with the record, records the pending deployment, calls the deployer and reads the result back.
            FluxDeployGuardResult deployed;
            try
            {
                // The manifest requires every port to be bound one-to-one (validate-manifest), so the guard checks the
                // compiled ports with RequireAllPortsBound.
                deployed = await new FluxDeployGuard(client, deployer).DeployModuleAsync(new FluxDeployGuardRequest(
                    loaded.ProjectDirectory, module.Module, module.Name, parent, url, libraryPath, helperPath,
                    moduleBindings?.InputMap, moduleBindings?.OutputMap, hash, null, statePath, sdkVersion,
                    FluxDeployExecuteRequest.DefaultDeadline, RequireAllPortsBound: true)
                    { DeclaredPorts = preflight.Ports }, cancellationToken);
            }
            catch (RLoopException error)
            {
                results.Add(new FluxModuleDeployment(module.Name, action, $"the deploy guard stopped ({error.Code})",
                    error.Code != "FLUX_COMPILE_FAILED", false, recordedRoot, null, error.Message, moduleBindings?.Bindings,
                    Build: verdict, BuildDiagnostics: buildDiagnostics, BindingTypes: bindingTypes));
                throw WithReport(error, Report(false, new(module.Name, error.Code, error.Message)));
            }
            results.Add(new FluxModuleDeployment(module.Name, action,
                previous is null ? "module has no deploy state" : "source or transitive dependency changed, or the record had to be confirmed",
                true, true, deployed.Preconditions.PreviousRootSlotId, deployed.NewRootSlotId,
                Bindings: moduleBindings?.Bindings, Deploy: FluxDeployGuardSummary.From(deployed),
                Build: verdict, BuildDiagnostics: buildDiagnostics, BindingTypes: bindingTypes));
        }
        return Report(true);
    }

    /// <summary>
    /// A module is unchanged only when its record is a settled (<c>deployed</c>) one with the same input hash and the
    /// same parent, was settled against this URL with a proven matched non-null <c>S-</c> ID, and its root, read by its
    /// exact ID, is still a direct child of that parent with the declared name. Nothing is looked up by name and the
    /// state is not changed; anything else goes to the guard. A <c>migrated-v1</c> record always goes to the guard,
    /// which decides whether to trust it (P6).
    /// </summary>
    private async Task<bool> IsUnchangedAsync(FluxDeployState state, FluxDeployModuleRecord? record, string hash,
        string parentSlotId, CancellationToken cancellationToken)
    {
        if (record is not { Origin: FluxDeployOrigins.Deployed, RootSlotId: { } rootId, ModuleName: { } moduleName } ||
            !string.Equals(record.InputHash, hash, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(parentSlotId) ||
            !string.Equals(record.ParentSlotId, parentSlotId, StringComparison.Ordinal) || state.Session is not { } recorded)
            return false;
        var session = await client.GetSessionInfoAsync(cancellationToken);
        if (!session.Connected) return false;
        var observed = (client as IApplySessionObservation)?.ObserveApplySession() ?? ApplySessionObservation.Observe(session.Url);
        // Keep this local to Flux: Core's mutation helpers remain internal. Unknown identity allows a first
        // deployment, but does not prove ownership of any session-scoped root for a no-op.
        if (recorded.IdentityStatus != "matched" || observed.IdentityStatus != "matched" ||
            recorded.DiscoverSessionId is not { } recordedId || observed.DiscoverSessionId is not { } currentId ||
            !recordedId.StartsWith("S-", StringComparison.Ordinal) ||
            !string.Equals(recorded.NormalizedUrl, observed.NormalizedUrl, StringComparison.Ordinal) ||
            !string.Equals(recordedId, currentId, StringComparison.Ordinal)) return false;
        SlotInfo root;
        try { root = await client.GetSlotAsync(rootId, 0, false, cancellationToken); }
        catch (RLoopException error) when (error.Code == "SLOT_NOT_FOUND") { return false; }
        return root is { IsReferenceOnly: false } && string.Equals(root.Id, rootId, StringComparison.Ordinal) &&
            string.Equals(root.ParentId, parentSlotId, StringComparison.Ordinal) &&
            string.Equals(root.Name, moduleName, StringComparison.Ordinal);
    }

    /// <summary>The same error with the run's report added to its context (code, message, exit code and suggestions are kept).</summary>
    private static RLoopException WithReport(RLoopException error, FluxManifestResult report)
    {
        var context = new Dictionary<string, object?>(error.Context) { [ReportContextKey] = report };
        return new RLoopException(error.Code, error.Message, error.ExitCode, context, error.Suggestions, error);
    }

    /// <summary>
    /// Re-runs <see cref="DeployAsync"/> whenever the manifest or a module source changes. Each deployment goes through
    /// the guard, which takes the URL lock for that one deployment and releases it after the readback and the state
    /// update (P5); the lock is not held while polling or building. A build failure is reported and the watch waits
    /// for the next change. Any error from the deploy state or the guard (a pending deployment, APPLY_SESSION_BUSY,
    /// a refusal) stops the watch: there is no retry (P11). The error keeps its code and carries the report with
    /// <see cref="FluxManifestResult.WatchStopped"/> and <see cref="FluxManifestResult.StoppedBy"/>.
    /// </summary>
    public async Task<FluxManifestResult> WatchAsync(string manifestPath, string? parentSlotId, Uri url,
        string? libraryPath, string? helperPath, TimeSpan pollInterval,
        IReadOnlyDictionary<string, FluxResolvedModuleBindings>? resolvedBindings = null,
        string? sdkVersion = null, CancellationToken cancellationToken = default)
    {
        FluxManifestResult? last = null;
        string? fingerprint = null;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var loaded = Load(manifestPath);
                var current = Fingerprint(loaded);
                if (current != fingerprint)
                {
                    try
                    {
                        last = await DeployAsync(manifestPath, parentSlotId, url, libraryPath, helperPath, resolvedBindings,
                            sdkVersion, cancellationToken);
                    }
                    catch (RLoopException error) when (error.Context.TryGetValue(ReportContextKey, out var value) &&
                                                       value is FluxManifestResult stopped)
                    {
                        throw WithReport(error, stopped with { WatchStopped = true });
                    }
                    fingerprint = current;
                }
                await Task.Delay(pollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && last is not null)
        {
            return last with { WatchStopped = true };
        }
    }

    /// <summary>
    /// The Flux deploy state of a manifest: <c>deployState</c> relative to the manifest, or
    /// <c>&lt;manifest directory&gt;/.resoloop/flux-state/&lt;manifest name&gt;.json</c>. Reads only the manifest JSON,
    /// so it works when module sources are missing (for <c>--discard-pending</c>).
    /// </summary>
    public static string ResolveDeployStatePath(string manifestPath)
    {
        var path = Path.GetFullPath(manifestPath);
        return ResolveStatePath(ReadManifest(path), path);
    }

    public static FluxModuleManifest Inspect(string manifestPath) => Load(manifestPath).Manifest;

    public static FluxManifestValidationResult ValidateManifest(string manifestPath)
    {
        var loaded = Load(manifestPath);
        var modules = new List<FluxManifestModuleValidation>();
        foreach (var module in Topological(loaded.Manifest.Modules))
        {
            var source = Path.GetFullPath(module.Source, loaded.Directory);
            var ports = FluxModuleSignature.Parse(File.ReadAllText(source), source);
            ValidateDeclaredBindings(module, ports);
            modules.Add(new FluxManifestModuleValidation(module.Name, source, module.Module,
                ports.Count, module.Bindings?.Count ?? 0, module.DependsOn ?? []));
        }
        return new FluxManifestValidationResult(true, loaded.Path, loaded.ProjectDirectory,
            ResolveWorldStatePath(loaded.Path, null, loaded.Manifest.WorldState),
            ResolveStatePath(loaded.Manifest, loaded.Path), modules);
    }

    public static string? ResolveWorldStatePath(string manifestPath, string? commandLineState,
        string? manifestState, string? currentDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(commandLineState))
            return Path.GetFullPath(commandLineState, currentDirectory ?? Environment.CurrentDirectory);
        if (!string.IsNullOrWhiteSpace(manifestState))
            return Path.GetFullPath(manifestState, Path.GetDirectoryName(Path.GetFullPath(manifestPath))!);
        return null;
    }

    private sealed record BindingPreflight(IReadOnlyList<FluxDeclaredPortExpectation> Ports, IReadOnlyList<FluxBindingTypeCheck>? TypeChecks);

    private async Task<BindingPreflight> ValidateResolvedBindingsAsync(FluxModuleSpec module,
        FluxResolvedModuleBindings? resolved, string source, CancellationToken cancellationToken)
    {
        var declared = module.Bindings ?? new Dictionary<string, FluxBindingSpec>();
        var ports = FluxModuleSignature.Parse(File.ReadAllText(source), source);
        if (declared.Count > 0 && resolved is null)
            throw new RLoopException("FLUX_BINDINGS_UNRESOLVED",
                $"Module '{module.Name}' declares bindings, but no resolved world targets were supplied.",
                ExitCodes.ValidationFailed);
        ValidateDeclaredBindings(module, ports);
        var expectations = ports.Select(port => new FluxDeclaredPortExpectation(port.Name, port.Direction, port.Type, port.Modifier)).ToArray();
        if (declared.Count == 0) return new(expectations, null);

        var byName = resolved!.Bindings.ToDictionary(binding => binding.Name, StringComparer.Ordinal);
        var missing = declared.Keys.Where(name => !byName.ContainsKey(name)).ToArray();
        var extra = byName.Keys.Where(name => !declared.ContainsKey(name)).ToArray();
        var mismatched = declared.Where(pair => byName.TryGetValue(pair.Key, out var binding) &&
                (!string.Equals(pair.Value.Mode, binding.Mode, StringComparison.Ordinal) ||
                 !string.Equals(pair.Value.Target, binding.Selector, StringComparison.Ordinal)))
            .Select(pair => pair.Key).ToArray();
        if (missing.Length != 0 || extra.Length != 0 || mismatched.Length != 0)
            throw new RLoopException("FLUX_BINDINGS_UNRESOLVED",
                $"Resolved bindings for module '{module.Name}' do not match its manifest declaration.",
                ExitCodes.ValidationFailed,
                new Dictionary<string, object?>
                {
                    ["module"] = module.Name,
                    ["missing"] = missing,
                    ["extra"] = extra,
                    ["mismatched"] = mismatched
                });

        var portsByName = ports.ToDictionary(port => port.Name, StringComparer.Ordinal);

        var checks = new List<FluxBindingTypeCheck>();
        foreach (var binding in resolved.Bindings)
        {
            var port = portsByName[binding.Name];
            var check = await CheckTargetTypeAsync(binding, port, cancellationToken);
            if (check is null)
            {
                var actual = ActualTargetType(binding);
                throw new RLoopException("FLUX_BINDING_TYPE_MISMATCH",
                    $"Binding '{module.Name}.{binding.Name}' expects '{port.Type}', but '{binding.Selector}' resolves to '{binding.TargetType ?? binding.TargetKind}'. Nothing was built or deployed.",
                    ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["module"] = module.Name,
                        ["binding"] = binding.Name, ["portType"] = port.Type, ["portModifier"] = port.Modifier,
                        ["targetKind"] = binding.TargetKind, ["targetType"] = binding.TargetType,
                        ["expectedType"] = FluxTypeNames.Normalize(port.Type), ["actualType"] = actual });
            }
            checks.Add(check);
        }
        return new(expectations, checks);
    }

    /// <summary>
    /// Compares a binding target's actual type with the port type (ROADMAP-9 unit 5). A Slot target offers
    /// <c>Slot</c>; a component target its component type (or, through the type information, a base type or an
    /// interface it lists); a member target the <c>T</c> it offers as <c>IValue&lt;T&gt;</c> / <c>IField&lt;T&gt;</c>,
    /// which is the value type of the field (or the target type of a reference) read from the live component when the
    /// selector was resolved. Returns null for "read and different" (the caller stops with FLUX_BINDING_TYPE_MISMATCH);
    /// a target whose type could not be read, or not shown to fit, is <see cref="FluxBindingTypeStatus.Unknown"/> and
    /// continues: this check runs before anything is written, and the guard's readback after the deployment stays the
    /// final check.
    /// </summary>
    private async Task<FluxBindingTypeCheck?> CheckTargetTypeAsync(FluxResolvedBinding binding, FluxModulePort port,
        CancellationToken cancellationToken)
    {
        var expected = FluxTypeNames.Normalize(port.Type);
        var actual = ActualTargetType(binding);
        FluxBindingTypeCheck Result(string status, string detail) => new(binding.Name, binding.Mode, port.Type,
            binding.TargetKind, binding.TargetType, expected, actual, status, detail);

        if (actual is null)
            return Result(FluxBindingTypeStatus.Unknown,
                $"the type of '{binding.Selector}' ({binding.TargetKind}) was not read, so it is not known to be '{port.Type}'");
        if (FluxTypeNames.SameType(expected, actual))
            return Result(FluxBindingTypeStatus.Matched, $"'{binding.Selector}' is '{actual}'");
        if (binding.TargetKind != "component" || string.IsNullOrWhiteSpace(binding.TargetType))
            return null; // A Slot is a Slot; a member's value type was read and is another type.

        // A component may still fit through a base type or an interface: read its type information.
        var interfaceExpected = FluxTypeNames.IsInterfaceName(expected);
        string? type = binding.TargetType;
        for (var depth = 0; type is not null && depth < 32; depth++)
        {
            RLoop.Core.TypeInfo info;
            try { info = await client.DescribeTypeAsync(type, cancellationToken); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return Result(FluxBindingTypeStatus.Unknown,
                    $"'{binding.Selector}' is '{actual}'; whether it is a '{port.Type}' is unknown because the type information of '{type}' could not be read ({error.Message})");
            }
            if (info.Interfaces.Any(name => FluxTypeNames.SameType(expected, FluxTypeNames.Normalize(name))))
                return Result(FluxBindingTypeStatus.Matched, $"'{binding.Selector}' is '{actual}', which implements '{expected}' (type information of '{type}')");
            if (depth > 0 && FluxTypeNames.SameType(expected, FluxTypeNames.Normalize(info.FullTypeName)))
                return Result(FluxBindingTypeStatus.Matched, $"'{binding.Selector}' is '{actual}', which derives from '{expected}'");
            type = string.IsNullOrWhiteSpace(info.BaseType) ? null : info.BaseType;
        }
        // The base chain was read to its end without the expected class. The listed interfaces may omit inherited
        // ones, so an interface that was not found is not proven absent.
        return interfaceExpected || type is not null
            ? Result(FluxBindingTypeStatus.Unknown,
                $"'{binding.Selector}' is '{actual}'; the type information read does not show that it is a '{port.Type}'")
            : null;
    }

    private static string? ActualTargetType(FluxResolvedBinding binding) =>
        binding.TargetKind == "slot" ? "Slot"
        : string.IsNullOrWhiteSpace(binding.TargetType) ? null
        : FluxTypeNames.Normalize(binding.TargetType);

    private static void ValidateDeclaredBindings(FluxModuleSpec module, IReadOnlyList<FluxModulePort> ports)
    {
        var declared = module.Bindings ?? new Dictionary<string, FluxBindingSpec>();
        if (declared.Count == 0)
        {
            if (ports.Count > 0)
                throw new RLoopException("FLUX_MODULE_PORT_UNBOUND",
                    $"Module '{module.Name}' declares input/output ports but its manifest has no bindings.", ExitCodes.ValidationFailed,
                    new Dictionary<string, object?> { ["module"] = module.Name, ["ports"] = ports.Select(port => port.Name).ToArray() });
            return;
        }

        var portsByName = ports.ToDictionary(port => port.Name, StringComparer.Ordinal);
        var undeclaredPorts = ports.Where(port => !declared.ContainsKey(port.Name)).Select(port => port.Name).ToArray();
        var unknownBindings = declared.Keys.Where(name => !portsByName.ContainsKey(name)).ToArray();
        if (undeclaredPorts.Length > 0 || unknownBindings.Length > 0)
            throw new RLoopException("FLUX_MODULE_PORT_UNBOUND",
                $"Module '{module.Name}' ports and manifest bindings do not form a complete one-to-one map.", ExitCodes.ValidationFailed,
                new Dictionary<string, object?> { ["module"] = module.Name, ["unboundPorts"] = undeclaredPorts,
                    ["unknownBindings"] = unknownBindings });

        foreach (var binding in declared)
        {
            var port = portsByName[binding.Key];
            if (port.Direction != binding.Value.Mode)
                throw new RLoopException("FLUX_BINDING_DIRECTION_MISMATCH",
                    $"Binding '{module.Name}.{binding.Key}' is mode '{binding.Value.Mode}', but the compiled module port is '{port.Direction}'.",
                    ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["portType"] = port.Type, ["modifier"] = port.Modifier });
            if (port.Modifier == "global" && IsInterfaceName(port.Type))
                throw new RLoopException("FLUX_INTERFACE_GLOBAL_UNSUPPORTED",
                    $"Flux-SDK 1.9.x cannot safely deploy interface global input '{module.Name}.{binding.Key}' ({port.Type}).",
                    ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["module"] = module.Name,
                        ["binding"] = binding.Key, ["portType"] = port.Type },
                    ["Use an element input with a concrete Component type, then convert it to a global inside the module with asDrivenGlobal.",
                     "For event-only coupling, use a Dynamic Impulse bridge instead of an interface global."]);
        }
    }

    private static bool IsInterfaceName(string type) => FluxTypeNames.IsInterfaceName(FluxTypeNames.Normalize(type));

    private static FluxModuleManifest ReadManifest(string path)
    {
        if (!File.Exists(path)) throw new RLoopException("FLUX_MANIFEST_NOT_FOUND", $"Flux manifest '{path}' does not exist.", ExitCodes.NotFound);
        try { return JsonSerializer.Deserialize<FluxModuleManifest>(File.ReadAllText(path), JsonOptions) ?? throw new JsonException("Manifest was empty."); }
        catch (JsonException ex)
        {
            var suggestions = ex.Path?.Contains(".bindings", StringComparison.OrdinalIgnoreCase) == true
                ? new[] { "Declare bindings as a JSON object keyed by module port name, for example: \"bindings\": { \"Score\": { \"mode\": \"drive\", \"target\": \"$member:score.Value\" } }." }
                : null;
            throw new RLoopException("FLUX_MANIFEST_INVALID", $"Invalid Flux manifest: {ex.Message}", ExitCodes.ValidationFailed,
                new Dictionary<string, object?> { ["manifest"] = path, ["jsonPath"] = ex.Path }, suggestions, ex);
        }
    }

    private static LoadedManifest Load(string manifestPath)
    {
        var path = Path.GetFullPath(manifestPath);
        var manifest = ReadManifest(path);
        if (manifest.SchemaVersion != "1") throw new RLoopException("FLUX_MANIFEST_VERSION_UNSUPPORTED", "Flux manifest schemaVersion must be \"1\".", ExitCodes.ValidationFailed);
        if (manifest.Modules.Count == 0) throw new RLoopException("FLUX_MANIFEST_EMPTY", "Flux manifest requires at least one module.", ExitCodes.ValidationFailed);
        if (manifest.Modules.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != manifest.Modules.Count)
            throw new RLoopException("FLUX_MODULE_DUPLICATE", "Flux module names must be unique.", ExitCodes.ValidationFailed);
        var directory = Path.GetDirectoryName(path)!;
        var project = Path.GetFullPath(manifest.ProjectDirectory ?? directory, directory);
        foreach (var module in manifest.Modules)
        {
            if (!File.Exists(Path.GetFullPath(module.Source, directory))) throw new RLoopException("FLUX_SOURCE_NOT_FOUND", $"Module '{module.Name}' source '{module.Source}' does not exist.", ExitCodes.NotFound);
            foreach (var dependency in module.DependsOn ?? [])
                if (!manifest.Modules.Any(x => x.Name == dependency)) throw new RLoopException("FLUX_DEPENDENCY_NOT_FOUND", $"Module '{module.Name}' depends on unknown module '{dependency}'.", ExitCodes.ValidationFailed);
            foreach (var binding in module.Bindings ?? new Dictionary<string, FluxBindingSpec>())
            {
                if (string.IsNullOrWhiteSpace(binding.Key))
                    throw new RLoopException("FLUX_BINDING_NAME_MISSING", $"Module '{module.Name}' contains an empty binding name.", ExitCodes.ValidationFailed);
                if (binding.Value.Mode is not ("source" or "drive"))
                    throw new RLoopException("FLUX_BINDING_MODE_INVALID",
                        $"Binding '{module.Name}.{binding.Key}' mode must be 'source' or 'drive'.", ExitCodes.ValidationFailed);
                if (string.IsNullOrWhiteSpace(binding.Value.Target) ||
                    !(binding.Value.Target.StartsWith("$slot:", StringComparison.Ordinal) ||
                      binding.Value.Target.StartsWith("$component:", StringComparison.Ordinal) ||
                      binding.Value.Target.StartsWith("$member:", StringComparison.Ordinal)))
                    throw new RLoopException("FLUX_BINDING_TARGET_INVALID",
                        $"Binding '{module.Name}.{binding.Key}' requires a stable $slot:, $component:, or $member: target.", ExitCodes.ValidationFailed);
                if (binding.Value.Mode == "drive" && !binding.Value.Target.StartsWith("$member:", StringComparison.Ordinal))
                    throw new RLoopException("FLUX_BINDING_DRIVE_REQUIRES_MEMBER",
                        $"Drive binding '{module.Name}.{binding.Key}' must target $member:key.MemberName.", ExitCodes.ValidationFailed);
            }
        }
        _ = Topological(manifest.Modules);
        return new LoadedManifest(path, directory, project, manifest);
    }

    private static IReadOnlyList<FluxModuleSpec> Topological(IReadOnlyList<FluxModuleSpec> modules)
    {
        var byName = modules.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var visited = new Dictionary<string, int>(StringComparer.Ordinal);
        var output = new List<FluxModuleSpec>();
        void Visit(string name, Stack<string> stack)
        {
            if (visited.GetValueOrDefault(name) == 2) return;
            if (visited.GetValueOrDefault(name) == 1)
                throw new RLoopException("FLUX_DEPENDENCY_CYCLE", $"Flux module dependency cycle: {string.Join(" -> ", stack.Reverse().Append(name))}", ExitCodes.ValidationFailed);
            visited[name] = 1; stack.Push(name);
            foreach (var dependency in byName[name].DependsOn ?? []) Visit(dependency, stack);
            stack.Pop(); visited[name] = 2; output.Add(byName[name]);
        }
        foreach (var module in modules) Visit(module.Name, new Stack<string>());
        return output;
    }

    private static Dictionary<string, string> ComputeHashes(FluxModuleManifest manifest, string directory)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var module in Topological(manifest.Modules))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(File.ReadAllBytes(Path.GetFullPath(module.Source, directory)));
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(module.Bindings ?? new Dictionary<string, FluxBindingSpec>(), JsonOptions));
            foreach (var dependency in module.DependsOn ?? []) hash.AppendData(Encoding.UTF8.GetBytes(result[dependency]));
            result[module.Name] = Convert.ToHexString(hash.GetHashAndReset());
        }
        return result;
    }

    private static string EffectiveHash(string sourceHash, FluxResolvedModuleBindings? bindings)
    {
        if (bindings is null || bindings.Bindings.Count == 0) return sourceHash;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(sourceHash));
        foreach (var binding in bindings.Bindings.OrderBy(binding => binding.Name, StringComparer.Ordinal))
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(binding, JsonOptions));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Fingerprint(LoadedManifest loaded)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(File.ReadAllBytes(loaded.Path));
        foreach (var module in loaded.Manifest.Modules) hash.AppendData(File.ReadAllBytes(Path.GetFullPath(module.Source, loaded.Directory)));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string ResolveStatePath(FluxModuleManifest manifest, string manifestPath) => Path.GetFullPath(
        manifest.DeployState ?? Path.Combine(".resoloop", "flux-state", Path.GetFileNameWithoutExtension(manifestPath) + ".json"),
        Path.GetDirectoryName(manifestPath)!);

    private sealed record LoadedManifest(string Path, string Directory, string ProjectDirectory, FluxModuleManifest Manifest);
    // Also the serializer of the binding part of the input hash; changing it would change every hash.
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
}

/// <summary>
/// Normalizes the type names that meet in a binding check: ProtoGraph port types (<c>int</c>, <c>float3</c>,
/// <c>Slot</c>), ResoniteLink type strings (<c>[FrooxEngine]FrooxEngine.IField&lt;[FrooxEngine]Elements.Core.float3&gt;</c>)
/// and CLR full names (<c>System.Single</c>, <c>System.Nullable`1[[System.Single, …]]</c>). Assembly prefixes and
/// namespaces are removed, generic arguments are normalized one by one, <c>T?</c> is <c>Nullable&lt;T&gt;</c>, and the
/// scalar aliases are made equal (<c>int</c> = <c>Int32</c>). Names only: it never decides assignability.
/// </summary>
public static class FluxTypeNames
{
    public static string Normalize(string type) => FluxDeployTypeNames.Normalize(type);
    public static bool SameType(string normalizedExpected, string normalizedActual) => FluxDeployTypeNames.SameType(normalizedExpected, normalizedActual);
    public static bool IsInterfaceName(string normalized) => FluxDeployTypeNames.IsInterfaceName(normalized);
}
