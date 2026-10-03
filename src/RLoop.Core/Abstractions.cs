namespace RLoop.Core;

public interface IResoniteClient : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task<SessionInfo> GetSessionInfoAsync(CancellationToken cancellationToken = default);
    Task<SlotInfo> GetSlotAsync(string id, int depth, bool includeComponentData, CancellationToken cancellationToken = default);
    Task<ComponentInfo> GetComponentAsync(string id, CancellationToken cancellationToken = default);
    Task<string> CreateSlotAsync(SlotCreateRequest request, CancellationToken cancellationToken = default);
    Task UpdateSlotAsync(SlotUpdateRequest request, CancellationToken cancellationToken = default);
    Task DeleteSlotAsync(string id, CancellationToken cancellationToken = default);
    Task<ComponentCreateResult> AddComponentAsync(string slotId, string componentType,
        IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default);
    Task SetComponentMemberAsync(string componentId, string member, string rawValue,
        CancellationToken cancellationToken = default);
    Task SetComponentMembersAsync(string componentId, string componentType,
        IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default);
    Task RemoveComponentAsync(string componentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> SearchComponentTypesAsync(string query, int limit,
        CancellationToken cancellationToken = default);
    Task<ComponentTypeInfo> DescribeComponentTypeAsync(string type,
        CancellationToken cancellationToken = default);
    Task<TypeInfo> DescribeTypeAsync(string type, CancellationToken cancellationToken = default);
    // Read-only conversion preflight; no world mutation or asset import.
    Task ValidateComponentMemberAsync(string componentType, string member, string rawValue,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task<SyncMethodCallResult> CallComponentMethodAsync(string componentId, string method,
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? arguments = null,
        CancellationToken cancellationToken = default) => Task.FromException<SyncMethodCallResult>(
            new RLoopException("SYNC_METHOD_UNAVAILABLE", "This Resonite adapter does not expose SyncMethod calls.", ExitCodes.OperationFailed));
    Task<string> ImportAssetAsync(ApplyAssetSpec asset, string resolvedSource,
        CancellationToken cancellationToken = default) => Task.FromException<string>(
            new RLoopException("ASSET_IMPORT_UNAVAILABLE", "This Resonite adapter does not expose asset imports.", ExitCodes.OperationFailed));
}

public interface IResoniteClientDiagnostics
{
    void ResetMetrics();
    ClientMetrics SnapshotMetrics();
}

public interface IFluxTool
{
    Task<FluxResult> BuildAsync(FluxBuildRequest request, CancellationToken cancellationToken = default);
    Task<FluxResult> CheckAsync(FluxBuildRequest request, CancellationToken cancellationToken = default);
    Task<FluxResult> WatchAsync(FluxBuildRequest request, CancellationToken cancellationToken = default);
    Task<FluxToolStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Places a compiled ProtoGraph module in two separate steps (ROADMAP-9, P1-b). Types are in FluxDeployContracts.cs.
/// Every deployment goes through <see cref="FluxDeployGuard"/>; nothing else calls <see cref="ExecuteAsync"/>.
/// </summary>
public interface IFluxDeployer
{
    /// <summary>Compiles only. Never connects to or writes to the world.</summary>
    Task<FluxDeployPreparation> PrepareAsync(FluxDeployPrepareRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compiles again and, only when that compile is clean and declares the expected module name: removes the exact
    /// previous root (if given), sends the creation batch once, and reads the new root ID from its first inner response.
    /// Failures are reported through the result, not by throwing.
    /// </summary>
    Task<FluxDeployExecution> ExecuteAsync(FluxDeployExecuteRequest request, CancellationToken cancellationToken = default);
}

public sealed record FluxBuildRequest(
    string Source,
    string? ProjectDirectory,
    string? Output,
    string? LibraryPath,
    bool CompactErrors = true);

public sealed record FluxDiagnostic(
    string? File,
    int? StartLine,
    int? StartColumn,
    int? EndLine,
    int? EndColumn,
    string Severity,
    string Message,
    string Channel,
    string Category,
    bool IsPrimary);

/// <param name="Verdict">How a build or check was judged from its diagnostics (P9); null for other Flux-SDK commands.</param>
public sealed record FluxResult(bool Success, int ExitCode, string StandardOutput, string StandardError,
    string? OutputPath = null,
    IReadOnlyList<FluxDiagnostic>? Diagnostics = null,
    IReadOnlyList<FluxDiagnostic>? PrimaryDiagnostics = null,
    FluxBuildVerdict? Verdict = null);

/// <summary>
/// The judgement of one Flux-SDK build or check (ROADMAP-9, P9). Success is decided by the error diagnostics in its
/// output, not by the exit code alone: Flux-SDK 1.9.0 exits 1 for a build with warnings only, and writes its
/// diagnostics to stdout. A build is only an early check; the deployer's own compile decides what is placed.
/// </summary>
/// <param name="Basis">
/// <c>clean</c> (exit 0, no error), <c>warningsOnly</c> (non-zero exit explained by warnings and a summary of 0 errors),
/// <c>errorDiagnostics</c> (at least one error diagnostic, whatever the exit code), <c>reportedErrors</c> (the summary
/// line reports errors that were not parsed as diagnostics), <c>unexplainedExitCode</c> (non-zero exit that no parsed
/// diagnostic explains), or <c>toolReportedFailure</c> (the tool reported failure without output that explains it).
/// </param>
/// <param name="ErrorCount">Parsed diagnostics with severity <c>error</c>.</param>
/// <param name="WarningCount">Parsed diagnostics with severity <c>warning</c>.</param>
/// <param name="ReportedErrorCount">Errors stated by the SDK's summary line, when there is one.</param>
public sealed record FluxBuildVerdict(bool Success, string Basis, int ExitCode, int ErrorCount, int WarningCount,
    int? ReportedErrorCount, string Detail);

public sealed record FluxToolStatus(bool Available, string Executable, string? Version);
