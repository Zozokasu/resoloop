using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ResoniteWorkbench.Protocol;

public sealed record RpcUsageExport(
    int UsageCatalogVersion,
    string WorkbenchVersion,
    int ProtocolVersion,
    string ContentSha256,
    IReadOnlyDictionary<string, RpcMethodUsage> Methods,
    IReadOnlyDictionary<string, RpcWorkflowUsage> Workflows,
    IReadOnlyList<RpcUsageError> ConnectionErrors)
{
    public static RpcUsageExport Create(
        RpcUsageOverview overview,
        IReadOnlyDictionary<string, RpcMethodUsage> methods,
        IReadOnlyDictionary<string, RpcWorkflowUsage> workflows)
    {
        ArgumentNullException.ThrowIfNull(overview);
        ArgumentNullException.ThrowIfNull(methods);
        ArgumentNullException.ThrowIfNull(workflows);

        SortedDictionary<string, RpcMethodUsage> sortedMethods = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, RpcMethodUsage> pair in methods)
            sortedMethods.Add(pair.Key, pair.Value);
        SortedDictionary<string, RpcWorkflowUsage> sortedWorkflows = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, RpcWorkflowUsage> pair in workflows)
            sortedWorkflows.Add(pair.Key, pair.Value);

        byte[] content = JsonSerializer.SerializeToUtf8Bytes(
            new ExportContent(
                overview.UsageCatalogVersion,
                overview.WorkbenchVersion,
                overview.ProtocolVersion,
                sortedMethods,
                sortedWorkflows,
                overview.ConnectionErrors),
            WorkbenchJson.Options);

        return new RpcUsageExport(
            overview.UsageCatalogVersion,
            overview.WorkbenchVersion,
            overview.ProtocolVersion,
            Convert.ToHexStringLower(SHA256.HashData(content)),
            sortedMethods,
            sortedWorkflows,
            overview.ConnectionErrors);
    }

    private sealed record ExportContent(
        int UsageCatalogVersion,
        string WorkbenchVersion,
        int ProtocolVersion,
        IReadOnlyDictionary<string, RpcMethodUsage> Methods,
        IReadOnlyDictionary<string, RpcWorkflowUsage> Workflows,
        IReadOnlyList<RpcUsageError> ConnectionErrors);
}

public static class RpcUsageMarkdown
{
    private static readonly JsonSerializerOptions Indented = new(WorkbenchJson.Options) { WriteIndented = true };

    public static string Render(RpcUsageExport export)
    {
        ArgumentNullException.ThrowIfNull(export);

        StringBuilder text = new();
        text.Append("# Workbench RPC usage\n\n");
        text.Append("- Workbench version: ").Append(export.WorkbenchVersion).Append('\n');
        text.Append("- Protocol version: ").Append(export.ProtocolVersion).Append('\n');
        text.Append("- Usage catalog version: ").Append(export.UsageCatalogVersion).Append('\n');
        text.Append("- Content SHA-256: `").Append(export.ContentSha256).Append("`\n\n");

        text.Append("## Methods\n\n");
        foreach (string name in export.Methods.Keys)
            text.Append("- [").Append(name).Append("](#").Append(name.Replace('.', '-')).Append(")\n");
        text.Append('\n');

        foreach ((string name, RpcMethodUsage method) in export.Methods)
        {
            text.Append("### ").Append(name).Append("\n\n");
            text.Append("Capability: `").Append(method.Capability ?? "none").Append("`\n\n");
            if (method.ExtraCapability is { } extraCapability)
                text.Append("Conditional capability: `").Append(extraCapability).Append("`\n\n");
            text.Append(method.Summary).Append("\n\n");
            text.Append(method.Description).Append("\n\n");

            if (method.Constraints.Count > 0)
            {
                text.Append("Constraints:\n\n");
                foreach (string constraint in method.Constraints)
                    text.Append("- ").Append(constraint).Append('\n');
                text.Append('\n');
            }

            AppendErrors(text, "RPC errors", method.RpcErrors);
            AppendErrors(text, "Result issues", method.ResultIssues);
            AppendErrors(text, "Connection errors", method.ConnectionErrors);

            if (method.RelatedMethods.Count > 0)
                text.Append("Related: ").Append(string.Join(", ", method.RelatedMethods)).Append("\n\n");

            text.Append("Params schema:\n\n```json\n").Append(JsonSerializer.Serialize(method.ParamsSchema, Indented)).Append("\n```\n\n");
            text.Append("Result schema:\n\n```json\n").Append(JsonSerializer.Serialize(method.ResultSchema, Indented)).Append("\n```\n\n");
            text.Append("Request example:\n\n```json\n").Append(PrettyJson(method.RequestExample)).Append("\n```\n\n");
            if (method.ResponseExample is { } responseExample)
                text.Append("Response example:\n\n```json\n").Append(PrettyJson(responseExample)).Append("\n```\n\n");
        }

        text.Append("## Workflows\n\n");
        foreach ((string name, RpcWorkflowUsage workflow) in export.Workflows)
        {
            text.Append("### ").Append(name).Append("\n\n");
            text.Append(workflow.Summary).Append("\n\n");
            int step = 1;
            foreach (RpcUsageStep usageStep in workflow.Steps)
            {
                text.Append(step++).Append(". `").Append(usageStep.Method).Append("` — ").Append(usageStep.Purpose);
                if (usageStep.Caution is { } caution)
                    text.Append(" (").Append(caution).Append(')');
                text.Append('\n');
            }
            text.Append('\n');
        }

        text.Append("## Connection-wide errors\n\n");
        foreach (RpcUsageError error in export.ConnectionErrors)
            text.Append("- `").Append(error.Code).Append("` — ").Append(error.Description).Append('\n');

        text.Append("\nContent SHA-256: ").Append(export.ContentSha256).Append('\n');

        return text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void AppendErrors(StringBuilder text, string heading, IReadOnlyList<RpcUsageError> errors)
    {
        if (errors.Count == 0)
            return;

        text.Append(heading).Append(":\n\n");
        foreach (RpcUsageError error in errors)
            text.Append("- `").Append(error.Code).Append("` — ").Append(error.Description).Append('\n');
        text.Append('\n');
    }

    private static string PrettyJson(string json)
    {
        try
        {
            return JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement, Indented);
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
