namespace RLoop.IntegrationTests;

internal static class LiveIntegrationGate
{
    internal const string Variable = "RESOLOOP_RUN_INTEGRATION";

    internal static string? SkipReason(string? value) =>
        value == "1" ? null : $"{Variable} is not 1";

    internal static string? SkipReason(string? value, string unmetCondition, bool conditionMet) =>
        SkipReason(value) ?? (conditionMet ? null : unmetCondition);
}

public class LiveIntegrationFactAttribute : FactAttribute
{
    public LiveIntegrationFactAttribute()
    {
        Skip = LiveIntegrationGate.SkipReason(Environment.GetEnvironmentVariable(LiveIntegrationGate.Variable));
    }
}
