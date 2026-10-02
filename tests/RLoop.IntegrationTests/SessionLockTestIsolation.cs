using System.Runtime.CompilerServices;
using RLoop.Core;

namespace RLoop.IntegrationTests;

internal static class SessionLockTestIsolation
{
    // All clients in this assembly coordinate in the same test-owned directory.
    // Per-client directories would hide same-endpoint contention and foreign pending state.
    internal static string LockDirectory { get; } = Path.Combine(Path.GetTempPath(),
        "resoloop-integration-write-locks-" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Initialize()
    {
        SessionWriteLock.DirectoryForTests = _ => LockDirectory;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (!Directory.Exists(LockDirectory)) return;
                foreach (var file in Directory.EnumerateFiles(LockDirectory, "*.lock")) File.Delete(file);
                Directory.Delete(LockDirectory); // Only this fixture-created directory, never recursive.
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
    }
}
