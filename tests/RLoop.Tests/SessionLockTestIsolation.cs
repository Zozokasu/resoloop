using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using RLoop.Core;

namespace RLoop.Tests;

internal static class SessionLockTestIsolation
{
    private sealed record Location(string Directory);
    private static readonly ConditionalWeakTable<IResoniteClient, Location> Locations = new();
    private static readonly ConcurrentBag<string> TemporaryDirectories = [];
    [ModuleInitializer]
    internal static void Initialize()
    {
        SessionWriteLock.DirectoryForTests = client => Locations.GetValue(client, _ =>
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "resoloop-write-lock-test-" + Guid.NewGuid().ToString("N"));
            TemporaryDirectories.Add(directory);
            return new(directory);
        }).Directory;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var directory in TemporaryDirectories)
            {
                try
                {
                    if (!Directory.Exists(directory)) continue;
                    foreach (var file in Directory.EnumerateFiles(directory, "*.lock")) File.Delete(file);
                    Directory.Delete(directory); // Only this test-created directory; no recursive deletion.
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        };
    }

    internal static void Share(IResoniteClient client, string directory)
    {
        Locations.Remove(client);
        Locations.Add(client, new(directory));
    }
}
