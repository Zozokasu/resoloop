using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;
using Xunit.Abstractions;

namespace RLoop.Tests;

internal sealed record LockFileHolder(int ProcessId, string Name, DateTime StartTimeUtc);

internal static class LockFileDiagnostics
{
    private const int ErrorMoreData = 234;

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int sessionFlags, string sessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint sessionHandle, uint fileCount, string[] fileNames,
        uint applicationCount, [In] RM_UNIQUE_PROCESS[]? applications, uint serviceCount, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint sessionHandle, out uint needed, ref uint count,
        [In, Out] RM_PROCESS_INFO[]? affectedApps, ref uint rebootReasons);

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS { public int dwProcessId; public FILETIME ProcessStartTime; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    public static IReadOnlyList<LockFileHolder> GetHolders(string path)
    {
        var result = RmStartSession(out var sessionHandle, 0, Guid.NewGuid().ToString("N"));
        if (result != 0) throw ApiFailure("RmStartSession", result);

        try
        {
            result = RmRegisterResources(sessionHandle, 1, [path], 0, null, 0, null);
            if (result != 0) throw ApiFailure("RmRegisterResources", result);

            uint count = 0;
            uint reasons = 0;
            result = RmGetList(sessionHandle, out var needed, ref count, null, ref reasons);
            if (result == 0) return [];
            if (result != ErrorMoreData) throw ApiFailure("RmGetList", result);

            var processes = new RM_PROCESS_INFO[needed];
            count = needed;
            result = RmGetList(sessionHandle, out _, ref count, processes, ref reasons);
            if (result != 0) throw ApiFailure("RmGetList", result);

            var holders = new List<LockFileHolder>((int)count);
            for (var i = 0; i < count; i++)
            {
                var process = processes[i].Process;
                var startFileTime = ((long)process.ProcessStartTime.dwHighDateTime << 32) |
                    (uint)process.ProcessStartTime.dwLowDateTime;
                holders.Add(new LockFileHolder(process.dwProcessId, processes[i].strAppName,
                    DateTime.FromFileTimeUtc(startFileTime)));
            }
            return holders;
        }
        finally
        {
            var endResult = RmEndSession(sessionHandle);
            if (endResult != 0) throw ApiFailure("RmEndSession", endResult);
        }
    }

    public static string Capture(string path, Exception error, IReadOnlyList<string> timeline, Process? holder)
    {
        var text = new StringBuilder();
        text.AppendLine($"path: {path}");
        foreach (var item in timeline) text.AppendLine($"timeline: {item}");
        for (Exception? current = error; current is not null; current = current.InnerException)
            text.AppendLine($"error: {current.GetType().Name} HResult=0x{current.HResult:X8} {current.Message}");

        try
        {
            if (holder is null) text.AppendLine("holder: none");
            else if (holder.HasExited) text.AppendLine($"holder: pid={holder.Id} exited=True exitCode={holder.ExitCode}");
            else text.AppendLine($"holder: pid={holder.Id} exited=False");
        }
        catch (Exception e) { text.AppendLine($"holder: unavailable {e.Message}"); }

        try
        {
            var holders = GetHolders(path);
            if (holders.Count == 0) text.AppendLine("holders: none");
            else foreach (var item in holders)
                text.AppendLine($"holders: pid={item.ProcessId} name={item.Name} start={item.StartTimeUtc:O}");
        }
        catch (Exception e) { text.AppendLine($"holders: unavailable {e.Message}"); }

        text.AppendLine(BusyDuration(path));
        return text.ToString();
    }

    public static void Report(ITestOutputHelper output, string text)
    {
        try { output.WriteLine(text); } catch { }
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "resoloop-test-diagnostics");
            Directory.CreateDirectory(directory);
            var name = $"lock-flake-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Environment.ProcessId}.txt";
            File.WriteAllText(Path.Combine(directory, name), text);
        }
        catch { }
    }

    private static string BusyDuration(string path)
    {
        if (!File.Exists(path)) return "busyForMs: file-missing";
        var timer = Stopwatch.StartNew();
        do
        {
            try
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return $"busyForMs: {timer.ElapsedMilliseconds}";
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            if (timer.ElapsedMilliseconds >= 2000) break;
            Thread.Sleep(25);
        } while (timer.ElapsedMilliseconds <= 2000);
        return "busyForMs: >2000";
    }

    private static InvalidOperationException ApiFailure(string api, int result) =>
        new($"{api} failed with return value {result}.");
}
