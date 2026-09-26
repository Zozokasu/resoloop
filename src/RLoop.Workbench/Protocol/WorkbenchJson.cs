using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResoniteWorkbench.Protocol;

/// <summary>
/// The one JSON shape every front end uses, so the same request gives the same result over RPC
/// and in process.
/// </summary>
public static class WorkbenchJson
{
    public static JsonSerializerOptions Options { get; } = Create(strict: false);

    /// <summary>
    /// For request parameters: unknown properties, missing required values and nulls in
    /// non-nullable positions are rejected instead of being silently defaulted.
    /// </summary>
    public static JsonSerializerOptions StrictOptions { get; } = Create(strict: true);

    private static JsonSerializerOptions Create(bool strict)
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            RespectRequiredConstructorParameters = true,
        };

        if (strict)
        {
            options.RespectNullableAnnotations = true;
            options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        }

        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
