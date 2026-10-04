using RLoop.Core;

namespace RLoop.Tests;

// ROADMAP-9 unit 3b: the output-writer check before the deployer (apply's F1) and the binding readback after it
// (the Workbench binding table, p0 report 1-5). Expected values come from those rules and the task contract.
public sealed partial class FluxDeployGuardTests
{
    private const string InputCarrierType = "[FrooxEngine]FrooxEngine.ProtoFlux.GlobalReference<[FrooxEngine]FrooxEngine.IValue<float>>";
    private const string OutputCarrierType = "[FrooxEngine]FrooxEngine.FieldDriveBase<float>+Proxy";
    private const string FieldTarget = "[FrooxEngine]FrooxEngine.IField<float>";
    private const string ValueTarget = "[FrooxEngine]FrooxEngine.IValue<float>";

    [Theory]
    [InlineData("type")]
    [InlineData("modifier")]
    [InlineData("unknown")]
    [InlineData("duplicateSame")]
    [InlineData("missing")]
    public async Task PreparedBindingEvidenceRefusesBeforeLockAndKeepsTheHealthyRoot(string fault)
    {
        var old = await DeployedOnce();
        var ports = _deployer.Preparation.Ports!;
        var speed = ports.Single(port => port.Name == "Speed");
        var changed = fault switch
        {
            "type" => speed with { ComponentTypes = ["GlobalReference<IValue<int>>"], ExpectedCarrierType = "GlobalReference<IValue<int>>" },
            "modifier" => speed with { Modifier = "element", SlotName = "Input:[Elem]Speed" },
            "unknown" => speed with { ExpectedCarrierType = null },
            "duplicateSame" => speed with { ComponentTypes = [InputCarrierType, InputCarrierType] },
            "missing" => speed with { ComponentTypes = [] },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        _deployer.Preparation = _deployer.Preparation with { Ports = [changed, ports.Single(port => port.Name == "Angle")] };
        var request = Request(hash: "HASH-2") with
        { DeclaredPorts = [new("Speed", "source", "float", null), new("Angle", "drive", "float", null)] };
        // A busy URL lease would stop any later lock acquisition; this mismatch must be decided before that point.
        using var busy = SessionWriteLock.Acquire(_world.Url, LockDirectory);

        var error = await StopsWithoutWriting("FLUX_BINDING_PORTS_MISMATCH", ExitCodes.ValidationFailed, request);

        Assert.Equal(("none", "none", "preparedPortEvidenceMismatch"),
            (error.Context["worldWrites"], error.Context["pending"], error.Context["reason"]));
        Assert.Equal(old, State().Modules["main"].RootSlotId);
        Assert.Empty(State().Pending);
    }

    /// <summary>The ID the fake deployer sends for a port Slot of the module rooted at <paramref name="root"/>.</summary>
    private static string PortSlotId(string root, string port) => $"{root}_Port_{port}";

    /// <summary>A port carrier: one reference member (<c>Reference</c> or <c>Drive</c>) pointing at <paramref name="target"/>.</summary>
    private static ComponentSummary Carrier(string slotId, string type, string member, string? target, string targetType) =>
        new(slotId + "_Carrier", type, new Dictionary<string, MemberValue>
        {
            [member] = new("reference", slotId + "_Carrier_" + member, null, null, target, targetType)
        });

    /// <summary>A component that may write <paramref name="target"/>: a reference to it as <c>IField&lt;…&gt;</c>.</summary>
    private static ComponentSummary Driver(string id, string target, string targetType = FieldTarget) =>
        new(id, "[FrooxEngine]FrooxEngine.ValueCopy<float>", new Dictionary<string, MemberValue>
        {
            ["Source"] = new("reference", id + "_Source", null, null, null, ValueTarget),
            ["Target"] = new("reference", id + "_Target", null, null, target, targetType)
        });

    /// <summary>The component that holds <paramref name="fieldId"/> as its <c>Value</c> member.</summary>
    private static ComponentSummary FieldOwner(string id, string fieldId) =>
        new(id, "[FrooxEngine]FrooxEngine.ValueField<float>", new Dictionary<string, MemberValue>
        {
            ["Value"] = new("field", fieldId, "float")
        });

    private FluxDeployGuardRequest RequestWith(IReadOnlyDictionary<string, string>? inputs, IReadOnlyDictionary<string, string>? outputs,
        IReadOnlyList<FluxDeployBindingRecord>? bindings = null) =>
        Request() with { InputMap = inputs, OutputMap = outputs, Bindings = bindings };

    /// <summary>The deployer creates the module as usual; then <paramref name="change"/> alters the world with the new root's ID.</summary>
    private void AfterCreation(Action<string> change) =>
        _deployer.OnExecute = async (request, token) =>
        {
            var result = await _deployer.Replace(request, token);
            change(result.NewRootSlotId!);
            return result;
        };

    private void ReplaceCarrier(string root, string port, Func<ComponentSummary, ComponentSummary?> change)
    {
        var components = _world.Components(PortSlotId(root, port));
        var changed = components.Select(change).OfType<ComponentSummary>().ToList();
        components.Clear();
        components.AddRange(changed);
    }

    private static ComponentSummary WithMember(ComponentSummary carrier, string member, MemberValue? value)
    {
        var members = new Dictionary<string, MemberValue>(carrier.Members!);
        if (value is null) members.Remove(member); else members[member] = value;
        return carrier with { Members = members };
    }

    private static IReadOnlyList<FluxDeployBindingReadback> Evidence(RLoopException e) =>
        Assert.IsAssignableFrom<IReadOnlyList<FluxDeployBindingReadback>>(e.Context["bindings"]);

    // ---- Binding readback: verified ------------------------------------------------------------------------

    [Fact]
    public async Task EveryBindingIsReadBackFromTheExactPortSlotTheDeployerSent()
    {
        var result = await Deploy();

        Assert.Equal(FluxDeployCheckStatus.Verified, result.BindingReadback);
        Assert.Collection(result.BindingEvidence,
            speed =>
            {
                Assert.Equal(("Speed", "source", "Reso_Field1", "verified"), (speed.Port, speed.Mode, speed.TargetId, speed.Status));
                Assert.Null(speed.Code);
                Assert.Equal(("Input:Speed", PortSlotId("Flux_1", "Speed"), true), (speed.PortSlotName, speed.PortSlotId, speed.PortSlotIdMatchesSent));
                Assert.Equal((InputCarrierType, InputCarrierType, "Reso_Field1"), (speed.ComponentType, speed.ExpectedCarrierType, speed.ObservedTargetId));
                Assert.Equal(FluxDeployTargetExistence.NotObserved, speed.TargetExistence);
            },
            angle =>
            {
                Assert.Equal(("Angle", "drive", "Reso_Field2", "verified"), (angle.Port, angle.Mode, angle.TargetId, angle.Status));
                Assert.Equal(("Output:Angle", OutputCarrierType, "Reso_Field2"), (angle.PortSlotName, angle.ComponentType, angle.ObservedTargetId));
                Assert.Equal(FluxDeployTargetExistence.NotObserved, angle.TargetExistence); // its owner is outside the observed range
            });
        Assert.Contains("Flux_1", _world.ComponentDataReads); // the module root was read with its component data
    }

    [Fact]
    public async Task NoBindingIsVerifiedWithZeroBindingsNotNotPerformed()
    {
        var result = await Deploy(RequestWith(null, null));

        Assert.Equal(FluxDeployCheckStatus.NoBindings, result.BindingReadback);
        Assert.NotEqual(FluxDeployCheckStatus.NotPerformed, result.BindingReadback);
        Assert.Empty(result.BindingEvidence);
        Assert.Equal(FluxDeployCheckStatus.NoOutputs, result.Preconditions.WriterCheck);
        Assert.Empty(_world.ComponentDataReads); // neither check read anything
        Assert.Empty(State().Modules["main"].Bindings);
        Assert.Empty(State().Pending);
    }

    [Fact]
    public async Task OutputTargetWhoseOwnerWasObservedIsReadAgainAndStillExists()
    {
        _world.AddComponent(Unrelated, FieldOwner("Reso_Owner", "Reso_Field2"));

        var result = await Deploy();

        Assert.Equal(FluxDeployCheckStatus.Verified, result.BindingReadback);
        Assert.Equal(FluxDeployTargetExistence.Exists, result.BindingEvidence.Single(entry => entry.Port == "Angle").TargetExistence);
        Assert.Equal(FluxDeployTargetExistence.NotObserved, result.BindingEvidence.Single(entry => entry.Port == "Speed").TargetExistence);
        Assert.Equal("Reso_Owner", result.Preconditions.WriterObservation!.TargetOwners["Reso_Field2"]);
        Assert.Contains(Unrelated, _world.ComponentDataReads);
    }

    // ---- Binding readback: failures and unknowns stay pending ----------------------------------------------

    public static TheoryData<string, string, string, string, string> BindingFaults => new()
    {
        // case, failing port, code, reason, status
        { "portSlotMissing", "Speed", "FLUX_BINDING_PORT_SLOT_MISSING", "portSlotMissing", "failed" },
        { "portSlotDuplicate", "Speed", "FLUX_BINDING_PORT_SLOT_DUPLICATE", "portSlotDuplicate", "failed" },
        { "portSlotNotTheSentOne", "Angle", "FLUX_BINDING_PORT_SLOT_MISSING", "sentPortSlotIdMismatch", "failed" },
        { "portNotSent", "Speed", "FLUX_BINDING_PORT_SLOT_MISSING", "portNotInSentOperations", "failed" },
        { "carrierMissing", "Angle", "FLUX_BINDING_CARRIER_MISSING", "carrierMissing", "failed" },
        { "carrierMemberMissing", "Speed", "FLUX_BINDING_CARRIER_MISSING", "carrierMemberMissing", "failed" },
        { "carrierTypeMismatch", "Speed", "FLUX_BINDING_CARRIER_TYPE_MISMATCH", "carrierTypeMismatch", "failed" },
        { "targetUnbound", "Angle", "FLUX_BINDING_TARGET_UNBOUND", "targetUnbound", "failed" },
        { "targetMismatch", "Speed", "FLUX_BINDING_TARGET_MISMATCH", "targetMismatch", "failed" },
        { "memberNotAReference", "Angle", "FLUX_BINDING_MEMBER_UNREAD", "memberUnread", "unknown" },
        { "componentsUnread", "Speed", "FLUX_BINDING_MEMBER_UNREAD", "componentsUnread", "unknown" },
        { "portsNotReported", "Speed", "FLUX_BINDING_EXPECTATION_UNKNOWN", "portsNotReported", "unknown" },
        { "carrierTypeNotEstablished", "Angle", "FLUX_BINDING_EXPECTATION_UNKNOWN", "carrierTypeNotEstablished", "unknown" },
        { "portSlotIdNotReported", "Speed", "FLUX_BINDING_EXPECTATION_UNKNOWN", "portSlotIdNotReported", "unknown" }
    };

    [Theory]
    [MemberData(nameof(BindingFaults))]
    public async Task BindingThatDoesNotReadBackAsRequestedIsLeftPendingUntilDiscarded(string fault, string port, string code, string reason, string status)
    {
        var portSlot = PortSlotId("Flux_1", port);
        switch (fault)
        {
            case "portSlotMissing": AfterCreation(_ => _world.Remove(portSlot)); break;
            case "portSlotDuplicate": AfterCreation(root => _world.Add("Reso_Twin", "Input:" + port, root)); break;
            case "portSlotNotTheSentOne":
                _deployer.OnExecute = async (request, token) =>
                {
                    var created = await _deployer.Replace(request, token);
                    return created with { Ports = created.Ports!.Select(entry => entry.Name == port ? entry with { SlotId = "Flux_Other" } : entry).ToArray() };
                };
                break;
            case "portNotSent": _deployer.ModulePorts.RemoveAll(entry => entry.Name == port); break;
            case "carrierMissing": AfterCreation(root => ReplaceCarrier(root, port, _ => null)); break;
            case "carrierMemberMissing": AfterCreation(root => ReplaceCarrier(root, port, carrier => WithMember(carrier, "Reference", null))); break;
            case "carrierTypeMismatch":
                AfterCreation(root => ReplaceCarrier(root, port, carrier => carrier with { Type = InputCarrierType.Replace("float", "int") }));
                break;
            case "targetUnbound":
                AfterCreation(root => ReplaceCarrier(root, port, carrier => WithMember(carrier, "Drive", carrier.Members!["Drive"] with { TargetId = null })));
                break;
            case "targetMismatch":
                AfterCreation(root => ReplaceCarrier(root, port, carrier => WithMember(carrier, "Reference", carrier.Members!["Reference"] with { TargetId = "Reso_Wrong" })));
                break;
            case "memberNotAReference":
                AfterCreation(root => ReplaceCarrier(root, port, carrier => WithMember(carrier, "Drive", new MemberValue("field", "Reso_X", "float"))));
                break;
            case "componentsUnread":
                AfterCreation(root =>
                {
                    ReplaceCarrier(root, port, _ => null);
                    _world.AddComponent(PortSlotId(root, port), new ComponentSummary("Reso_Opaque", "[FrooxEngine]FrooxEngine.Unknown"));
                });
                break;
            case "portsNotReported": _deployer.ReportPorts = false; break;
            case "carrierTypeNotEstablished": _deployer.ReportedCarrierTypes[port] = null; break;
            case "portSlotIdNotReported":
                _deployer.OnExecute = async (request, token) =>
                {
                    var created = await _deployer.Replace(request, token);
                    return created with { Ports = created.Ports!.Select(entry => entry.Name == port ? entry with { SlotId = null } : entry).ToArray() };
                };
                break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }

        var e = await Fails();

        var pending = AssertPendingKept(e, code, FluxDeployStage.Created, FluxDeploySendStatus.Sent, "Flux_1");
        Assert.Equal(reason, e.Context["reason"]);
        Assert.Equal(status, e.Context["bindingReadback"]);
        Assert.Equal(code, pending.Reason);
        var binding = Evidence(e).First(entry => entry.Port == port && entry.Status != "verified");
        Assert.Equal((status, code, reason), (binding.Status, binding.Code, binding.Reason));
        Assert.Contains(port, Assert.IsAssignableFrom<IEnumerable<string>>(e.Context[status == "failed" ? "failedPorts" : "unknownPorts"]));
        Assert.Empty(State().Modules); // the created module is not settled
        Assert.Equal(["Flux_1"], _world.ChildrenNamed(Parent, "Main"));
        await AssertRerunBlocked();

        // Discarding adopts nothing. Once the user deletes the module by exact ID, the next deployment runs normally.
        Discard(pending);
        _deployer.OnExecute = null;
        _deployer.ReportPorts = true;
        _deployer.ReportedCarrierTypes.Clear();
        _deployer.ModulePorts.Clear();
        _deployer.ModulePorts.AddRange([("Speed", "source"), ("Angle", "drive")]);
        Assert.Equal("FLUX_MODULE_UNRECORDED_SIBLING", (await Fails()).Code);
        _world.Remove("Flux_1");
        var retry = await Deploy();
        Assert.Equal(("Flux_2", FluxDeployCheckStatus.Verified), (retry.NewRootSlotId, retry.BindingReadback));
        Assert.Empty(State().Pending);
    }

    [Fact]
    public async Task ReadAndDifferentOutranksNotReadableAndBothAreReported()
    {
        AfterCreation(root =>
        {
            ReplaceCarrier(root, "Speed", carrier => WithMember(carrier, "Reference", carrier.Members!["Reference"] with { TargetId = "Reso_Wrong" }));
            ReplaceCarrier(root, "Angle", carrier => carrier with { Members = null });
        });

        var e = await Fails();

        AssertPendingKept(e, "FLUX_BINDING_TARGET_MISMATCH", FluxDeployStage.Created, FluxDeploySendStatus.Sent, "Flux_1");
        Assert.Equal("failed", e.Context["bindingReadback"]);
        Assert.Equal(["Speed"], Assert.IsAssignableFrom<IEnumerable<string>>(e.Context["failedPorts"]));
        Assert.Equal(["Angle"], Assert.IsAssignableFrom<IEnumerable<string>>(e.Context["unknownPorts"]));
        Assert.Equal("Reso_Wrong", Evidence(e).Single(entry => entry.Port == "Speed").ObservedTargetId);
        Assert.Equal("memberUnread", Evidence(e).Single(entry => entry.Port == "Angle").Reason);
    }

    [Fact]
    public async Task StoredBindingThatWasNotSentReadsBackAsAMismatch()
    {
        var e = await Fails(RequestWith(null, new Dictionary<string, string> { ["Angle"] = "Reso_Field2" },
            [new("Angle", "drive", "Reso_Field9")]));

        AssertPendingKept(e, "FLUX_BINDING_TARGET_MISMATCH", FluxDeployStage.Created, FluxDeploySendStatus.Sent, "Flux_1");
        Assert.Equal(["verified", "failed"], Evidence(e).Select(entry => entry.Status));
    }

    [Fact]
    public async Task ModuleRootThatCannotBeReadWithItsComponentsIsLeftPending()
    {
        _world.BeforeComponentDataRead = id => { if (id == "Flux_1") throw new RLoopException("REQUEST_TIMEOUT", "slot.get timed out", ExitCodes.Timeout); };

        var e = await Fails();

        AssertPendingKept(e, "FLUX_READBACK_FAILED", FluxDeployStage.Created, FluxDeploySendStatus.Sent, "Flux_1");
        Assert.Equal("moduleUnread", e.Context["reason"]);
        await AssertRerunBlocked();
    }

    [Fact]
    public async Task ModuleChildReturnedAsReferenceOnlyIsLeftPending()
    {
        AfterCreation(root => _world.ReferenceOnlySlots.Add(PortSlotId(root, "Speed")));

        var e = await Fails();

        AssertPendingKept(e, "FLUX_READBACK_FAILED", FluxDeployStage.Created, FluxDeploySendStatus.Sent, "Flux_1");
        Assert.Equal("moduleChildrenIncomplete", e.Context["reason"]);
    }

    [Fact]
    public async Task FailedBindingOnAReplacementKeepsThePreviousRecord()
    {
        var old = await DeployedOnce();
        AfterCreation(root => ReplaceCarrier(root, "Angle", carrier => WithMember(carrier, "Drive", carrier.Members!["Drive"] with { TargetId = "Reso_Wrong" })));

        var e = await Fails(Request(hash: "HASH-2"));

        var pending = AssertPendingKept(e, "FLUX_BINDING_TARGET_MISMATCH", FluxDeployStage.Created, FluxDeploySendStatus.Sent, "Flux_2");
        Assert.Equal(old, pending.PreviousRootSlotId);
        Assert.Equal((old, "HASH-1"), (State().Modules["main"].RootSlotId, State().Modules["main"].InputHash));
        Assert.Equal(["Flux_2"], _world.ChildrenNamed(Parent, "Main")); // the previous module is already gone
        await AssertRerunBlocked();
    }

    // ---- Binding readback: target existence ----------------------------------------------------------------

    [Fact]
    public async Task OutputTargetWhoseOwnerDisappearedIsMissing()
    {
        _world.AddComponent(Unrelated, FieldOwner("Reso_Owner", "Reso_Field2"));
        AfterCreation(_ => _world.Components(Unrelated).Clear());

        var e = await Fails();

        AssertPendingKept(e, "FLUX_BINDING_TARGET_MISSING", FluxDeployStage.Created, FluxDeploySendStatus.Sent, "Flux_1");
        Assert.Equal("targetMissing", e.Context["reason"]);
        var angle = Evidence(e).Single(entry => entry.Port == "Angle");
        Assert.Equal(("failed", FluxDeployTargetExistence.Missing, "Reso_Field2"), (angle.Status, angle.TargetExistence, angle.ObservedTargetId));
        await AssertRerunBlocked();
    }

    [Fact]
    public async Task OutputTargetHeldByThePreviousModuleIsMissingAfterTheReplacement()
    {
        var old = await DeployedOnce();
        _world.AddComponent(old, FieldOwner("Reso_OldOwner", "Reso_Field2"));

        var e = await Fails();

        AssertPendingKept(e, "FLUX_BINDING_TARGET_MISSING", FluxDeployStage.Created, FluxDeploySendStatus.Sent, "Flux_2");
        Assert.Contains(old, Evidence(e).Single(entry => entry.Port == "Angle").Detail);
        Assert.Equal(old, State().Modules["main"].RootSlotId);
    }

    [Fact]
    public async Task OutputTargetWhoseOwnerCannotBeReadAgainIsUnknown()
    {
        _world.AddComponent(Unrelated, FieldOwner("Reso_Owner", "Reso_Field2"));
        _world.BeforeComponentDataRead = id => { if (id == Unrelated) throw new RLoopException("REQUEST_TIMEOUT", "slot.get timed out", ExitCodes.Timeout); };

        var e = await Fails();

        AssertPendingKept(e, "FLUX_BINDING_MEMBER_UNREAD", FluxDeployStage.Created, FluxDeploySendStatus.Sent, "Flux_1");
        Assert.Equal("targetOwnerUnread", e.Context["reason"]);
        Assert.Equal("unknown", e.Context["bindingReadback"]);
        Assert.Equal(FluxDeployTargetExistence.Unread, Evidence(e).Single(entry => entry.Port == "Angle").TargetExistence);
    }

    // ---- Output writers before the deployer (F1) -----------------------------------------------------------

    [Theory]
    [InlineData(Unrelated)]
    [InlineData(Parent)]
    public async Task AnotherWriterOfAnOutputTargetRefusesWithNothingWritten(string holder)
    {
        _world.AddComponent(holder, Driver("Reso_Driver", "Reso_Field2"));

        var e = await StopsWithoutWriting("FLUX_OUTPUT_WRITER_CONFLICT", 6);

        Assert.Equal(("none", "none", "writerDetected"), (e.Context["worldWrites"], e.Context["pending"], e.Context["reason"]));
        var hit = Assert.Single(Assert.IsAssignableFrom<IEnumerable<FluxDeployWriterHit>>(e.Context["writers"]));
        Assert.Equal(("Angle", "Reso_Field2", holder, "Reso_Driver", "Target", false),
            (hit.Port, hit.TargetId, hit.SlotId, hit.ComponentId, hit.Member, hit.UnderPreviousModule));
        Assert.Empty(_deployer.Executions);
        Assert.Null(StateText());
        using (var lease = SessionWriteLock.Acquire(_world.Url, LockDirectory)) lease.CheckPreviousState(null); // released, points nowhere new

        _world.Components(holder).Clear(); // the user removed the other driver
        Assert.Equal("Flux_1", (await Deploy()).NewRootSlotId);
    }

    [Fact]
    public async Task WriterNestedInAListIsFound()
    {
        _world.AddComponent(Unrelated, new ComponentSummary("Reso_Multi", "[FrooxEngine]FrooxEngine.MultiDriver", new Dictionary<string, MemberValue>
        {
            ["Drives"] = new("list", "Reso_List", Elements: [new("syncObject", "Reso_Elem", Members: new Dictionary<string, MemberValue>
            {
                ["Field"] = new("reference", "Reso_Ref", null, null, "Reso_Field2", FieldTarget)
            })])
        }));

        var e = await StopsWithoutWriting("FLUX_OUTPUT_WRITER_CONFLICT", 6);

        Assert.Equal("Drives", Assert.Single(Assert.IsAssignableFrom<IEnumerable<FluxDeployWriterHit>>(e.Context["writers"])).Member);
    }

    [Fact]
    public async Task WriterOnTheRecordedPreviousModuleIsAllowed()
    {
        var old = await DeployedOnce();
        _world.AddComponent(old, Driver("Reso_OldDriver", "Reso_Field2"));

        var result = await Deploy();

        Assert.Equal(FluxDeployCheckStatus.ObservedRangeClear, result.Preconditions.WriterCheck);
        var allowed = Assert.Single(result.Preconditions.WriterObservation!.AllowedWriters);
        Assert.Equal((old, "Reso_OldDriver", true), (allowed.SlotId, allowed.ComponentId, allowed.UnderPreviousModule));
        Assert.Equal("Flux_2", result.NewRootSlotId);
    }

    [Fact]
    public async Task OwnerOfTheTargetAndReadOnlyReferencesAreNotWriters()
    {
        _world.AddComponent(Unrelated, FieldOwner("Reso_Owner", "Reso_Field2"));
        _world.AddComponent(Unrelated, Driver("Reso_Reader", "Reso_Field2", ValueTarget)); // IValue<…>: reads, does not write
        _world.AddComponent(Unrelated, Driver("Reso_InputWriter", "Reso_Field1")); // writes the input's target, which is not an output

        var result = await Deploy();

        Assert.Equal(FluxDeployCheckStatus.ObservedRangeClear, result.Preconditions.WriterCheck);
        Assert.Empty(result.Preconditions.WriterObservation!.AllowedWriters);
        Assert.Equal(["Reso_Field2"], result.Preconditions.WriterObservation.Targets);
    }

    [Fact]
    public async Task WriterOutsideTheObservedRangeIsNotSearchedAndRecordedAsUnknown()
    {
        _world.Add("Reso_Deep", "Deep", Unrelated);
        _world.AddComponent("Reso_Deep", Driver("Reso_DeepDriver", "Reso_Field2")); // a grandchild of the parent

        var result = await Deploy();

        Assert.Equal(FluxDeployCheckStatus.ObservedRangeClear, result.Preconditions.WriterCheck);
        var observation = result.Preconditions.WriterObservation!;
        Assert.Equal(FluxDeployCheckStatus.Unknown, observation.OutsideObservedRange);
        Assert.Equal([Parent, Unrelated], observation.ObservedSlotIds);
        Assert.DoesNotContain("Reso_Deep", _world.ComponentDataReads);
        Assert.DoesNotContain("Root", _world.ComponentDataReads); // the world is not searched
    }

    [Fact]
    public async Task ParentThatCannotBeReadWithComponentsIsUnknownAndDoesNotStop()
    {
        _world.BeforeComponentDataRead = id => { if (id == Parent) throw new RLoopException("REQUEST_TIMEOUT", "slot.get timed out", ExitCodes.Timeout); };

        var result = await Deploy();

        Assert.Equal(FluxDeployCheckStatus.Unknown, result.Preconditions.WriterCheck);
        Assert.Contains("read failed", Assert.Single(result.Preconditions.WriterObservation!.Unobserved));
        Assert.Equal("Flux_1", State().Modules["main"].RootSlotId);
    }

    [Fact]
    public async Task ComponentWhoseMembersWereNotReadIsUnknownAndDoesNotStop()
    {
        _world.AddComponent(Unrelated, new ComponentSummary("Reso_Opaque", "[FrooxEngine]FrooxEngine.Unknown"));

        var result = await Deploy();

        Assert.Equal(FluxDeployCheckStatus.Unknown, result.Preconditions.WriterCheck);
        Assert.Contains("Reso_Opaque", Assert.Single(result.Preconditions.WriterObservation!.Unobserved));
        Assert.Equal(FluxDeployCheckStatus.Verified, result.BindingReadback);
    }

    // ---- Binding keys against the compiled ports, before the lock (unit 5) ----------------------------------

    /// <summary>The stop wrote nothing, held no lock and left no state: it happened right after the compile.</summary>
    private async Task<RLoopException> PortsMismatch(FluxDeployGuardRequest request)
    {
        var e = await StopsWithoutWriting("FLUX_BINDING_PORTS_MISMATCH", 6, request);
        Assert.Equal(("none", "none"), (e.Context["worldWrites"], e.Context["pending"]));
        Assert.Null(StateText());
        Assert.Empty(Directory.Exists(LockDirectory) ? Directory.GetFiles(LockDirectory) : []);
        Assert.Empty(_world.ComponentDataReads); // not even the writer check ran
        Assert.Equal(FluxDeployBindingPortsStatus.Mismatch, Assert.IsType<FluxDeployBindingPortsCheck>(e.Context["bindingPorts"]).Status);
        return e;
    }

    [Fact]
    public async Task BindingKeysThatNameEveryCompiledPortAreMatched()
    {
        var result = await Deploy(Request() with { RequireAllPortsBound = true });

        var check = result.Preconditions.BindingPorts!;
        Assert.Equal(FluxDeployBindingPortsStatus.Matched, check.Status);
        Assert.Equal([new FluxDeployPortKey("Speed", "source"), new FluxDeployPortKey("Angle", "drive")], check.CompiledPorts);
        Assert.Equal(check.CompiledPorts, check.BoundPorts);
        Assert.Empty(check.UnboundPorts);
    }

    [Fact]
    public async Task BindingKeyWithoutACompiledPortStopsBeforeAnythingIsWritten()
    {
        var e = await PortsMismatch(RequestWith(new Dictionary<string, string> { ["Speed"] = "Reso_Field1", ["Sped"] = "Reso_Field3" },
            new Dictionary<string, string> { ["Angle"] = "Reso_Field2" }));

        Assert.Equal([new FluxDeployPortKey("Sped", "source")], Assert.IsAssignableFrom<IEnumerable<FluxDeployPortKey>>(e.Context["unknownKeys"]));
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<FluxDeployPortKey>>(e.Context["modeMismatches"]));
        Assert.Contains("Sped", e.Message);
    }

    [Fact]
    public async Task BindingKeyOfTheOtherModeStopsBeforeAnythingIsWritten()
    {
        // Angle is an output of the module, but the request binds it as an input.
        var e = await PortsMismatch(RequestWith(new Dictionary<string, string> { ["Speed"] = "Reso_Field1", ["Angle"] = "Reso_Field2" }, null));

        Assert.Equal([new FluxDeployPortKey("Angle", "source")], Assert.IsAssignableFrom<IEnumerable<FluxDeployPortKey>>(e.Context["modeMismatches"]));
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<FluxDeployPortKey>>(e.Context["unknownKeys"]));
    }

    [Fact]
    public async Task CompiledPortWithoutABindingStopsWhenEveryPortMustBeBound()
    {
        var e = await PortsMismatch(RequestWith(new Dictionary<string, string> { ["Speed"] = "Reso_Field1" }, null) with { RequireAllPortsBound = true });

        Assert.Equal([new FluxDeployPortKey("Angle", "drive")], Assert.IsAssignableFrom<IEnumerable<FluxDeployPortKey>>(e.Context["unboundPorts"]));
        Assert.Equal(true, e.Context["requireAllPortsBound"]);
    }

    [Fact]
    public async Task CompiledPortWithoutABindingIsAWarningWhenNotEveryPortMustBeBound()
    {
        var result = await Deploy(RequestWith(new Dictionary<string, string> { ["Speed"] = "Reso_Field1" }, null));

        var check = result.Preconditions.BindingPorts!;
        Assert.Equal(FluxDeployBindingPortsStatus.UnboundPortsAllowed, check.Status);
        Assert.Equal([new FluxDeployPortKey("Angle", "drive")], check.UnboundPorts);
        Assert.Contains("Angle", check.Detail);
        Assert.Equal("Flux_1", result.NewRootSlotId);
    }

    [Fact]
    public async Task UnknownPreparationPortsRefuseBindingsBeforeReplacingAHealthyRoot()
    {
        await Deploy();
        var before = StateText();
        var world = _world.Snapshot();
        var executions = _deployer.Executions.Count;
        _deployer.Preparation = FakeDeployer.Prepared("Main") with { Ports = null };

        // C03: post-write readback cannot replace the contract's pre-send port/map check.
        var error = await Assert.ThrowsAsync<RLoopException>(() => Deploy(Request() with { RequireAllPortsBound = true }));
        Assert.Equal("FLUX_BINDING_PORTS_MISMATCH", error.Code);
        Assert.Equal(("none", "none"), (error.Context["worldWrites"], error.Context["pending"]));
        Assert.Equal(executions, _deployer.Executions.Count);
        Assert.Equal(before, StateText());
        Assert.Equal(world, _world.Snapshot());
    }

    [Fact]
    public async Task ModuleWithoutPortsAndRequestWithoutBindingsIsMatched()
    {
        _deployer.Preparation = FakeDeployer.Prepared("Main") with { Ports = [] };
        _deployer.ModulePorts.Clear();

        var result = await Deploy(RequestWith(null, null) with { RequireAllPortsBound = true });

        Assert.Equal(FluxDeployBindingPortsStatus.Matched, result.Preconditions.BindingPorts!.Status);
        Assert.Empty(result.Preconditions.BindingPorts.CompiledPorts!);
    }

    [Fact]
    public async Task RequestWithoutOutputsDoesNotReadForWriters()
    {
        _world.AddComponent(Unrelated, Driver("Reso_Driver", "Reso_Field1")); // would be a writer if Reso_Field1 were an output

        var result = await Deploy(RequestWith(new Dictionary<string, string> { ["Speed"] = "Reso_Field1" }, null));

        Assert.Equal(FluxDeployCheckStatus.NoOutputs, result.Preconditions.WriterCheck);
        Assert.Equal("notApplicable", result.Preconditions.WriterObservation!.OutsideObservedRange);
        Assert.DoesNotContain(Parent, _world.ComponentDataReads);
        Assert.Equal(FluxDeployCheckStatus.Verified, result.BindingReadback);
    }
}
