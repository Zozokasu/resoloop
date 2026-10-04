namespace RLoop.Flux.Deployer

open System
open System.IO
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open RLoop.Core

/// Plain values crossing the injected transport seam; no SDK models are exposed.
type FluxWriterSlot(id: string, name: string, parentId: string, found: bool) =
    member _.Id = id
    member _.Name = name
    member _.ParentId = parentId
    member _.Found = found

type IFluxWriterConnection =
    inherit IDisposable
    abstract IsClosed: bool
    abstract GetIdentityAsync: unit -> Task<FluxDeployWriterIdentity>
    abstract ReadSlotAsync: string -> Task<FluxWriterSlot>
    abstract ReadChildrenAsync: string -> Task<IReadOnlyList<FluxWriterSlot>>
    abstract RemoveAsync: string -> Task<FluxPreviousRootRemoval>
    abstract CreateAsync: unit -> Task<FluxBatchClassification>

type IFluxWriterDiscovery =
    abstract ObserveAsync: Uri * DateTime * CancellationToken -> Task<IReadOnlyList<DiscoveredResoniteSession>>

/// Owns the process-global console writers and serializes SDK compilation/ID generation.
[<AbstractClass; Sealed>]
type FluxConsoleCapture private () =
    static let gate = new SemaphoreSlim(1, 1)
    static member RunAsync<'T>(body: Func<StringWriter, StringWriter, Task<'T>>, cancellationToken: CancellationToken) : Task<'T> =
        task {
            do! gate.WaitAsync(cancellationToken)
            try
                let oldOut, oldError = Console.Out, Console.Error
                use stdout = new StringWriter()
                use stderr = new StringWriter()
                try
                    Console.SetOut(stdout)
                    Console.SetError(stderr)
                    return! body.Invoke(stdout, stderr)
                finally
                    Console.SetOut(oldOut)
                    Console.SetError(oldError)
            finally
                gate.Release() |> ignore
        }

type private Progress =
    { Stage: FluxDeployStage
      Send: FluxDeploySendStatus
      Removal: FluxPreviousRootRemoval
      RemovedSlotId: string | null
      NewRootSlotId: string | null
      BatchFailures: string[]
      Outcome: FluxDeployOutcome option
      Error: string | null }

/// Where an execution stands. The link phase runs on another task than the one that enforces the deadline.
type private Tracker() =
    let gate = obj ()

    let mutable progress =
        { Stage = FluxDeployStage.NotSent
          Send = FluxDeploySendStatus.NotSentProven
          Removal = FluxPreviousRootRemoval.NotAttempted
          RemovedSlotId = null
          NewRootSlotId = null
          BatchFailures = [||]
          Outcome = None
          Error = null }

    let update (change: Progress -> Progress) = lock gate (fun () -> progress <- change progress)

    member _.Snapshot = lock gate (fun () -> progress)

    /// The read before the removal answered "not found": nothing is sent for the previous root.
    member _.PreviousAbsent() =
        update (fun p -> { p with Removal = FluxPreviousRootRemoval.NotFound })

    member _.BeginRemoval() =
        update (fun p ->
            { p with
                Stage = FluxDeployStage.Removing
                Send = FluxDeploySendStatus.Unknown
                Removal = FluxPreviousRootRemoval.Unknown })

    member _.RemovalAnswered(removal: FluxPreviousRootRemoval, slotId: string) =
        update (fun p ->
            { p with
                Stage =
                    (if removal = FluxPreviousRootRemoval.Failed then
                         FluxDeployStage.Removing
                     else
                         FluxDeployStage.Removed)
                Send = FluxDeploySendStatus.Sent
                Removal = removal
                RemovedSlotId = (if removal = FluxPreviousRootRemoval.Removed then slotId else null) })

    member _.BeginCreate() =
        update (fun p ->
            { p with
                Stage = FluxDeployStage.Creating
                Send = FluxDeploySendStatus.Unknown })

    member _.CreateAnswered(classification: FluxBatchClassification) =
        update (fun p ->
            let failures = Seq.toArray classification.Failures

            if classification.Created then
                { p with
                    Stage = FluxDeployStage.Created
                    Send = FluxDeploySendStatus.Sent
                    NewRootSlotId = classification.NewRootSlotId
                    BatchFailures = failures
                    Outcome = Some FluxDeployOutcome.Created
                    Error = null }
            else
                let first = if failures.Length = 0 then "no root entity ID" else failures[0]

                { p with
                    Send = FluxDeploySendStatus.Sent
                    NewRootSlotId = classification.NewRootSlotId
                    BatchFailures = failures
                    Outcome = Some FluxDeployOutcome.PartialCreated
                    Error = $"The creation batch was answered with {failures.Length} problem(s); first: {first}" })

    /// Ends with a decided outcome.
    member _.Finish(outcome: FluxDeployOutcome, error: string) =
        update (fun p -> { p with Outcome = Some outcome; Error = error })

    /// Ends on an exception. What may have reached the world follows from how far the execution was.
    member _.Fault(error: string) =
        update (fun p ->
            if p.Outcome.IsSome then
                p
            else
                let outcome =
                    if p.Send = FluxDeploySendStatus.NotSentProven then FluxDeployOutcome.NotSent
                    elif p.Stage = FluxDeployStage.Removed && p.Send = FluxDeploySendStatus.Sent then FluxDeployOutcome.RemovedNotCreated
                    else FluxDeployOutcome.Unknown

                { p with Outcome = Some outcome; Error = error })

    /// The deadline passed or the caller cancelled, and the connection was closed.
    member _.Interrupted(reason: string, settled: bool) =
        update (fun p ->
            if not settled then
                { p with
                    Stage = FluxDeployStage.Unknown
                    Send = FluxDeploySendStatus.Unknown
                    Outcome = Some FluxDeployOutcome.Unknown
                    Error = $"The connection was closed because {reason}, and the deployer did not stop in time; what reached the world is unknown." }
            else
                match p.Outcome with
                | Some FluxDeployOutcome.Created ->
                    { p with Outcome = Some FluxDeployOutcome.Unknown; Error = $"The connection was closed because {reason}; final writer identity was not checked." }
                | Some _ -> { p with Error = $"The connection was closed because {reason}. {p.Error}" }
                | None ->
                    { p with
                        Outcome = Some FluxDeployOutcome.Unknown
                        Error = $"The connection was closed because {reason}." })

/// Production and offline tests share connection ownership, identity gates and write ordering.
[<AbstractClass; Sealed>]
type FluxWriterBoundary private () =
    static member ExecuteAsync(request: FluxDeployExecuteRequest, ports: IReadOnlyList<FluxModulePortInfo>, rootId: string,
                               connect: Func<CancellationToken, Task<IFluxWriterConnection>>, discovery: IFluxWriterDiscovery,
                               cancellationToken: CancellationToken) : Task<FluxDeployExecution> =
        task {
            let tracker = Tracker()
            let observations = ResizeArray<FluxWriterIdentityObservation>()
            let observationGate = obj()
            use deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            deadline.CancelAfter(request.Deadline)
            let mutable connection: IFluxWriterConnection option = None
            let mutable closingConnection: IFluxWriterConnection option = None
            let connectionGate = obj()
            let mutable closed = false
            let close () =
                lock connectionGate (fun () ->
                    closed <- true
                    match connection with
                    | Some link ->
                        connection <- None
                        closingConnection <- Some link
                        try link.Dispose() with _ -> ()
                    | None -> ())
            let mutable known = request.ExpectedWriterIdentity
            let work =
                task {
                    try
                        match FluxDeployClassifier.ValidateExecutionPorts(request, ports) with
                        | null -> ()
                        | reason -> raise (InvalidOperationException(reason + " Nothing was sent."))
                        deadline.Token.ThrowIfCancellationRequested()
                        let! link = connect.Invoke(deadline.Token)
                        lock connectionGate (fun () ->
                            if closed then link.Dispose()
                            else connection <- Some link)
                        deadline.Token.ThrowIfCancellationRequested()
                        let observe checkpoint =
                            task {
                                deadline.Token.ThrowIfCancellationRequested()
                                let started = DateTime.UtcNow
                                let! announced = discovery.ObserveAsync(request.Url, started, deadline.Token)
                                let now = DateTime.UtcNow
                                let fresh = announced |> Seq.filter(fun entry -> entry.LastSeenUtc >= started && entry.LastSeenUtc <= now && now - entry.LastSeenUtc <= TimeSpan.FromSeconds(25.0))
                                let evidence = ApplySessionObservation.Observe(request.Url.AbsoluteUri, fresh)
                                let! versions = link.GetIdentityAsync()
                                deadline.Token.ThrowIfCancellationRequested()
                                let identity = FluxDeployWriterIdentity(versions.NormalizedUrl, evidence.DiscoverSessionId, evidence.IdentityStatus, versions.ResoniteVersion, versions.ResoniteLinkVersion)
                                lock observationGate (fun () -> observations.Add(FluxWriterIdentityObservation(checkpoint, identity)))
                                let expectedUrl = ApplySessionObservation.NormalizeUrl(request.Url.AbsoluteUri)
                                let mismatch =
                                    if identity.NormalizedUrl <> expectedUrl then ("writer endpoint differs from requested URL" : string | null)
                                    else FluxDeployClassifier.WriterIdentityMismatch(known, identity)
                                if not (isNull mismatch) then
                                    tracker.Finish((if tracker.Snapshot.Send = FluxDeploySendStatus.NotSentProven then FluxDeployOutcome.NotSent else FluxDeployOutcome.Unknown), mismatch |> nonNull)
                                    raise (InvalidOperationException(mismatch))
                                if not (isNull identity.DiscoverSessionId) || isNull known then known <- identity
                            }
                        let checkChildren (expectedRoot: string | null) =
                            task {
                                try
                                    deadline.Token.ThrowIfCancellationRequested()
                                    let! children = link.ReadChildrenAsync(request.ParentSlotId)
                                    if isNull (box children) then failwith "The parent's direct children were not read."
                                    if children |> Seq.exists(fun child -> isNull (box child) || not child.Found || String.IsNullOrWhiteSpace(child.Id) || isNull (box child.Name) || child.ParentId <> request.ParentSlotId) then
                                        failwith "A direct child was not read in full with its exact ID, name and parent."
                                    if children |> Seq.groupBy(fun child -> child.Id) |> Seq.exists(fun (_, entries) -> Seq.length entries <> 1) then
                                        failwith "The parent's direct children repeat an ID."
                                    let sameName = children |> Seq.filter(fun child -> child.Name = request.ExpectedModuleName) |> Seq.map(fun child -> child.Id) |> Seq.sort |> Seq.toArray
                                    let expected = match expectedRoot with | null -> [||] | root -> [|root|]
                                    if sameName <> expected then
                                        let ids = String.Join(", ", sameName)
                                        failwith $"The parent's same-name child IDs changed before writing (observed: [{ids}]); no child was adopted."
                                with error ->
                                    tracker.Finish((if tracker.Snapshot.Send = FluxDeploySendStatus.NotSentProven then FluxDeployOutcome.NotSent else FluxDeployOutcome.Unknown), error.Message)
                                    return raise error
                            }
                        let! parent = link.ReadSlotAsync(request.ParentSlotId)
                        if not parent.Found || parent.Id <> request.ParentSlotId then failwith "The exact parent could not be read."
                        match request.PreviousRootSlotId with
                        | null -> ()
                        | previousId ->
                            let! previous = link.ReadSlotAsync(previousId)
                            if not previous.Found then tracker.PreviousAbsent()
                            elif previous.Id <> previousId || previous.ParentId <> request.ParentSlotId || previous.Name <> request.ExpectedModuleName then
                                failwith "The previous root is not the exact expected child; nothing was removed."
                            else
                                do! observe "beforeRemoval"
                                let! current = link.ReadSlotAsync(previousId)
                                if not current.Found || current.Id <> previousId || current.ParentId <> request.ParentSlotId || current.Name <> request.ExpectedModuleName then
                                    failwith "The previous root changed during identity observation; nothing was removed."
                                do! checkChildren previousId
                                deadline.Token.ThrowIfCancellationRequested()
                                tracker.BeginRemoval()
                                let! removal = link.RemoveAsync(previousId)
                                tracker.RemovalAnswered(removal, previousId)
                                if removal = FluxPreviousRootRemoval.Failed then
                                    tracker.Finish(FluxDeployOutcome.RemoveFailed, "The exact previous root could not be removed.")
                                    failwith "Removal refused."
                        do! observe "beforeCreation"
                        // No previous root (including initially absent or removed/not-found) can be re-adopted.
                        do! checkChildren null
                        deadline.Token.ThrowIfCancellationRequested()
                        tracker.BeginCreate()
                        let! classification = link.CreateAsync()
                        tracker.CreateAnswered(classification)
                        do! observe "afterCreation"
                    with error ->
                        if tracker.Snapshot.Outcome = Some FluxDeployOutcome.Created then
                            tracker.Finish(FluxDeployOutcome.Unknown, $"Final writer identity could not be checked: {error.Message}")
                        else tracker.Fault(error.Message)
                }
            try
                let! winner = Task.WhenAny(work :> Task, Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token))
                if Object.ReferenceEquals(winner, work) then do! work
                else
                    deadline.Cancel()
                    close()
                    let! settled = Task.WhenAny(work :> Task, Task.Delay(TimeSpan.FromSeconds(10.0)))
                    tracker.Interrupted((if cancellationToken.IsCancellationRequested then "the operation was cancelled" else "the deadline passed"), Object.ReferenceEquals(settled, work))
            finally
                close()
                deadline.Cancel()
            // Dispose requests SDK receiver cancellation. Public connection state must also confirm closure;
            // otherwise report the residual uncertainty before the guard can release its lock.
            let closureDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10.0)
            let isClosed () =
                match closingConnection with
                | None -> true
                | Some link -> try link.IsClosed with _ -> false
            while not (isClosed()) && DateTime.UtcNow < closureDeadline do
                do! Task.Delay(10)
            if not (isClosed()) then tracker.Interrupted("connection closure could not be confirmed; residual SDK work may remain", false)
            let progress = tracker.Snapshot
            let result = FluxDeployExecution(defaultArg progress.Outcome FluxDeployOutcome.Unknown, progress.Stage, progress.Send,
                             progress.Removal, request.ExpectedModuleName, progress.RemovedSlotId, rootId, progress.NewRootSlotId,
                             [||], progress.BatchFailures, ports, progress.Error, "", "")
            return result.WithWriterIdentityEvidence(request.ExpectedWriterIdentity, lock observationGate (fun () -> observations.ToArray()))
        }
