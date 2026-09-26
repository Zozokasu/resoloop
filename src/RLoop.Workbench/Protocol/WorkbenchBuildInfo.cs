using System.Reflection;

namespace ResoniteWorkbench.Protocol;

public sealed record WorkbenchBuildInfo(string WorkbenchVersion, int ProtocolVersion)
{
    public const int CurrentProtocolVersion = 1;

    public static WorkbenchBuildInfo Current { get; } =
        new(ResolveWorkbenchVersion(), CurrentProtocolVersion);

    public string DisplayVersion => $"v{WorkbenchVersion} / protocol {ProtocolVersion}";

    private static string ResolveWorkbenchVersion()
    {
        Assembly assembly = typeof(WorkbenchBuildInfo).Assembly;

        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            int buildMetadata = informational.IndexOf('+', StringComparison.Ordinal);
            return buildMetadata < 0 ? informational : informational[..buildMetadata];
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
