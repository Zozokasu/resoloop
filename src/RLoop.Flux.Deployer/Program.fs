namespace RLoop.Flux.Deployer

open System
open System.IO
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open System.Security.Cryptography
open FluxSDK.Common
open FluxSDK.Build.Incremental
open FluxSDK.ResoniteLink
open ResoniteLink
open RLoop.Core

type private SdkWriterConnection(link: LinkInterface, url: Uri, operations: List<DataModelOperation>, rootId: string | null,
                                  removeSlot: string -> Task<Result<unit, string>>) =
    interface IFluxWriterConnection with
        member _.IsClosed = not link.IsConnected
        member _.Dispose() = link.Dispose()
        member _.GetIdentityAsync() =
            task {
                let! session = link.GetSessionData()
                if isNull (box session) || not session.Success then failwith "Writer session versions could not be read."
                return FluxDeployWriterIdentity(ApplySessionObservation.NormalizeUrl(url.AbsoluteUri), null, "unknown", session.ResoniteVersion, session.ResoniteLinkVersion)
            }
        member _.ReadSlotAsync(id) =
            task {
                let! answer = link.GetSlotData(GetSlot(SlotID = id, Depth = 0, IncludeComponentData = false))
                if isNull (box answer) then failwith "Unexpected slot answer."
                if not answer.Success then
                    if FluxDeployClassifier.IsSlotNotFound(answer.ErrorInfo) then return FluxWriterSlot(id, "", "", false)
                    else return failwith answer.ErrorInfo
                else
                    let data = answer.Data
                    if isNull (box data) then return failwith "Missing slot data."
                    else return FluxWriterSlot(data.ID, (if isNull (box data.Name) then "" else data.Name.Value), (if isNull (box data.Parent) then "" else data.Parent.TargetID), true)
            }
        member _.ReadChildrenAsync(id) =
            task {
                let! answer = link.GetSlotData(GetSlot(SlotID = id, Depth = 1, IncludeComponentData = false))
                if isNull (box answer) || not answer.Success || isNull (box answer.Data) then
                    failwith "The writer could not read the exact parent and its direct children."
                let parent = answer.Data
                // SlotData.Depth is not completeness evidence (live observations report 0 for every requested depth).
                if parent.IsReferenceOnly || parent.ID <> id || String.Equals(parent.ID, "Root", StringComparison.OrdinalIgnoreCase) ||
                   isNull (box parent.Parent) || String.IsNullOrWhiteSpace(parent.Parent.TargetID) || isNull (box parent.Name) || isNull (box parent.Name.Value) then
                    failwith "The writer's parent is Root or was not read in full with its name and parent."
                let children = ResizeArray<FluxWriterSlot>()
                // A full exact parent with Children=null is a leaf, as verified by the existing live adapter evidence.
                if not (isNull (box parent.Children)) then
                    for child in parent.Children do
                        if isNull (box child) || child.IsReferenceOnly || String.IsNullOrWhiteSpace(child.ID) ||
                           isNull (box child.Name) || isNull (box child.Name.Value) || isNull (box child.Parent) || child.Parent.TargetID <> id then
                            failwith "A direct child was not read in full with its exact ID, name and parent."
                        children.Add(FluxWriterSlot(child.ID, child.Name.Value, child.Parent.TargetID, true))
                return children.ToArray() :> IReadOnlyList<FluxWriterSlot>
            }
        member _.RemoveAsync(id) =
            task {
                let! answer = removeSlot id
                return match answer with | Ok () -> FluxPreviousRootRemoval.Removed | Error message -> FluxDeployClassifier.ClassifyRemoval(false, message)
            }
        member _.CreateAsync() =
            task {
                let! answer = link.RunDataModelOperationBatch(operations)
                if isNull (box answer) then failwith "Unexpected creation answer."
                if isNull (box answer.Responses) then
                    return FluxDeployClassifier.ClassifyBatch(answer.Success, answer.ErrorInfo, operations.Count, rootId, null)
                else
                    let inner = answer.Responses |> nonNull |> Seq.mapi(fun index response ->
                        if isNull (box response) then FluxBatchInnerResponse(index, "null", false, null, "missing response")
                        else
                            let id = match response with | :? NewEntityId as created -> created.EntityId | _ -> null
                            FluxBatchInnerResponse(index, response.GetType().Name, response.Success, id, response.ErrorInfo)) |> Seq.toArray
                    return FluxDeployClassifier.ClassifyBatch(answer.Success, answer.ErrorInfo, operations.Count, rootId, inner)

            }

// ROADMAP-9 P1-b. Compiles with Flux-SDK's own compiler and places the module with its public parts:
// Build.moduleCompiler -> Loader.removeSlot (exact ID) -> Link.batchAddProtoGraphNodes (sent once).
// Loader.replace is not used: it removes every same-named child and ignores compile diagnostics.
// The rules applied to the answers live in RLoop.Core.FluxDeployClassifier so they are testable offline.

// Call only inside FluxConsoleCapture: the SDK epoch and counter are process-global,
// and the same gate must cover rotation, compilation and packing.
module private ElementIdEpoch =
    let mutable private seeded = false

    let rotate () =
        if not seeded then
            // Independent CLI processes must not replay IDs spent in the same world session.
            // Reserve the top byte for this process's subsequent monotonic rotations.
            let candidate = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(sizeof<uint64>)) &&& 0x00FF_FFFF_FFFF_FFFFUL
            ElementID.setEpoch (if candidate = 0UL then 1UL else candidate) |> ignore
            seeded <- true
        ElementID.nextEpoch ()

module private Deploy =

    let placeholderParent = "ResoLoop.Prepare.NotSent"

    let withConsole cancellationToken body =
        FluxConsoleCapture.RunAsync(Func<StringWriter, StringWriter, Task<_>>(fun stdout stderr -> body stdout stderr), cancellationToken)

    let toDiagnostics (compiled: LaidOutModule) : FluxDiagnostic[] =
        compiled.Diagnostics
        |> Seq.map (fun diagnostic ->
            let severity =
                if diagnostic.Level.IsError then "error"
                elif diagnostic.Level.IsWarn then "warning"
                elif diagnostic.Level.IsHint then "hint"
                else "info"

            let start = diagnostic.Location.Start
            let finish = diagnostic.Location.End

            FluxDiagnostic(
                start.StreamName,
                Nullable(int start.Line),
                Nullable(int start.Column),
                Nullable(int finish.Line),
                Nullable(int finish.Column),
                severity,
                diagnostic.Contents,
                "deployer",
                "compiler",
                (severity = "error")
            ))
        |> Seq.toArray

    /// Why a compile result must not be sent, or None when it may.
    let compileFailure (modulePath: string) (compiled: LaidOutModule) (diagnostics: FluxDiagnostic[]) : string option =
        let errors = diagnostics |> Array.filter (fun diagnostic -> diagnostic.Severity = "error")

        if errors.Length > 0 then
            let first = errors[0]
            Some $"Module '{modulePath}' has {errors.Length} compile error(s); first: {first.File}({first.StartLine},{first.StartColumn}): {first.Message}"
        elif Option.isNone compiled.ParsedModule then
            Some $"Module '{modulePath}' could not be parsed."
        elif String.IsNullOrWhiteSpace(compiled.ModuleName) then
            Some $"Module '{modulePath}' declares no module name."
        else
            None

    /// Compiles in a fresh store. Does not use a link.
    let compile (projectDirectory: string) (modulePath: string) (libraryPath: string | null) =
        task {
            let paths =
                match libraryPath with
                | null -> [||]
                | path when String.IsNullOrWhiteSpace(path) -> [||]
                | path -> [| Path.GetFullPath(path) |]

            let store =
                if paths.Length = 0 then Build.initializeStore ()
                else Build.initializeStoreWith (paths)

            // Set the incremental input before any query allocates or memoizes IDs.
            let epoch = ElementIdEpoch.rotate ()
            store.SetInput(FluxSDK.Resolving.Incremental.ElementIDEpochKey.Key, epoch)

            let project = Path.GetFullPath(projectDirectory)
            let manifest = Build.loadManifest (project)
            let config = Build.defaultConfig (manifest)
            store.SetInput(BuildConfigKey(), config)
            let! compiled = Step.runStepAsync (Build.moduleCompiler project modulePath) store
            return store, config, compiled
        }

    let describeOperation (index: int) (operation: DataModelOperation) : FluxSentOperation =
        let kind = operation.GetType().Name

        match operation with
        | :? AddUpdateSlotData as slot when not (isNull (box slot.Data)) ->
            let name: string | null = if isNull (box slot.Data.Name) then null else slot.Data.Name.Value
            let parent: string | null = if isNull (box slot.Data.Parent) then null else slot.Data.Parent.TargetID
            FluxSentOperation(index, kind, slot.Data.ID, name, parent, null, null)
        | :? AddComponent as addition when not (isNull (box addition.Data)) ->
            FluxSentOperation(index, kind, addition.Data.ID, null, null, addition.ContainerSlotId, addition.Data.ComponentType)
        | _ -> FluxSentOperation(index, kind, null, null, null, null, null)

    let describeOperations (operations: List<DataModelOperation>) : FluxSentOperation[] =
        operations |> Seq.mapi describeOperation |> Seq.toArray

    let describeResponse (index: int) (response: Response) : FluxBatchInnerResponse =
        if isNull (box response) then
            FluxBatchInnerResponse(index, "null", false, null, "missing response")
        else
            let entityId: string | null =
                match response with
                | :? NewEntityId as created -> created.EntityId
                | _ -> null

            FluxBatchInnerResponse(index, response.GetType().Name, response.Success, entityId, response.ErrorInfo)

    let invalidRequest (request: FluxDeployExecuteRequest) : string option =
        if String.IsNullOrWhiteSpace(request.ParentSlotId) then
            Some "An exact parent slot ID is required."
        elif String.IsNullOrWhiteSpace(request.ExpectedModuleName) then
            Some "The expected module name is required."
        elif request.Deadline <= TimeSpan.Zero then
            Some "The deadline must be positive."
        else
            match request.PreviousRootSlotId with
            | null -> None
            | previous when String.IsNullOrWhiteSpace(previous) -> Some "The previous root ID is blank; pass null to remove nothing."
            | previous when String.Equals(previous, request.ParentSlotId, StringComparison.Ordinal) ->
                Some "The previous root ID is the parent slot ID; the parent is never removed."
            | _ -> None

    let emptyMap () : IReadOnlyDictionary<string, string> =
        Dictionary<string, string>() :> IReadOnlyDictionary<string, string>

type FluxSdkDeployer() =
    interface IFluxDeployer with
        member _.PrepareAsync(request: FluxDeployPrepareRequest, cancellationToken: CancellationToken) : Task<FluxDeployPreparation> =
            Deploy.withConsole cancellationToken (fun stdout stderr ->
                task {
                    let noDiagnostics: FluxDiagnostic[] = [||]

                    try
                        cancellationToken.ThrowIfCancellationRequested()
                        let! (_, config, compiled) = Deploy.compile request.ProjectDirectory request.Module request.LibraryPath
                        let diagnostics = Deploy.toDiagnostics compiled

                        match Deploy.compileFailure request.Module compiled diagnostics with
                        | Some failure ->
                            return FluxDeployPreparation(false, null, diagnostics, null, failure, stdout.ToString(), stderr.ToString())
                        | None ->
                            // The operations are built only to read the ports. They are never sent, so their IDs are dropped.
                            let ports =
                                try
                                    Link.batchAddProtoGraphNodes (config, Deploy.placeholderParent, compiled)
                                    |> Deploy.describeOperations
                                    |> FluxDeployClassifier.ReadPorts
                                    |> Seq.map (fun port ->
                                        FluxModulePortInfo(
                                            port.Name,
                                            port.Direction,
                                            port.Modifier,
                                            port.SlotName,
                                            null,
                                            port.ComponentTypes,
                                            port.ExpectedCarrierType
                                        ))
                                    |> Seq.toArray
                                    |> Some
                                with error ->
                                    stderr.WriteLine($"Ports could not be read: {error.Message}")
                                    None

                            match ports with
                            | Some known ->
                                return
                                    FluxDeployPreparation(true, compiled.ModuleName, diagnostics, known, null, stdout.ToString(), stderr.ToString())
                            | None ->
                                return
                                    FluxDeployPreparation(true, compiled.ModuleName, diagnostics, null, null, stdout.ToString(), stderr.ToString())
                    with error ->
                        stderr.WriteLine(error.ToString())

                        return
                            FluxDeployPreparation(
                                false,
                                null,
                                noDiagnostics,
                                null,
                                $"Module '{request.Module}' could not be compiled: {error.Message}",
                                stdout.ToString(),
                                stderr.ToString()
                            )
                })

        member _.ExecuteAsync(request: FluxDeployExecuteRequest, cancellationToken: CancellationToken) : Task<FluxDeployExecution> =
            Deploy.withConsole cancellationToken (fun stdout stderr ->
                task {
                    let tracker = Tracker()
                    let mutable diagnostics: FluxDiagnostic[] = [||]
                    let mutable declaredName: string | null = null
                    let mutable requestedRootId: string | null = null
                    let mutable ports: IReadOnlyList<FluxModulePortInfo> | null = null
                    let mutable boundaryExecution: FluxDeployExecution option = None

                    try
                        match Deploy.invalidRequest request with
                        | Some reason -> tracker.Finish(FluxDeployOutcome.NotSent, reason)
                        | None ->
                            // Step 1: compile and check the diagnostics before anything is sent.
                            let! attempt =
                                task {
                                    try
                                        let! result = Deploy.compile request.ProjectDirectory request.Module request.LibraryPath
                                        return Ok result
                                    with error ->
                                        return Error error
                                }

                            match attempt with
                            | Error error ->
                                stderr.WriteLine(error.ToString())
                                tracker.Finish(FluxDeployOutcome.CompileFailed, $"Module '{request.Module}' could not be compiled: {error.Message}")
                            | Ok(store, config, compiled) ->
                                diagnostics <- Deploy.toDiagnostics compiled

                                match Deploy.compileFailure request.Module compiled diagnostics with
                                | Some failure -> tracker.Finish(FluxDeployOutcome.CompileFailed, failure)
                                | None ->
                                    let declared = compiled.ModuleName
                                    declaredName <- declared

                                    if not (String.Equals(declared, request.ExpectedModuleName, StringComparison.Ordinal)) then
                                        tracker.Finish(
                                            FluxDeployOutcome.ModuleNameMismatch,
                                            $"Module '{request.Module}' declares '{declared}', but '{request.ExpectedModuleName}' was expected; nothing was sent."
                                        )
                                    else
                                        let inputMap =
                                            match request.InputMap with
                                            | null -> Deploy.emptyMap ()
                                            | map -> map

                                        let outputMap =
                                            match request.OutputMap with
                                            | null -> Deploy.emptyMap ()
                                            | map -> map

                                        // Additions only. Nothing is sent until the link phase.
                                        let operations =
                                            Link.batchAddProtoGraphNodes (config, request.ParentSlotId, compiled, inputMap, outputMap)

                                        let described = Deploy.describeOperations operations
                                        ports <- FluxDeployClassifier.ReadPorts(described)

                                        match FluxDeployClassifier.ValidateRootOperation(described, request.ParentSlotId, declared) with
                                        | null ->
                                            let rootId = described[0].Id
                                            requestedRootId <- rootId

                                            if cancellationToken.IsCancellationRequested then
                                                tracker.Finish(FluxDeployOutcome.NotSent, "The operation was cancelled before connecting.")
                                            else
                                                let setLink (link: LinkInterface) = store.SetInput(Step.linkInterface, link)

                                                let removeSlot (slotId: string) =
                                                    Step.runStepAsync (Loader.removeSlot slotId) store

                                                let connect = Func<CancellationToken, Task<IFluxWriterConnection>>(fun token ->
                                                    task {
                                                        let link = Link.initialize(request.Url, token)
                                                        try
                                                            setLink link
                                                            return new SdkWriterConnection(link, request.Url, operations, rootId, removeSlot) :> IFluxWriterConnection
                                                        with error ->
                                                            link.Dispose()
                                                            return raise error
                                                    })
                                                let! execution = FluxWriterBoundary.ExecuteAsync(request, ports |> nonNull, rootId |> nonNull, connect, FreshWriterDiscovery(), cancellationToken)
                                                boundaryExecution <- Some execution
                                        | reason -> tracker.Finish(FluxDeployOutcome.NotSent, $"{reason} Nothing was sent.")
                    with error ->
                        stderr.WriteLine(error.ToString())
                        tracker.Fault(error.Message)

                    match boundaryExecution with
                    | Some execution -> return execution.WithCompileEvidence(diagnostics, stdout.ToString(), stderr.ToString())
                    | None ->
                        let progress = tracker.Snapshot

                        return
                            FluxDeployExecution(
                                defaultArg progress.Outcome FluxDeployOutcome.Unknown,
                                progress.Stage,
                                progress.Send,
                                progress.Removal,
                                declaredName,
                                progress.RemovedSlotId,
                                requestedRootId,
                                progress.NewRootSlotId,
                                diagnostics,
                                progress.BatchFailures,
                                ports,
                                progress.Error,
                                stdout.ToString(),
                                stderr.ToString()
                            )
                })
