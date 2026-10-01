using System.Diagnostics;
using System.Text;

namespace RLoop.Core;

internal static class CheckpointFiles
{
    internal static IDisposable AcquireWriter(string path)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex)
        {
            throw new RLoopException("APPLY_STATE_BUSY", "Another apply is using this checkpoint. Wait for it to finish before applying to the same state.",
                ExitCodes.OperationFailed, new Dictionary<string, object?> { ["stateFile"] = path }, innerException: ex);
        }
    }

    // Readers keep the old complete snapshot open while Windows atomically replaces its name.
    internal static string Read(string path)
    {
        var timer = Stopwatch.StartNew();
        var observedContention = File.Exists(path);
        var missingAttempts = 0;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) switch
            {
                2 or 3 => observedContention ? timer.Elapsed < TimeSpan.FromSeconds(2) : missingAttempts++ < 5,
                32 or 33 => timer.Elapsed < TimeSpan.FromSeconds(2),
                _ => false
            })
            {
                // Existing snapshots may alternate missing-path and sharing errors during
                // replacement. Keep first-time checkpoint creation's short missing-path wait.
                if ((ex.HResult & 0xffff) is 32 or 33) observedContention = true;
                Thread.Sleep(Math.Min(10 * (attempt + 1), 100));
            }
        }
    }

    internal static void Write(string path, string content)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
