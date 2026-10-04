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

    // Readers share delete access so a snapshot can remain open during replacement.
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
        Exception? writeFailure = null;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(content);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) ReplaceWithContentionRetry(temporary, path);
            else File.Move(temporary, path);
        }
        catch (Exception ex)
        {
            writeFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception cleanupFailure) when (writeFailure is not null && cleanupFailure is IOException or UnauthorizedAccessException)
            {
                // Keep the persistence failure and native error code if cleanup is also blocked.
                writeFailure.Data["checkpointTemporaryFile"] = temporary;
                writeFailure.Data["checkpointTemporaryFileCleanupError"] = cleanupFailure;
            }
        }
    }

    private static void ReplaceWithContentionRetry(string temporary, string path)
    {
        var timer = Stopwatch.StartNew();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Replace(temporary, path, null);
                return;
            }
            catch (IOException ex) when (OperatingSystem.IsWindows()
                && (ex.HResult & 0xffff) is 32 or 33 or 1175
                && timer.Elapsed < TimeSpan.FromSeconds(2))
            {
                // Windows 1175 leaves both original names intact, like sharing/lock errors.
                // Do not retry 1176/1177: they can move or remove the previous snapshot.
                Thread.Sleep(Math.Min(10 * (attempt + 1), 100));
            }
        }
    }
}
