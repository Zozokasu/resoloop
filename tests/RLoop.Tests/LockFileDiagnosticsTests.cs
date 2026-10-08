using System.Diagnostics;
using System.Text;
using Xunit.Abstractions;

namespace RLoop.Tests;

public sealed class LockFileDiagnosticsTests
{
    [Fact]
    public async Task GetHoldersReportsTheProcessHoldingTheFile()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "held.lock");
        File.WriteAllText(path, "lock");
        using var holder = await StartHolder(path);

        Assert.Contains(LockFileDiagnostics.GetHolders(path), item => item.ProcessId == holder.Process.Id);
    }

    [Fact]
    public void GetHoldersIsEmptyWhenNobodyHoldsTheFile()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "free.lock");
        File.WriteAllText(path, "lock");

        Assert.Empty(LockFileDiagnostics.GetHolders(path));
    }

    [Fact]
    public async Task CaptureIncludesTimelineErrorAndHolders()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "held.lock");
        File.WriteAllText(path, "lock");
        using var holder = await StartHolder(path);

        var text = LockFileDiagnostics.Capture(path, new IOException("x", unchecked((int)0x80070020)),
            ["held T1", "kill T2"], holder.Process);

        Assert.Contains("held T1", text);
        Assert.Contains("kill T2", text);
        Assert.Contains("HResult=0x80070020", text);
        Assert.Contains($"pid={holder.Process.Id}", text);
        Assert.Contains("busyForMs: >2000", text);
    }

    private static async Task<HeldFile> StartHolder(string path)
    {
        var escaped = path.Replace("'", "''");
        var script = "$h = [System.IO.File]::Open('" + escaped + "', [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None); " +
            "[Console]::WriteLine('held'); [Console]::ReadLine() | Out-Null; $h.Dispose()";
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start powershell.exe.");
        try
        {
            Assert.Equal("held", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            return new HeldFile(process);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.Dispose();
            throw;
        }
    }

    private sealed class HeldFile(Process process) : IDisposable
    {
        public Process Process { get; } = process;
        public void Dispose()
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                Process.WaitForExit();
            }
            Process.Dispose();
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lock-diagnostics-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
