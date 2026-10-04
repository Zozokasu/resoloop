using System.Text.Json;
using RLoop.Cli;
using RLoop.Core;
using RLoop.Flux;

namespace RLoop.Tests;

// ROADMAP-9 unit 5: the checks before a deployment. Expected values come from P9 (error diagnostics decide, warnings
// continue), the p0 report's measured Flux-SDK 1.9.0 output (section 1-3), and the task contract. No process is
// started and nothing connects anywhere: the build output is given as text, the world and the deployer are fakes.
public sealed class FluxPreDeployCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "resoloop-flux-predeploy-" + Guid.NewGuid().ToString("N"));
    private readonly FluxTestWorld _world = new();
    private readonly FluxTestDeployer _deployer;

    public FluxPreDeployCheckTests()
    {
        Directory.CreateDirectory(_root);
        _deployer = new FluxTestDeployer(_world);
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(_root);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }

    // Shapes of the measured Flux-SDK 1.9.0 output (p0 report 1-3), with shortened paths.
    private const string WarningsOnlyStdout = """
        Loading Froox Nodes...
        Added Froox nodes: 3320
        C:/p/INTERNAL.pg(0,0,0,0): warning: Unknown build profile 'p0unknown' for optimization. Defaulting to 'dev'.
        Writing output to C:\p\out\Main.pg.brson
        Packing 2 ProtoFlux nodes and 0 comments.

        Main compile elapsed time: 977 ms
        Finished compiling Main but encountered 0 errors and 1 warnings.
        """;

    private const string ErrorStdout = """
        Loading Froox Nodes...
        C:/p/Main.pg(4,1,4,1): error: Closing '}' expected here. I expected something to close the opening scope started by '{' at Main(2:7 to 2:8)
        Writing output to C:\p\out\Main.pg.brson
        Packing 2 ProtoFlux nodes and 0 comments.

        Main compile elapsed time: 759 ms
        Finished compiling Main but encountered 1 errors and 0 warnings.
        """;

    private const string CleanStdout = """
        Loading Froox Nodes...
        Packing 2 ProtoFlux nodes and 0 comments.

        Main compile elapsed time: 829 ms
        Compilation of all targets succeeded.
        """;

    // ---- Build verdict (P9), pure ---------------------------------------------------------------------------

    [Fact]
    public void WarningsOnlyBuildWithExitCodeOneSucceeds()
    {
        var verdict = FluxBuildJudgement.Judge(1, WarningsOnlyStdout, "");

        Assert.True(verdict.Success);
        Assert.Equal(FluxBuildJudgement.WarningsOnly, verdict.Basis);
        Assert.Equal((0, 1, 0), (verdict.ErrorCount, verdict.WarningCount, verdict.ReportedErrorCount));
    }

    [Fact]
    public void ErrorDiagnosticFailsEvenWithExitCodeZero()
    {
        var verdict = FluxBuildJudgement.Judge(0, ErrorStdout, "");

        Assert.False(verdict.Success);
        Assert.Equal(FluxBuildJudgement.ErrorDiagnostics, verdict.Basis);
        Assert.Equal(1, verdict.ErrorCount);
    }

    [Fact]
    public void CleanBuildSucceeds()
    {
        var verdict = FluxBuildJudgement.Judge(0, CleanStdout, "");

        Assert.True(verdict.Success);
        Assert.Equal(FluxBuildJudgement.Clean, verdict.Basis);
        Assert.Null(verdict.ReportedErrorCount);
    }

    [Theory]
    [InlineData("Loading Froox Nodes...\n", "Unhandled exception. System.IO.FileNotFoundException: FrooxEngine.dll\n", 127)]
    [InlineData("", "", 1)]
    public void NonZeroExitWithoutAnyDiagnosticFails(string stdout, string stderr, int exitCode)
    {
        var verdict = FluxBuildJudgement.Judge(exitCode, stdout, stderr);

        Assert.False(verdict.Success);
        Assert.Equal(FluxBuildJudgement.UnexplainedExitCode, verdict.Basis);
    }

    [Fact]
    public void WarningsWithNonZeroExitButNoSummaryOfZeroErrorsCannotBeJudgedASuccess()
    {
        var verdict = FluxBuildJudgement.Judge(1, "C:/p/Main.pg(1,1,1,2): warning: unused value\n", "");

        Assert.False(verdict.Success);
        Assert.Equal(FluxBuildJudgement.UnexplainedExitCode, verdict.Basis);
    }

    [Theory]
    [InlineData("Some error text the parser does not know\n\nFinished compiling Main but encountered 2 errors and 0 warnings.\n", 1, 2)]
    [InlineData("Main compile elapsed time: 331 ms\nResolving completed with 5 errors. Stopping early.\n", 1, 5)]
    [InlineData("C:/p/Main.pg(1,1,1,2): warning: w\n\nFinished compiling Main but encountered 1 errors and 1 warnings.\n", 1, 1)]
    public void ErrorsStatedOnlyByTheSummaryFail(string stdout, int exitCode, int reported)
    {
        var verdict = FluxBuildJudgement.Judge(exitCode, stdout, "");

        Assert.False(verdict.Success);
        Assert.Equal(FluxBuildJudgement.ReportedErrors, verdict.Basis);
        Assert.Equal(reported, verdict.ReportedErrorCount);
    }

    [Fact]
    public void ToolThatReportsFailureIsNeverTurnedIntoASuccess()
    {
        var verdict = FluxBuildJudgement.Of(new FluxResult(false, 0, CleanStdout, ""));

        Assert.False(verdict.Success);
        Assert.Equal(FluxBuildJudgement.ToolReportedFailure, verdict.Basis);
    }

    [Fact]
    public void FailureDescriptionUsesTheStdoutDiagnosticsNotTheEmptyStderr()
    {
        var result = new FluxResult(false, 1, ErrorStdout, "");

        var text = FluxBuildJudgement.DescribeFailure(FluxBuildJudgement.Of(result), result);

        Assert.Contains("Closing '}' expected here", text);
        Assert.Contains("Main.pg(4,1,4,1): error:", text);
    }

    [Fact]
    public void FailureDescriptionWithoutDiagnosticsCarriesStdoutAndStderr()
    {
        var result = new FluxResult(false, 127, "Loading Froox Nodes...\n", "Unhandled exception. FrooxEngine.dll\n");

        var text = FluxBuildJudgement.DescribeFailure(FluxBuildJudgement.Of(result), result);

        Assert.Contains("Loading Froox Nodes...", text);
        Assert.Contains("Unhandled exception. FrooxEngine.dll", text);
    }

    // ---- Build verdict in deploy-manifest (acceptance 8) ----------------------------------------------------

    private string Manifest(string source, string bindings = "", string module = "Main")
    {
        // Independent fixed declarations of this file's healthy fixtures, not binding-map-derived ports.
        // Explicit drift/mismatch fixtures above retain their separately supplied compile result.
        if (!_deployer.PreparedPorts.ContainsKey(module))
            _deployer.PreparedPorts[module] = source.Contains("in Count:", StringComparison.Ordinal)
                ? [FluxTestDeployer.Port("Count", "source", "[FrooxEngine]FrooxEngine.ProtoFlux.GlobalReference<[FrooxEngine]FrooxEngine.IValue<int>>")]
                : source.Contains("in Button:", StringComparison.Ordinal)
                    ? [FluxTestDeployer.Port("Button", "source", source.Contains("ButtonBase", StringComparison.Ordinal)
                        ? "[FrooxEngine]FrooxEngine.ProtoFlux.GlobalReference<[FrooxEngine]FrooxEngine.ButtonBase>"
                        : "[FrooxEngine]FrooxEngine.ProtoFlux.GlobalReference<[FrooxEngine]FrooxEngine.IButton>", "element")] : [];
        File.WriteAllText(Path.Combine(_root, "main.pg"), source);
        var manifest = Path.Combine(_root, "flux.json");
        File.WriteAllText(manifest, $$"""
            { "schemaVersion":"1", "modules":[{ "name":"main", "source":"main.pg", "module":"{{module}}"{{bindings}} }] }
            """);
        return manifest;
    }

    private FluxManifestOrchestrator Orchestrator() => new(_deployer, _deployer, _world);

    private Task<FluxManifestResult> Deploy(string manifest, Dictionary<string, FluxResolvedModuleBindings>? resolved = null) =>
        Orchestrator().DeployAsync(manifest, FluxTestWorld.Parent, new Uri(_world.Url), null, null, resolvedBindings: resolved);

    [Fact]
    public async Task ManifestBuildWithWarningsOnlyContinuesAndReportsTheWarnings()
    {
        var manifest = Manifest("module Main where { 1->display }");
        _deployer.BuildResult = new FluxResult(false, 1, WarningsOnlyStdout, "");

        var result = await Deploy(manifest);

        Assert.True(result.Success);
        var module = Assert.Single(result.Modules);
        Assert.True(module.Deployed);
        Assert.True(module.BuildSucceeded);
        Assert.Equal(FluxBuildJudgement.WarningsOnly, module.Build!.Basis);
        Assert.Contains(module.BuildDiagnostics!, diagnostic => diagnostic.Severity == "warning" && diagnostic.Message.Contains("Unknown build profile"));
        Assert.Single(_deployer.Executions);
    }

    [Fact]
    public async Task ManifestBuildWithAnErrorStopsEvenWithExitCodeZeroAndReportsTheStdoutDiagnostic()
    {
        var manifest = Manifest("module Main where { 1->display }");
        _deployer.BuildResult = new FluxResult(true, 0, ErrorStdout, "");

        var result = await Deploy(manifest);

        Assert.False(result.Success);
        var module = Assert.Single(result.Modules);
        Assert.False(module.BuildSucceeded);
        Assert.False(module.Deployed);
        Assert.Equal(FluxBuildJudgement.ErrorDiagnostics, module.Build!.Basis);
        Assert.Contains("Closing '}' expected here", module.Error);
        Assert.Contains(module.BuildDiagnostics!, diagnostic => diagnostic.Severity == "error");
        Assert.Empty(_deployer.Preparations);
        Assert.Empty(_deployer.Executions);
    }

    [Fact]
    public async Task ManifestBuildFailureWithEmptyStderrReportsTheStdoutDiagnostic()
    {
        var manifest = Manifest("module Main where { 1->display }");
        _deployer.BuildResult = new FluxResult(false, 1, ErrorStdout, "");

        var module = Assert.Single((await Deploy(manifest)).Modules);

        Assert.False(string.IsNullOrWhiteSpace(module.Error));
        Assert.Contains("Closing '}' expected here", module.Error);
    }

    [Fact]
    public async Task ManifestBuildThatExitsNonZeroWithoutDiagnosticsStopsAndReportsStdoutAndStderr()
    {
        var manifest = Manifest("module Main where { 1->display }");
        _deployer.BuildResult = new FluxResult(false, 127, "Loading Froox Nodes...\n", "Unhandled exception. FrooxEngine.dll\n");

        var module = Assert.Single((await Deploy(manifest)).Modules);

        Assert.False(module.Deployed);
        Assert.Equal(FluxBuildJudgement.UnexplainedExitCode, module.Build!.Basis);
        Assert.Contains("Loading Froox Nodes...", module.Error);
        Assert.Contains("Unhandled exception. FrooxEngine.dll", module.Error);
        Assert.Empty(_deployer.Preparations);
    }

    // ---- Build verdict in the CLI ---------------------------------------------------------------------------

    private async Task<(int Exit, JsonElement Output)> Cli(params string[] args)
    {
        var report = Path.Combine(_root, "report-" + Guid.NewGuid().ToString("N") + ".json");
        var exit = await Program.RunAsync([.. args, "--json", "--report", report], null, null, (_deployer, _deployer));
        using var document = JsonDocument.Parse(File.ReadLines(report).Last());
        return (exit, document.RootElement.Clone());
    }

    [Fact]
    public async Task CliBuildWithWarningsOnlySucceedsWithItsVerdict()
    {
        _deployer.BuildResult = new FluxResult(false, 1, WarningsOnlyStdout, "");

        var (exit, output) = await Cli("flux", "build", Path.Combine(_root, "main.pg"));

        Assert.Equal(0, exit);
        var data = output.GetProperty("data");
        Assert.True(data.GetProperty("success").GetBoolean());
        Assert.Equal(1, data.GetProperty("exitCode").GetInt32());
        Assert.Equal(FluxBuildJudgement.WarningsOnly, data.GetProperty("verdict").GetProperty("basis").GetString());
    }

    [Fact]
    public async Task CliCheckWithAnErrorDiagnosticFailsEvenWithExitCodeZero()
    {
        _deployer.BuildResult = new FluxResult(true, 0, ErrorStdout, "");

        var (exit, output) = await Cli("flux", "check", Path.Combine(_root, "main.pg"));

        Assert.Equal(ExitCodes.ExternalToolFailed, exit);
        var error = output.GetProperty("error");
        Assert.Equal("FLUX_COMMAND_FAILED", error.GetProperty("code").GetString());
        Assert.Equal(FluxBuildJudgement.ErrorDiagnostics, error.GetProperty("context").GetProperty("verdict").GetProperty("basis").GetString());
    }

    // ---- Port declarations in the module source -------------------------------------------------------------

    [Fact]
    public void PortLikeLinesThatCannotBeReadAreReportedAndOtherLinesAreIgnored()
    {
        var reading = FluxModuleSignature.Read("""
            module Main
            // in Commented: int
            /// out AlsoCommented: bool

            in	Tabbed: int
            in Count: int element // trailing comment
            in Missing
            out : bool
            in Two Words: int
            in Modifier: global
            inputs are described below
            outer: not a port
            where {
                in NotHeader
            }
            """);

        Assert.Equal([("Tabbed", "source", "int", (string?)null), ("Count", "source", "int", "element")],
            reading.Ports.Select(port => (port.Name, port.Direction, port.Type, port.Modifier)));
        Assert.Equal([(7, "noTypeSeparator"), (8, "emptyName"), (9, "invalidName"), (10, "emptyType")],
            reading.Unparsed.Select(issue => (issue.Line, issue.Reason)));
        Assert.Equal("in Missing", reading.Unparsed[0].Text);
    }

    [Fact]
    public void HeaderEndsAtWhereAlsoOnTheModuleLine()
    {
        var reading = FluxModuleSignature.Read("module Main where {\n    in Count\n    out Value: int\n}\n");

        Assert.Empty(reading.Ports);
        Assert.Empty(reading.Unparsed);
    }

    [Fact]
    public void ParseFailsWithFluxPortParseFailed()
    {
        var e = Assert.Throws<RLoopException>(() => FluxModuleSignature.Parse("module Main\nin Count int\nwhere { }", "C:/p/main.pg"));

        Assert.Equal("FLUX_PORT_PARSE_FAILED", e.Code);
        Assert.Equal(ExitCodes.ValidationFailed, e.ExitCode);
        Assert.Equal("C:/p/main.pg", e.Context["source"]);
        var issue = Assert.Single(Assert.IsAssignableFrom<IEnumerable<FluxModulePortParseIssue>>(e.Context["lines"]));
        Assert.Equal((2, "in Count int", "noTypeSeparator"), (issue.Line, issue.Text, issue.Reason));
    }

    [Fact]
    public async Task UnreadablePortLineStopsValidationAndDeployBeforeTheBuild()
    {
        var manifest = Manifest("module Main\nin Count int\nwhere { 1->display }");

        Assert.Equal("FLUX_PORT_PARSE_FAILED", Assert.Throws<RLoopException>(() => FluxManifestOrchestrator.ValidateManifest(manifest)).Code);
        var e = await Assert.ThrowsAsync<RLoopException>(() => Deploy(manifest));

        Assert.Equal("FLUX_PORT_PARSE_FAILED", e.Code);
        Assert.Empty(_deployer.BuildRequests);
        Assert.Empty(_deployer.Executions);
    }

    // ---- Binding target types -------------------------------------------------------------------------------

    [Theory]
    [InlineData("int", "Int32")]
    [InlineData("System.Int32", "Int32")]
    [InlineData("[mscorlib]System.Single", "Single")]
    [InlineData("float3", "float3")]
    [InlineData("[FrooxEngine]Elements.Core.float3", "float3")]
    [InlineData("[FrooxEngine]FrooxEngine.Slot", "Slot")]
    [InlineData("[FrooxEngine]FrooxEngine.IValue<float>", "IValue<Single>")]
    [InlineData("[FrooxEngine]FrooxEngine.IField<[FrooxEngine]Elements.Core.float3>", "IField<float3>")]
    [InlineData("float?", "Nullable<Single>")]
    [InlineData("System.Nullable`1[[System.Single, System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]]", "Nullable<Single>")]
    [InlineData("[FrooxEngine]FrooxEngine.FieldDriveBase<float>+Proxy", "FieldDriveBase<Single>+Proxy")]
    public void TypeNamesAreNormalizedWithTheirGenericArguments(string type, string expected) =>
        Assert.Equal(expected, FluxTypeNames.Normalize(type));

    private const string CountSource = "module Main\nin Count: int\nwhere { Count->display }";
    private const string CountBinding = """, "bindings":{ "Count":{ "target":"$member:count.Value", "mode":"source" } }""";

    [Theory]
    [InlineData("type")]
    [InlineData("name")]
    [InlineData("mode")]
    [InlineData("modifier")]
    [InlineData("unknownCarrier")]
    [InlineData("missingCarrier")]
    [InlineData("duplicateSameCarrier")]
    [InlineData("duplicateDifferentCarrier")]
    [InlineData("malformedCarrier")]
    [InlineData("mutable")]
    public async Task SourceTargetPreflightAndPreparationDriftCannotReplaceAHealthyRoot(string fault)
    {
        const string carrier = "[FrooxEngine]FrooxEngine.ProtoFlux.GlobalReference<[FrooxEngine]FrooxEngine.IValue<float>>";
        var healthy = FluxTestDeployer.Port("Count", "source", carrier);
        _deployer.PreparedPorts["Main"] = [healthy];
        var manifest = Manifest("module Main\nin Count: float\nwhere { Count->display }", CountBinding);
        var resolved = Resolved("Count", "source", "$member:count.Value", "member", "System.Single");
        var first = await Deploy(manifest, resolved);
        Assert.Equal("Flux_1", Assert.Single(first.Modules).ModuleSlotIdAfter);
        var statePath = Path.Combine(_root, ".resoloop", "flux-state", "flux.json");
        var state = File.ReadAllBytes(statePath);
        var world = _world.Snapshot();
        var executions = _deployer.Executions.Count;
        _deployer.PreparedPorts["Main"] = [fault switch
        {
            "type" => FluxTestDeployer.Port("Count", "source", "GlobalReference<IValue<int>>"),
            "name" => healthy with { Name = "Changed" },
            "mode" => FluxTestDeployer.Port("Count", "drive", "FieldDriveBase<float>+Proxy"),
            "modifier" => FluxTestDeployer.Port("Count", "source", "GlobalReference<Sync<float>>", "element"),
            "unknownCarrier" => healthy with { ExpectedCarrierType = null },
            "missingCarrier" => healthy with { ComponentTypes = [] },
            "duplicateSameCarrier" => healthy with { ComponentTypes = [carrier, carrier] },
            "duplicateDifferentCarrier" => healthy with { ComponentTypes = [carrier, "GlobalReference<IValue<int>>"] },
            "malformedCarrier" => FluxTestDeployer.Port("Count", "source", "GlobalReference<IValue<List<>>>"),
            "mutable" => FluxTestDeployer.Port("Count", "source", "GlobalReference<Sync<float>>"),
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        }];
        // Changing the authored body requires an update; source and observed target remain float.
        File.WriteAllText(Path.Combine(_root, "main.pg"), fault == "mutable"
            ? "module Main\nin Count: float mutable\nwhere { Count->display }"
            : "module Main\nin Count: float\nwhere { Count+1->display }");

        var error = await Assert.ThrowsAsync<RLoopException>(() => Deploy(manifest, resolved));

        Assert.Equal("FLUX_BINDING_PORTS_MISMATCH", error.Code);
        Assert.Equal(("none", "none"), (error.Context["worldWrites"], error.Context["pending"]));
        Assert.Equal(executions, _deployer.Executions.Count);
        Assert.Equal(world, _world.Snapshot());
        Assert.Equal(state, File.ReadAllBytes(statePath));
        Assert.Empty(FluxDeployStateStore.Load(statePath).Pending);
        Assert.Equal(0, _world.WriteCalls);
    }

    [Fact]
    public async Task EquivalentPreparedTypeJoinsTheSourceThatWasCheckedAgainstTheTarget()
    {
        _deployer.PreparedPorts["Main"] = [FluxTestDeployer.Port("Count", "source",
            "[FrooxEngine]FrooxEngine.ProtoFlux.GlobalReference<[FrooxEngine]FrooxEngine.IValue<System.Single>>")];
        var manifest = Manifest("module Main\nin Count: float\nwhere { Count->display }", CountBinding);

        var result = await Deploy(manifest, Resolved("Count", "source", "$member:count.Value", "member", "System.Single"));

        Assert.True(result.Success);
        var request = Assert.Single(_deployer.Executions);
        Assert.Equal("[FrooxEngine]FrooxEngine.ProtoFlux.GlobalReference<[FrooxEngine]FrooxEngine.IValue<System.Single>>",
            Assert.Single(request.ExpectedPorts!).ExpectedCarrierType);
        Assert.Equal(FluxBindingTypeStatus.Matched, Assert.Single(Assert.Single(result.Modules).BindingTypes!).Status);
    }

    private static Dictionary<string, FluxResolvedModuleBindings> Resolved(string name, string mode, string selector, string kind, string? type) =>
        new() { ["main"] = new([new FluxResolvedBinding(name, mode, selector, "Reso_Target", kind, type)]) };

    [Fact]
    public async Task MemberTargetOfThePortTypeIsMatched()
    {
        var result = await Deploy(Manifest(CountSource, CountBinding), Resolved("Count", "source", "$member:count.Value", "member", "System.Int32"));

        var check = Assert.Single(Assert.Single(result.Modules).BindingTypes!);
        Assert.Equal((FluxBindingTypeStatus.Matched, "Int32", "Int32"), (check.Status, check.ExpectedType, check.ActualType));
        Assert.True(Assert.Single(result.Modules).Deployed);
    }

    [Fact]
    public async Task MemberTargetOfAnotherTypeStopsBeforeTheBuild()
    {
        var e = await Assert.ThrowsAsync<RLoopException>(() => Deploy(Manifest(CountSource, CountBinding),
            Resolved("Count", "source", "$member:count.Value", "member", "System.String")));

        Assert.Equal("FLUX_BINDING_TYPE_MISMATCH", e.Code);
        Assert.Equal("Int32", e.Context["expectedType"]);
        Assert.Equal("String", e.Context["actualType"]);
        Assert.Empty(_deployer.BuildRequests);
        Assert.Empty(_deployer.Executions);
    }

    [Fact]
    public async Task MemberTargetWhoseTypeWasNotReadIsUnknownAndContinues()
    {
        var result = await Deploy(Manifest(CountSource, CountBinding), Resolved("Count", "source", "$member:count.Value", "member", null));

        var module = Assert.Single(result.Modules);
        var check = Assert.Single(module.BindingTypes!);
        Assert.Equal(FluxBindingTypeStatus.Unknown, check.Status);
        Assert.Null(check.ActualType);
        Assert.True(module.Deployed);
    }

    [Fact]
    public async Task SlotTargetForANonSlotPortStops()
    {
        var e = await Assert.ThrowsAsync<RLoopException>(() => Deploy(Manifest(CountSource,
                """, "bindings":{ "Count":{ "target":"$slot:pool", "mode":"source" } }"""),
            Resolved("Count", "source", "$slot:pool", "slot", "[FrooxEngine]FrooxEngine.Slot")));

        Assert.Equal("FLUX_BINDING_TYPE_MISMATCH", e.Code);
    }

    private const string ButtonBinding = """, "bindings":{ "Button":{ "target":"$component:button", "mode":"source" } }""";
    private const string PhysicalButton = "[FrooxEngine]FrooxEngine.PhysicalButton";

    private static RLoop.Core.TypeInfo Type(string fullName, string? baseType, params string[] interfaces) =>
        new(fullName, "FrooxEngine", "FrooxEngine", fullName[(fullName.LastIndexOf('.') + 1)..], baseType, false, false, false, false,
            true, false, true, [], interfaces);

    [Fact]
    public async Task ComponentTargetThatListsTheInterfaceIsMatched()
    {
        _world.Types[PhysicalButton] = Type(PhysicalButton, "[FrooxEngine]FrooxEngine.Component", "[FrooxEngine]FrooxEngine.IButton");

        var result = await Deploy(Manifest("module Main\nin Button: IButton element\nwhere { Button->display }", ButtonBinding),
            Resolved("Button", "source", "$component:button", "component", PhysicalButton));

        var check = Assert.Single(Assert.Single(result.Modules).BindingTypes!);
        Assert.Equal(FluxBindingTypeStatus.Matched, check.Status);
        Assert.Equal([PhysicalButton], _world.TypeReads);
    }

    [Fact]
    public async Task ComponentTargetWhoseTypeInformationCannotBeReadIsUnknownForAnInterface()
    {
        var result = await Deploy(Manifest("module Main\nin Button: IButton element\nwhere { Button->display }", ButtonBinding),
            Resolved("Button", "source", "$component:button", "component", PhysicalButton));

        var module = Assert.Single(result.Modules);
        Assert.Equal(FluxBindingTypeStatus.Unknown, Assert.Single(module.BindingTypes!).Status);
        Assert.True(module.Deployed);
    }

    [Fact]
    public async Task ComponentTargetDerivedFromThePortClassIsMatched()
    {
        _world.Types[PhysicalButton] = Type(PhysicalButton, "[FrooxEngine]FrooxEngine.ButtonBase");
        _world.Types["[FrooxEngine]FrooxEngine.ButtonBase"] = Type("[FrooxEngine]FrooxEngine.ButtonBase", "[FrooxEngine]FrooxEngine.Component");

        var result = await Deploy(Manifest("module Main\nin Button: ButtonBase element\nwhere { Button->display }", ButtonBinding),
            Resolved("Button", "source", "$component:button", "component", PhysicalButton));

        Assert.Equal(FluxBindingTypeStatus.Matched, Assert.Single(Assert.Single(result.Modules).BindingTypes!).Status);
    }

    [Fact]
    public async Task ComponentTargetWhoseWholeBaseChainWasReadWithoutThePortClassStops()
    {
        _world.Types[PhysicalButton] = Type(PhysicalButton, "[FrooxEngine]FrooxEngine.Component");
        _world.Types["[FrooxEngine]FrooxEngine.Component"] = Type("[FrooxEngine]FrooxEngine.Component", null);

        var e = await Assert.ThrowsAsync<RLoopException>(() => Deploy(
            Manifest("module Main\nin Button: TextField element\nwhere { Button->display }", ButtonBinding),
            Resolved("Button", "source", "$component:button", "component", PhysicalButton)));

        Assert.Equal("FLUX_BINDING_TYPE_MISMATCH", e.Code);
        Assert.Empty(_deployer.Executions);
    }

    // ---- Binding keys against the compiled ports through deploy-manifest and the single deploy --------------

    [Fact]
    public async Task ManifestBindingKeysMatchingTheCompiledPortsAreMatched()
    {
        _deployer.PreparedPorts["Main"] = [FluxTestDeployer.Port("Count", "source", "GlobalReference<IValue<int>>")];

        var result = await Deploy(Manifest(CountSource, CountBinding), Resolved("Count", "source", "$member:count.Value", "member", "System.Int32"));

        var check = Assert.Single(result.Modules).Deploy!.Preconditions.BindingPorts!;
        Assert.Equal(FluxDeployBindingPortsStatus.Matched, check.Status);
        Assert.True(check.RequireAllPortsBound);
    }

    [Fact]
    public async Task ManifestCompiledPortMissingFromTheSourceHeaderStopsWithNothingWritten()
    {
        // The source header declares Count only; the compile reports a second port the manifest cannot bind.
        _deployer.PreparedPorts["Main"] = FluxTestDeployer.Ports(("Count", "source"), ("Result", "drive"));
        var world = _world.Snapshot();

        var e = await Assert.ThrowsAsync<RLoopException>(() => Deploy(Manifest(CountSource, CountBinding),
            Resolved("Count", "source", "$member:count.Value", "member", "System.Int32")));

        Assert.Equal("FLUX_BINDING_PORTS_MISMATCH", e.Code);
        Assert.Equal(("none", "none"), (e.Context["worldWrites"], e.Context["pending"]));
        Assert.Equal([new FluxDeployPortKey("Result", "drive")], Assert.IsAssignableFrom<IEnumerable<FluxDeployPortKey>>(e.Context["unboundPorts"]));
        var report = Assert.IsType<FluxManifestResult>(e.Context[FluxManifestOrchestrator.ReportContextKey]);
        Assert.Equal("FLUX_BINDING_PORTS_MISMATCH", report.StoppedBy!.Code);
        Assert.Empty(_deployer.Executions);
        Assert.Equal(world, _world.Snapshot());
        Assert.Equal(0, _world.WriteCalls);
        Assert.False(File.Exists(Path.Combine(_root, ".resoloop", "flux-state", "flux.json")));
    }

    [Fact]
    public async Task SingleDeployReportsUnboundCompiledPortsAsAWarning()
    {
        _deployer.PreparedPorts["Main"] = FluxTestDeployer.Ports(("Count", "source"));

        var result = await FluxSingleDeploy.DeployAsync(_world, _deployer, _root, "Main", FluxTestWorld.Parent, new Uri(_world.Url), null, null);

        var check = result.Deploy.Preconditions.BindingPorts!;
        Assert.Equal(FluxDeployBindingPortsStatus.UnboundPortsAllowed, check.Status);
        Assert.False(check.RequireAllPortsBound);
        Assert.Equal([new FluxDeployPortKey("Count", "source")], check.UnboundPorts);
        Assert.Equal("Flux_1", result.OutputPath);
    }
}
