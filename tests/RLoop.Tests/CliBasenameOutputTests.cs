using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CliBasenameOutputTests
{
    [Fact]
    public void DiagnosticsFilenameWithoutDirectoryWritesInCurrentDirectory()
    {
        var name = "l3-diagnostics-" + Guid.NewGuid().ToString("N") + ".json";
        try
        {
            using var output = new OutputWriter(true);
            output.ConfigureDiagnostics(name, "validate", null);
            output.WriteDiagnostics();
            using var content = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Environment.CurrentDirectory, name)));
            Assert.Equal("1", content.RootElement.GetProperty("diagnosticVersion").GetString());
        }
        finally { File.Delete(name); }
    }

    [Fact]
    public void CheckpointBasenameWriterRetainsExplicitLockAndAtomicReplacement()
    {
        var name = "l3-checkpoint-" + Guid.NewGuid().ToString("N") + ".json";
        try
        {
            using (CheckpointFiles.AcquireWriter(name))
            {
                Assert.Equal("APPLY_STATE_BUSY", Assert.Throws<RLoopException>(() => CheckpointFiles.AcquireWriter(name)).Code);
                CheckpointFiles.Write(name, "{\"value\":1}");
                CheckpointFiles.Write(name, "{\"value\":2}");
                Assert.Equal("{\"value\":2}", CheckpointFiles.Read(name));
            }
        }
        finally { File.Delete(name); File.Delete(name + ".lock"); }
    }
}
