using System.Text;

namespace RLoop.Core;

internal static class CheckpointFiles
{
    internal static IDisposable AcquireWriter(string path)
    {
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
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch (IOException ex) when (attempt < 5 && (ex.HResult & 0xffff) is 2 or 3 or 32 or 33)
            {
                // Windows can briefly expose a rename/delete-pending window to a new opener.
                Thread.Sleep(10 * (attempt + 1));
            }
        }
    }

    internal static void Write(string path, string content)
    {
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
