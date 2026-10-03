using System.Text.Json;
using System.Text.Json.Serialization;

namespace RLoop.Core;

// Contract between the deploy guard (Core/Flux) and the F# deployer (ROADMAP-9, P1-b).
// Nothing here references Flux-SDK or ResoniteLink types.

/// <summary>Serializes the deploy enums as camelCase strings (<c>notSent</c>, <c>partialCreated</c>).</summary>
public sealed class FluxDeployEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.CamelCase)
    where TEnum : struct, Enum;

/// <summary>How an <see cref="IFluxDeployer.ExecuteAsync"/> call ended.</summary>
[JsonConverter(typeof(FluxDeployEnumConverter<FluxDeployOutcome>))]
public enum FluxDeployOutcome
{
    /// <summary>The deployer's own compile has error diagnostics, no parsed module, or an empty declared module name. Nothing was sent.</summary>
    CompileFailed,
    /// <summary>The compiled declared module name differs from <see cref="FluxDeployExecuteRequest.ExpectedModuleName"/>. Nothing was sent.</summary>
    ModuleNameMismatch,
    /// <summary>
    /// Stopped before any world-writing message for another reason: invalid request, connection failure, missing parent,
    /// a previous root that is not the expected child, an unexpected creation batch shape, or cancellation before sending.
    /// </summary>
    NotSent,
    /// <summary>The removal of the previous root was answered with a failure other than "not found". No creation was sent.</summary>
    RemoveFailed,
    /// <summary>The removal was answered (removed, or "not found") and the deployer stopped before starting the creation batch.</summary>
    RemovedNotCreated,
    /// <summary>
    /// The creation batch was answered, but at least one inner response failed, was not a NewEntityId, was missing,
    /// or the first entity ID differs from the requested root ID. Part of the module may exist. Never treat as settled.
    /// </summary>
    PartialCreated,
    /// <summary>Every inner response of the creation batch succeeded; <see cref="FluxDeployExecution.NewRootSlotId"/> is the new module root.</summary>
    Created,
    /// <summary>Exception, deadline, cancellation, or disconnect after a world-writing message may have been sent.</summary>
    Unknown
}

/// <summary>The furthest step an execution is known to have reached.</summary>
[JsonConverter(typeof(FluxDeployEnumConverter<FluxDeployStage>))]
public enum FluxDeployStage
{
    /// <summary>No world-writing message was sent.</summary>
    NotSent,
    /// <summary>The removal of the previous root was started; its answer is a failure or was not received.</summary>
    Removing,
    /// <summary>The removal was answered with success or "not found"; the creation batch was not started.</summary>
    Removed,
    /// <summary>The creation batch was started; its answer is a failure, partial, or was not received.</summary>
    Creating,
    /// <summary>The creation batch was answered and every inner response succeeded.</summary>
    Created,
    /// <summary>The deployer's work did not stop within the grace period after its connection was closed; the step it is in cannot be stated.</summary>
    Unknown
}

/// <summary>Whether any world-writing message (removal or creation batch) left the deployer.</summary>
[JsonConverter(typeof(FluxDeployEnumConverter<FluxDeploySendStatus>))]
public enum FluxDeploySendStatus
{
    /// <summary>Proven: no world-writing message was sent.</summary>
    NotSentProven,
    /// <summary>At least one world-writing message was sent and answered.</summary>
    Sent,
    /// <summary>A world-writing message may or may not have reached the world.</summary>
    Unknown
}

/// <summary>What happened to <see cref="FluxDeployExecuteRequest.PreviousRootSlotId"/>.</summary>
[JsonConverter(typeof(FluxDeployEnumConverter<FluxPreviousRootRemoval>))]
public enum FluxPreviousRootRemoval
{
    /// <summary>No previous root was given, or the execution stopped before the removal step.</summary>
    NotAttempted,
    /// <summary>The exact ID was removed; see <see cref="FluxDeployExecution.RemovedSlotId"/>.</summary>
    Removed,
    /// <summary>
    /// The world answered "not found" for the exact ID, and the execution continued to the creation. On the read before
    /// the removal nothing is sent for it (the stage stays NotSent until the creation starts); on the removal itself the stage is Removed.
    /// </summary>
    NotFound,
    /// <summary>The removal was answered with another failure. The execution stopped.</summary>
    Failed,
    /// <summary>The removal was started and its answer was not received.</summary>
    Unknown
}

public sealed record FluxDeployPrepareRequest(
    string ProjectDirectory,
    string Module,
    string? LibraryPath);

/// <summary>
/// A port of the compiled module, read from the creation operations the SDK builds (not from source text).
/// </summary>
/// <param name="Name">Port name as used for InputMap/OutputMap keys.</param>
/// <param name="Direction"><c>source</c> for an input port, <c>drive</c> for an output port (the manifest binding modes).</param>
/// <param name="Modifier"><c>element</c>, <c>global</c>, or null for a plain input; always null for outputs.</param>
/// <param name="SlotName">Name of the port Slot (<c>Input:…</c> / <c>Output:…</c>).</param>
/// <param name="SlotId">ID the SDK requests for the port Slot. Only meaningful for the operations of an execution; preparation IDs are never sent.</param>
/// <param name="ComponentTypes">Component types added to the port Slot, in order.</param>
/// <param name="ExpectedCarrierType">
/// The single component type that carries the binding (GlobalReference for inputs, FieldDriveBase proxy for outputs),
/// or null when there is not exactly one candidate.
/// </param>
public sealed record FluxModulePortInfo(
    string Name,
    string Direction,
    string? Modifier,
    string SlotName,
    string? SlotId,
    IReadOnlyList<string> ComponentTypes,
    string? ExpectedCarrierType);

/// <summary>Plain source declarations retained from the target-type preflight. No compiler or transport models.</summary>
public sealed record FluxDeclaredPortExpectation(string Name, string Direction, string Type, string? Modifier);

/// <summary>Result of compiling only. The world is never contacted.</summary>
/// <param name="Success">False when there is an error diagnostic, no parsed module, an empty declared module name, or the compile threw.</param>
/// <param name="DeclaredModuleName">The name in the source's <c>module</c> declaration; null unless <paramref name="Success"/>.</param>
/// <param name="Diagnostics">Compiler diagnostics (errors and warnings) with channel <c>deployer</c>.</param>
/// <param name="Ports">Ports of the compiled module; null when they could not be read (always null unless <paramref name="Success"/>).</param>
/// <param name="Error">Summary of the failure (first error diagnostic, unparsable module, empty name, or exception); null when <paramref name="Success"/>.</param>
public sealed record FluxDeployPreparation(
    bool Success,
    string? DeclaredModuleName,
    IReadOnlyList<FluxDiagnostic> Diagnostics,
    IReadOnlyList<FluxModulePortInfo>? Ports,
    string? Error,
    string StandardOutput,
    string StandardError);

/// <param name="ExpectedModuleName">Declared module name from a preceding preparation; execution stops with nothing sent when its own compile declares another name.</param>
/// <param name="PreviousRootSlotId">Exact ID of the recorded previous module root to remove before creating, or null to remove nothing.</param>
/// <param name="Deadline">
/// Time allowed from opening the deployer's connection until the creation batch is answered. When it passes, the
/// deployer closes its own connection and answers <see cref="FluxDeployOutcome.Unknown"/> (or NotSent when nothing was sent).
/// </param>
public sealed record FluxDeployExecuteRequest(
    string ProjectDirectory,
    string Module,
    string ParentSlotId,
    Uri Url,
    string? LibraryPath,
    string? HelperPath,
    IReadOnlyDictionary<string, string>? InputMap,
    IReadOnlyDictionary<string, string>? OutputMap,
    string ExpectedModuleName,
    string? PreviousRootSlotId,
    TimeSpan Deadline)
{
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromMinutes(2);
    public FluxDeployWriterIdentity? ExpectedWriterIdentity { get; init; }
    public IReadOnlyList<FluxModulePortInfo>? ExpectedPorts { get; init; }
    public bool RequireAllPortsBound { get; init; }
}

/// <summary>Versions read through the writer connection and separately matched announcement evidence. Never a connection-bound S-ID proof.</summary>
public sealed record FluxDeployWriterIdentity(string NormalizedUrl, string? DiscoverSessionId, string IdentityStatus,
    string? ResoniteVersion, string? ResoniteLinkVersion);

public sealed record FluxWriterIdentityObservation(string Checkpoint, FluxDeployWriterIdentity Identity);

/// <summary>Result of one P1-b execution: compile, remove the exact previous root, send the creation batch once.</summary>
/// <param name="RemovedSlotId">The previous root ID, only when its removal was answered with success.</param>
/// <param name="RequestedRootSlotId">The root ID the SDK put into the creation batch; a candidate ID when the outcome is not Created.</param>
/// <param name="NewRootSlotId">
/// Entity ID of the first inner response of the creation batch. Confirmed only when <see cref="Outcome"/> is Created;
/// otherwise a candidate.
/// </param>
/// <param name="BatchFailures">One line per problem found in the creation batch answer; empty when it was not answered or is clean.</param>
/// <param name="Ports">Ports read from the operations of this execution; null when the batch was not built.</param>
/// <param name="Error">Summary of what stopped the execution; null when Created.</param>
public sealed record FluxDeployExecution(
    FluxDeployOutcome Outcome,
    FluxDeployStage Stage,
    FluxDeploySendStatus SendStatus,
    FluxPreviousRootRemoval PreviousRootRemoval,
    string? DeclaredModuleName,
    string? RemovedSlotId,
    string? RequestedRootSlotId,
    string? NewRootSlotId,
    IReadOnlyList<FluxDiagnostic> Diagnostics,
    IReadOnlyList<string> BatchFailures,
    IReadOnlyList<FluxModulePortInfo>? Ports,
    string? Error,
    string StandardOutput,
    string StandardError)
{
    public bool Success => Outcome == FluxDeployOutcome.Created;
    public IReadOnlyList<FluxWriterIdentityObservation> WriterObservations { get; init; } = [];
    public FluxDeployWriterIdentity? ExpectedWriterIdentity { get; init; }
    public FluxDeployExecution WithWriterIdentityEvidence(FluxDeployWriterIdentity? expected, IReadOnlyList<FluxWriterIdentityObservation> observations) =>
        this with { ExpectedWriterIdentity = expected, WriterObservations = observations };
    public FluxDeployExecution WithCompileEvidence(IReadOnlyList<FluxDiagnostic> diagnostics, string stdout, string stderr) =>
        this with { Diagnostics = diagnostics, StandardOutput = stdout, StandardError = stderr };
}

/// <summary>A creation operation as the deployer is about to send it, reduced to plain values.</summary>
/// <param name="Kind">Operation type name (<c>AddSlot</c>, <c>AddComponent</c>, …).</param>
public sealed record FluxSentOperation(
    int Index,
    string Kind,
    string? Id,
    string? SlotName,
    string? ParentSlotId,
    string? ContainerSlotId,
    string? ComponentType);

/// <summary>One inner response of the creation batch, reduced to plain values.</summary>
/// <param name="Kind">Response type name; <c>NewEntityId</c> is the only kind that counts as created. <c>null</c> for a missing response.</param>
public sealed record FluxBatchInnerResponse(
    int Index,
    string Kind,
    bool Success,
    string? EntityId,
    string? ErrorInfo);

/// <param name="Created">True only when the batch and every inner response are clean.</param>
/// <param name="NewRootSlotId">Entity ID of the first inner response when it is a successful NewEntityId; a candidate unless <paramref name="Created"/>.</param>
public sealed record FluxBatchClassification(
    bool Created,
    string? NewRootSlotId,
    IReadOnlyList<string> Failures);

/// <summary>Pure rules the deployer applies to the answers it receives. Kept here so they are testable offline.</summary>
public static class FluxDeployClassifier
{
    /// <summary>Joins source preflight declarations to the preparation's actual generated binding carriers.</summary>
    public static string? ValidatePreparedPortTypes(IReadOnlyList<FluxDeclaredPortExpectation>? declared,
        IReadOnlyList<FluxModulePortInfo> prepared, IReadOnlyList<FluxDeployPortKey> bound)
    {
        if (prepared.GroupBy(port => port.Name, StringComparer.Ordinal).Any(group => group.Count() != 1))
            return "The preparation reports duplicate port names.";
        if (declared is not null && (declared.Count != prepared.Count ||
            declared.Any(expectation => !prepared.Any(port => port.Name == expectation.Name &&
                port.Direction == expectation.Direction && port.Modifier == expectation.Modifier))))
            return "The preparation ports differ from source preflight declarations (name, mode or modifier).";
        foreach (var key in bound)
        {
            var port = prepared.SingleOrDefault(entry => entry.Name == key.Port && entry.Direction == key.Mode);
            if (port is null) return $"The preparation has no unique port for '{key.Port}' ({key.Mode}).";
            var carriers = port.ComponentTypes.Where(type => key.Mode == "source"
                    ? type.Contains("GlobalReference<", StringComparison.Ordinal)
                    : type.Contains("FieldDriveBase<", StringComparison.Ordinal) && type.EndsWith("+Proxy", StringComparison.Ordinal))
                .ToArray();
            var slotPrefix = key.Mode == "drive" ? "Output:" : port.Modifier switch
            { "element" => "Input:[Elem]", "global" => "Input:[Global]", _ => "Input:" };
            if (carriers.Length != 1 || port.ExpectedCarrierType != carriers[0] ||
                port.SlotName != slotPrefix + port.Name ||
                BindingType(port) is not { } actualType)
                return $"The actual generated binding carrier/type of '{port.Name}' is unknown or unsupported.";
            if (declared?.SingleOrDefault(entry => entry.Name == port.Name) is { } expectation &&
                !FluxDeployTypeNames.SameType(FluxDeployTypeNames.Normalize(expectation.Type), actualType))
                return $"The preparation binding type of '{port.Name}' is '{actualType}', but source preflight declared '{expectation.Type}'.";
        }
        return null;
    }

    // Wrapper forms are pinned SDK 1.9.0 evidence, not inferred assignability. Unsupported forms stay unknown.
    private static string? BindingType(FluxModulePortInfo port)
    {
        if (port.ExpectedCarrierType is null) return null;
        var carrier = FluxDeployTypeNames.Normalize(port.ExpectedCarrierType);
        if (port.Direction == "drive" && port.Modifier is null)
            return Argument(carrier, "FieldDriveBase", "+Proxy");
        if (port.Direction != "source" || Argument(carrier, "GlobalReference") is not { } target) return null;
        return port.Modifier switch
        {
            null => Argument(target, "IValue") ?? Argument(target, "SyncRef"),
            "element" => Argument(target, "Sync") ?? DirectType(target),
            "global" => DirectType(target),
            _ => null
        };

        static string? DirectType(string type) =>
            Argument(type, "IValue") is not null || Argument(type, "Sync") is not null || Argument(type, "SyncRef") is not null
                ? null : ValidType(type) ? type : null;
    }

    private static string? Argument(string type, string wrapper, string suffix = "")
    {
        var prefix = wrapper + "<";
        var ending = ">" + suffix;
        if (!type.StartsWith(prefix, StringComparison.Ordinal) || !type.EndsWith(ending, StringComparison.Ordinal)) return null;
        var argument = type[prefix.Length..^ending.Length];
        return ValidType(argument) ? argument : null;
    }

    private static bool ValidType(string type)
    {
        if (string.IsNullOrWhiteSpace(type)) return false;
        var index = 0;
        return ReadType() && index == type.Length;

        bool ReadType()
        {
            if (index == type.Length || !(char.IsLetter(type[index]) || type[index] == '_')) return false;
            while (index < type.Length && (char.IsLetterOrDigit(type[index]) || type[index] is '_' or '+' or '.')) index++;
            if (index == type.Length || type[index] != '<') return true;
            index++;
            if (!ReadType()) return false;
            while (index < type.Length && type[index] == ',')
            {
                index++;
                if (!ReadType()) return false;
            }
            if (index == type.Length || type[index] != '>') return false;
            index++;
            return true;
        }
    }

    /// <summary>Checks the actual second compile before connecting. Preparation IDs were never sent and are excluded.</summary>
    public static string? ValidateExecutionPorts(FluxDeployExecuteRequest request, IReadOnlyList<FluxModulePortInfo> actual)
    {
        var inputs = request.InputMap ?? new Dictionary<string, string>();
        var outputs = request.OutputMap ?? new Dictionary<string, string>();
        if (request.ExpectedPorts is null && (inputs.Count > 0 || outputs.Count > 0 || request.RequireAllPortsBound))
            return "Compiled preparation ports are unknown; binding preflight cannot be established.";
        if (request.ExpectedPorts is { } expected &&
            (expected.Count != actual.Count || expected.Where((port, index) => !SameShape(port, actual[index])).Any()))
            return "The execution port shape differs from preparation (name, mode, modifier, Slot name or component/carrier type).";
        if (actual.GroupBy(port => (port.Name, port.Direction)).Any(group => group.Count() != 1))
            return "The execution has duplicate port names/modes.";
        if (inputs.Keys.Any(key => !actual.Any(port => port.Name == key && port.Direction == "source")) ||
            outputs.Keys.Any(key => !actual.Any(port => port.Name == key && port.Direction == "drive")))
            return "Execution binding keys do not match the compiled port names and modes.";
        if (request.RequireAllPortsBound && actual.Any(port => !(port.Direction == "source" ? inputs : outputs).ContainsKey(port.Name)))
            return "Execution has an unbound compiled port although every port must be bound.";
        return null;

        static bool SameShape(FluxModulePortInfo a, FluxModulePortInfo b) =>
            a.Name == b.Name && a.Direction == b.Direction && a.Modifier == b.Modifier && a.SlotName == b.SlotName &&
            a.ExpectedCarrierType == b.ExpectedCarrierType && a.ComponentTypes.SequenceEqual(b.ComponentTypes, StringComparer.Ordinal);
    }

    public static string? WriterIdentityMismatch(FluxDeployWriterIdentity? expected, FluxDeployWriterIdentity observed)
    {
        if (expected is null) return null;
        if (expected.NormalizedUrl != observed.NormalizedUrl) return "writer endpoint changed";
        if (expected.ResoniteVersion != observed.ResoniteVersion || expected.ResoniteLinkVersion != observed.ResoniteLinkVersion)
            return "writer versions changed";
        if (expected.DiscoverSessionId is not null && observed.DiscoverSessionId is not null &&
            expected.DiscoverSessionId != observed.DiscoverSessionId) return "writer announced S-ID changed";
        return null; // Missing/ambiguous announcements are explicitly unknown under F1, not a mismatch or proof.
    }
    public const string RootOperationKind = "AddSlot";
    public const string CreatedResponseKind = "NewEntityId";

    /// <summary>Same test Flux-SDK uses: the error text contains "not found" (live text: <c>Slot with ID '…' not found.</c>).</summary>
    public static bool IsSlotNotFound(string? errorInfo) =>
        !string.IsNullOrWhiteSpace(errorInfo) && errorInfo.Contains("not found", StringComparison.OrdinalIgnoreCase);

    public static FluxPreviousRootRemoval ClassifyRemoval(bool success, string? errorInfo) =>
        success ? FluxPreviousRootRemoval.Removed
        : IsSlotNotFound(errorInfo) ? FluxPreviousRootRemoval.NotFound
        : FluxPreviousRootRemoval.Failed;

    /// <summary>
    /// Checks that the creation batch starts with the module root: an AddSlot with an ID, named
    /// <paramref name="moduleName"/>, directly below <paramref name="parentSlotId"/>. Returns null when it does,
    /// otherwise the reason. A batch that fails this check must not be sent.
    /// </summary>
    public static string? ValidateRootOperation(IReadOnlyList<FluxSentOperation> operations, string parentSlotId, string moduleName)
    {
        if (operations.Count == 0) return "The creation batch is empty.";
        var root = operations[0];
        if (!string.Equals(root.Kind, RootOperationKind, StringComparison.Ordinal))
            return $"The first creation operation is '{root.Kind}', not {RootOperationKind}.";
        if (string.IsNullOrWhiteSpace(root.Id)) return "The module root operation has no ID.";
        if (!string.Equals(root.ParentSlotId, parentSlotId, StringComparison.Ordinal))
            return $"The module root operation targets parent '{root.ParentSlotId}', not '{parentSlotId}'.";
        if (!string.Equals(root.SlotName, moduleName, StringComparison.Ordinal))
            return $"The module root operation is named '{root.SlotName}', not '{moduleName}'.";
        return null;
    }

    /// <summary>
    /// A batch is not atomic and its outer success does not reflect inner failures, so every inner response is checked:
    /// the count must equal the number of sent operations, each must be a successful NewEntityId with an ID, and the
    /// first ID must equal the requested root ID (when one is known).
    /// </summary>
    public static FluxBatchClassification ClassifyBatch(bool outerSuccess, string? outerErrorInfo, int sentOperationCount,
        string? requestedRootSlotId, IReadOnlyList<FluxBatchInnerResponse>? responses)
    {
        var failures = new List<string>();
        if (!outerSuccess)
            failures.Add($"batch: {(string.IsNullOrWhiteSpace(outerErrorInfo) ? "answered without success" : outerErrorInfo)}");
        if (responses is null)
        {
            failures.Add($"batch: no inner responses for {sentOperationCount} operation(s)");
            return new FluxBatchClassification(false, null, failures);
        }
        if (responses.Count != sentOperationCount)
            failures.Add($"batch: {responses.Count} inner response(s) for {sentOperationCount} operation(s)");
        foreach (var response in responses)
        {
            if (!response.Success)
                failures.Add($"#{response.Index} {response.Kind}: {(string.IsNullOrWhiteSpace(response.ErrorInfo) ? "failed without error text" : response.ErrorInfo)}");
            else if (!string.Equals(response.Kind, CreatedResponseKind, StringComparison.Ordinal))
                failures.Add($"#{response.Index} {response.Kind}: expected {CreatedResponseKind}");
            else if (string.IsNullOrWhiteSpace(response.EntityId))
                failures.Add($"#{response.Index} {response.Kind}: no entity ID");
        }
        string? rootId = null;
        if (responses.Count > 0 && responses[0] is { Success: true, Kind: CreatedResponseKind } first &&
            !string.IsNullOrWhiteSpace(first.EntityId))
        {
            rootId = first.EntityId;
            if (!string.IsNullOrWhiteSpace(requestedRootSlotId) &&
                !string.Equals(rootId, requestedRootSlotId, StringComparison.Ordinal))
                failures.Add($"#0 {CreatedResponseKind}: entity ID '{rootId}' differs from the requested root ID '{requestedRootSlotId}'");
        }
        return new FluxBatchClassification(failures.Count == 0 && rootId is not null, rootId, failures);
    }

    /// <summary>
    /// Reads the module's ports from the creation operations: Slots named <c>Input:…</c> / <c>Output:…</c> and the
    /// components added to them. The carrier type is stated only when exactly one component type matches.
    /// </summary>
    public static IReadOnlyList<FluxModulePortInfo> ReadPorts(IReadOnlyList<FluxSentOperation> operations)
    {
        var ports = new List<FluxModulePortInfo>();
        foreach (var slot in operations)
        {
            if (!string.Equals(slot.Kind, RootOperationKind, StringComparison.Ordinal) || slot.SlotName is not { } slotName) continue;
            string direction, name;
            string? modifier = null;
            if (slotName.StartsWith(ElementInputPrefix, StringComparison.Ordinal))
                (direction, modifier, name) = ("source", "element", slotName[ElementInputPrefix.Length..]);
            else if (slotName.StartsWith(GlobalInputPrefix, StringComparison.Ordinal))
                (direction, modifier, name) = ("source", "global", slotName[GlobalInputPrefix.Length..]);
            else if (slotName.StartsWith(InputPrefix, StringComparison.Ordinal))
                (direction, name) = ("source", slotName[InputPrefix.Length..]);
            else if (slotName.StartsWith(OutputPrefix, StringComparison.Ordinal))
                (direction, name) = ("drive", slotName[OutputPrefix.Length..]);
            else continue;

            var types = string.IsNullOrWhiteSpace(slot.Id)
                ? []
                : operations.Where(operation => operation.ComponentType is not null &&
                        string.Equals(operation.ContainerSlotId, slot.Id, StringComparison.Ordinal))
                    .Select(operation => operation.ComponentType!).ToArray();
            var carriers = types.Where(type => direction == "source"
                ? type.Contains("GlobalReference<", StringComparison.Ordinal)
                : type.Contains("FieldDriveBase<", StringComparison.Ordinal) && type.EndsWith("+Proxy", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal).ToArray();
            ports.Add(new FluxModulePortInfo(name, direction, modifier, slotName, slot.Id, types,
                carriers.Length == 1 ? carriers[0] : null));
        }
        return ports;
    }

    private const string InputPrefix = "Input:";
    private const string ElementInputPrefix = "Input:[Elem]";
    private const string GlobalInputPrefix = "Input:[Global]";
    private const string OutputPrefix = "Output:";
}
