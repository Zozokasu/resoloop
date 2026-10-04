using System.Diagnostics;
using System.Reflection;
using LaidOutModule = FluxSDK.Build.Incremental.LaidOutModule;
using FluxSDK.Common;
using FluxSDK.ResoniteLink;
using Newtonsoft.Json.Linq;
using RLoop.Core;
using RLoop.Flux.Deployer;
using BuildConfig = FluxSDK.Packages.Types.BuildConfig;

namespace RLoop.Tests;

[Collection("Flux SDK console")]
public sealed class FluxSdkElementIdTests : IDisposable
{
    private const string ProbeEnvironment = "RESOLOOP_TEST_FLUX_ID_PROBE";
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-sdk-ids-" + Guid.NewGuid().ToString("N"));

    public FluxSdkElementIdTests() { Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "protograph.toml"), "name = \"sdk-id-regression\"\nversion = \"0.1.0\"\n[build.dev]\noptimization_preset = \"dev\"\n[dependencies]\n"); }

    public void Dispose() => Directory.Delete(root, recursive: true);

    // Exercise the production compiler, rather than reimplementing its store/epoch setup in the test.
    // No link is initialized. Real SDK packing includes root, asset, component, member and reference IDs.
    private Task<string[]> CompileAndPack(string source, string? libraryPath = null) =>
        FluxConsoleCapture.RunAsync(async (_, _) =>
        {
            File.WriteAllText(Path.Combine(root, "Main.pg"), source);
            var compile = typeof(FluxSdkDeployer).Assembly.GetType("RLoop.Flux.Deployer.Deploy")!
                .GetMethod("compile", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
            var pending = (Task)compile.Invoke(null, [root, "Main", libraryPath ?? Environment.GetEnvironmentVariable("RESOLOOP_TEST_FLUX_LIBRARY_PATH")])!;
            await pending;
            var result = pending.GetType().GetProperty("Result")!.GetValue(pending)!;
            var config = (BuildConfig)result.GetType().GetProperty("Item2")!.GetValue(result)!;
            var compiled = (LaidOutModule)result.GetType().GetProperty("Item3")!.GetValue(result)!;
            Assert.True(compiled.ParsedModule is not null, string.Join("\n", compiled.Diagnostics.Select(d => d.Contents)));
            Assert.Equal("Main", compiled.ModuleName);
            Assert.DoesNotContain(compiled.Diagnostics, diagnostic => diagnostic.Level.IsError);

            var operations = Link.batchAddProtoGraphNodes(config, "offline-parent", compiled, null, null);
            Assert.NotEmpty(operations);
            // Newtonsoft uses the runtime member types, including IDs hidden behind Member values.
            var packed = JArray.FromObject(operations);
            var ids = packed.Descendants().OfType<JValue>()
                .Where(value => value.Type == JTokenType.String)
                .Select(value => value.Value<string>()!)
                .Where(value => Guid.TryParse(value, out var id) && id.ToString().Substring(9, 9) == "ffff-ffff")
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            Assert.NotEmpty(ids);
            Assert.All(ids, id => Assert.NotEqual(0UL, BitConverter.ToUInt64(Guid.Parse(id).ToByteArray(), 8)));
            var epochs = ids.Select(id => BitConverter.ToUInt64(Guid.Parse(id).ToByteArray(), 8)).Distinct().ToArray();
            Assert.Single(epochs);
            Assert.Equal(ElementID.currentEpoch(), epochs[0]);
            return ids;
        }, CancellationToken.None);

    [InstalledFluxLibrariesFact]
    public async Task RepeatedProductionCompilesPackDisjointIdsForUnchangedAndChangedSources()
    {
        var first = await CompileAndPack("module Main\nwhere {\n}");
        var unchanged = await CompileAndPack("module Main\nwhere {\n}");
        var changed = await CompileAndPack("module Main\nwhere {\n\n}");
        Assert.Empty(first.Intersect(unchanged, StringComparer.Ordinal));
        Assert.Empty(first.Intersect(changed, StringComparer.Ordinal));
        Assert.Empty(unchanged.Intersect(changed, StringComparer.Ordinal));
    }

    [InstalledFluxLibrariesFact]
    public async Task IndependentProcessesCompileAndPackWithFreshIds()
    {
        var probeOutput = Environment.GetEnvironmentVariable(ProbeEnvironment);
        if (probeOutput is not null)
        {
            var ids = await CompileAndPack("module Main\nwhere {\n}");
            File.WriteAllLines(probeOutput, ids);
            return;
        }

        var first = await RunIndependentProcess("first");
        var second = await RunIndependentProcess("second");
        Assert.NotEmpty(first);
        Assert.NotEmpty(second);
        Assert.Empty(first.Intersect(second, StringComparer.Ordinal));
        // Counter reset is deliberate: uniqueness must come from the independent process seed.
        Assert.Equal(Guid.Parse(first[0]).ToByteArray()[0], Guid.Parse(second[0]).ToByteArray()[0]);
    }


    private Task<string[]> AllocateSdkIds() => FluxConsoleCapture.RunAsync((_, _) =>
    {
        var rotate = typeof(FluxSdkDeployer).Assembly.GetType("RLoop.Flux.Deployer.ElementIdEpoch")!
            .GetMethod("rotate", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        var epoch = (ulong)rotate.Invoke(null, null)!;
        Assert.NotEqual(0UL, epoch);
        var ids = Enumerable.Range(0, 64).Select(_ => ElementID.create().ToString()).ToArray();
        Assert.Equal(64, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.Equal(epoch, BitConverter.ToUInt64(Guid.Parse(id).ToByteArray(), 8)));
        Assert.Equal(1U, BitConverter.ToUInt32(Guid.Parse(ids[0]).ToByteArray(), 0));
        return Task.FromResult(ids);
    }, CancellationToken.None);

    [Fact]
    public async Task ProductionEpochRotationAllocatesDisjointRealSdkIds()
    {
        var first = await AllocateSdkIds();
        var second = await AllocateSdkIds();
        Assert.Empty(first.Intersect(second, StringComparer.Ordinal));
        Assert.Equal(BitConverter.ToUInt64(Guid.Parse(first[0]).ToByteArray(), 8) + 1,
            BitConverter.ToUInt64(Guid.Parse(second[0]).ToByteArray(), 8));
    }

    [Fact]
    public async Task IndependentProcessesAllocateDisjointRealSdkIds()
    {
        var probeOutput = Environment.GetEnvironmentVariable(ProbeEnvironment);
        if (probeOutput is not null)
        {
            File.WriteAllLines(probeOutput, await AllocateSdkIds());
            return;
        }
        const string test = "RLoop.Tests.FluxSdkElementIdTests.IndependentProcessesAllocateDisjointRealSdkIds";
        var first = await RunIndependentProcess("allocated-first", test);
        var second = await RunIndependentProcess("allocated-second", test);
        Assert.Empty(first.Intersect(second, StringComparer.Ordinal));
    }

    private async Task<string[]> RunIndependentProcess(string name, string test = "RLoop.Tests.FluxSdkElementIdTests.IndependentProcessesCompileAndPackWithFreshIds")
    {
        var output = Path.Combine(root, name + ".ids");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(FluxSdkElementIdTests).Assembly.Location);
        start.ArgumentList.Add("--Tests:" + test);
        start.Environment[ProbeEnvironment] = output;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, (await stdout) + (await stderr));
        Assert.True(File.Exists(output), "The independent SDK compile/pack probe did not produce ID evidence.");
        return File.ReadAllLines(output);
    }

    [InstalledFluxLibrariesFact]
    public async Task ChangedLiteralGraphUsesFreshEntityAndReferenceIds()
    {
        var libraryPath = Environment.GetEnvironmentVariable("RESOLOOP_TEST_FLUX_LIBRARY_PATH")!;
        var first = await CompileAndPack("module Main\nwhere {\n    1->display\n}", libraryPath);
        var unchanged = await CompileAndPack("module Main\nwhere {\n    1->display\n}", libraryPath);
        var changed = await CompileAndPack("module Main\nwhere {\n    2->display\n}", libraryPath);
        Assert.True(first.Length > 5, "The real literal/display graph must cover more than just its root.");
        Assert.Empty(first.Intersect(unchanged, StringComparer.Ordinal));
        Assert.Empty(first.Intersect(changed, StringComparer.Ordinal));
    }

    [InstalledFluxLibrariesFact]
    public async Task InvalidCompileNeverConnectsOrRequestsARoot()
    {
        File.WriteAllText(Path.Combine(root, "Main.pg"), "module Main\nwhere {");
        IFluxDeployer deployer = new FluxSdkDeployer();
        var result = await deployer.ExecuteAsync(new FluxDeployExecuteRequest(root, "Main", "offline-parent",
            new Uri("ws://127.0.0.1:1/"), Environment.GetEnvironmentVariable("RESOLOOP_TEST_FLUX_LIBRARY_PATH"), null, null, null, "Main", null, TimeSpan.FromSeconds(1)), CancellationToken.None);
        Assert.Equal(FluxDeployOutcome.CompileFailed, result.Outcome);
        Assert.Equal(FluxDeploySendStatus.NotSentProven, result.SendStatus);
        Assert.Null(result.RequestedRootSlotId);
        Assert.Empty(result.WriterObservations);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == "error");
    }
}

public sealed class InstalledFluxLibrariesFactAttribute : FactAttribute
{
    public InstalledFluxLibrariesFactAttribute()
    {
        var path = Environment.GetEnvironmentVariable("RESOLOOP_TEST_FLUX_LIBRARY_PATH");
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            Skip = "Set RESOLOOP_TEST_FLUX_LIBRARY_PATH to approved installed libraries for the offline literal/display graph test.";
    }
}
