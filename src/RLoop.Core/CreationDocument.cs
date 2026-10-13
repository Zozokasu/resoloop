using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace RLoop.Core;

/// <summary>A versioned offline creation envelope around the existing apply definition.</summary>
public sealed record CreationDocument(
    string SchemaVersion,
    string Kind,
    string CreationVersion,
    string CreationId,
    ApplyDocument Definition,
    IReadOnlyList<JsonElement>? Objects = null,
    IReadOnlyList<JsonElement>? Variables = null,
    IReadOnlyList<JsonElement>? Requirements = null,
    IReadOnlyList<JsonElement>? Bindings = null,
    IReadOnlyList<JsonElement>? Scripts = null,
    IReadOnlyList<JsonElement>? Diagnostics = null,
    ApplyAuthoringSpec? Authoring = null,
    [property: JsonIgnore] string? SourcePath = null)
{
    [JsonIgnore]
    public string? InputHash { get; init; }

    [JsonIgnore]
    public string? SourceText { get; init; }

    private static readonly HashSet<string> AllowedProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion", "kind", "creationVersion", "creationId", "definition", "objects", "variables",
        "requirements", "bindings", "scripts", "diagnostics", "authoring"
    };

    private static readonly HashSet<string> UnsupportedCollections = new(StringComparer.Ordinal)
    {
        "objects", "variables", "requirements", "bindings", "scripts"
    };

    private static readonly JsonSerializerOptions DefinitionOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static CreationDocument Load(string path)
    {
        string text;
        try { text = File.ReadAllText(path, new UTF8Encoding(false, true)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw Invalid($"Could not read creation document '{path}': {ex.Message}", ex);
        }
        return Parse(text, Path.GetFullPath(path));
    }

    public static CreationDocument Parse(string text, string? sourcePath = null)
    {
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        var sourceText = text;
        var inputHash = HashSourceText(sourceText);
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(text); }
        catch (JsonException ex) { throw Invalid("Creation document is not valid JSON: " + ex.Message, ex); }
        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid("Creation document root must be an object.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid($"Duplicate creation document property '{property.Name}'.");
                if (!AllowedProperties.Contains(property.Name)) throw Invalid($"Unknown creation document property '{property.Name}'.");
            }

            RequireString(root, "schemaVersion", "creation-1");
            RequireString(root, "kind", "resoloop-creation");
            RequireString(root, "creationVersion", "1");
            if (!root.TryGetProperty("creationId", out var idElement) || idElement.ValueKind != JsonValueKind.String)
                throw CreationIdInvalid("creationId must be a string containing 1..128 non-control characters.");
            var creationId = idElement.GetString()!;
            if (creationId.Length is < 1 or > 128 || creationId.Any(char.IsControl))
                throw CreationIdInvalid("creationId must be 1..128 characters and contain no control characters.");

            foreach (var name in UnsupportedCollections)
            {
                if (!root.TryGetProperty(name, out var value)) continue;
                if (value.ValueKind != JsonValueKind.Array)
                    throw Invalid($"{name} must be an empty array; this field is not supported in this version.");
                if (value.GetArrayLength() != 0)
                {
                    var path = $"$.{name}[0]";
                    const string message = "This content is not supported by regenerate in this version.";
                    throw new RLoopException("CREATION_DOCUMENT_INVALID", $"{name} is not supported in this version.",
                        ExitCodes.ValidationFailed,
                        new Dictionary<string, object?>
                        {
                            ["issues"] = new[] { new ApplyValidationIssue("CREATION_DOCUMENT_INVALID", message, path) }
                        });
                }
            }
            if (root.TryGetProperty("diagnostics", out var diagnostics) && diagnostics.ValueKind != JsonValueKind.Array)
                throw Invalid("diagnostics must be an array.");
            try { ApplyProjectContext.ValidateJson(text); }
            catch (RLoopException ex) { throw Invalid("Invalid authoring metadata: " + ex.Message, ex); }
            ApplyAuthoringSpec? envelopeAuthoring = null;
            if (root.TryGetProperty("authoring", out var authoring))
            {
                try { envelopeAuthoring = JsonSerializer.Deserialize<ApplyAuthoringSpec>(authoring.GetRawText(), DefinitionOptions); }
                catch (JsonException ex) { throw Invalid("Invalid authoring metadata: " + ex.Message, ex); }
            }
            if (!root.TryGetProperty("definition", out var definition) || definition.ValueKind != JsonValueKind.Object)
                throw Invalid("definition must be an apply document object.");

            if (!definition.TryGetProperty("ownership", out var ownership) || ownership.ValueKind != JsonValueKind.Object ||
                !ownership.TryGetProperty("key", out var ownershipKey) || ownershipKey.ValueKind != JsonValueKind.String ||
                !string.Equals(creationId, ownershipKey.GetString(), StringComparison.Ordinal))
                throw Invalid("creationId must exactly match definition.ownership.key.");

            try
            {
                var compiled = ApplyDocumentCompiler.CompileBundleIr(definition.GetRawText());
                ApplyProjectContext.ValidateJson(compiled.Json);
                var document = JsonSerializer.Deserialize<ApplyDocument>(compiled.Json, DefinitionOptions)
                    ?? throw new JsonException("definition was empty.");
                if (envelopeAuthoring is not null)
                {
                    if (document.Authoring is not null)
                        throw new InvalidOperationException("authoring cannot be declared both on the envelope and definition.");
                    document = document with { Authoring = envelopeAuthoring };
                }
                document = document with
                {
                    SourcePath = sourcePath,
                    Compilation = compiled.Summary
                };
                if (document.Authoring is not null)
                    document = document with { ResolvedProjectContext = ApplyProjectContext.Resolve(document) };
                if (!string.Equals(creationId, document.Ownership?.Key, StringComparison.Ordinal))
                    throw new InvalidOperationException("Compiled definition ownership.key changed or is missing.");
                return new CreationDocument("creation-1", "resoloop-creation", "1", creationId, document,
                    ReadArray(root, "objects"), ReadArray(root, "variables"), ReadArray(root, "requirements"),
                    ReadArray(root, "bindings"), ReadArray(root, "scripts"), ReadArray(root, "diagnostics"),
                    envelopeAuthoring, sourcePath) { InputHash = inputHash, SourceText = sourceText };
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or RLoopException or NotSupportedException or ArgumentException)
            {
                throw Invalid("Invalid creation definition: " + ex.Message, ex);
            }
        }
    }

    public async Task<ApplyValidationResult> ValidateAsync(CancellationToken cancellationToken = default)
    {
        var shapeIssues = ValidateEnvelopeShape();
        if (shapeIssues.Count > 0)
            return new ApplyValidationResult(false, Definition?.SchemaVersion, 0, 0, 0, false, shapeIssues);

        try
        {
            var definition = Definition with { SourcePath = SourcePath ?? Definition.SourcePath };
            if (Authoring is not null) definition = definition with { Authoring = Authoring, ResolvedProjectContext = null };
            if (definition.Authoring is not null)
                definition = definition with { ResolvedProjectContext = ApplyProjectContext.Resolve(definition) };
            var result = await ApplyDocumentValidator.ValidateAsync(definition, cancellationToken: cancellationToken);
            return result.Valid ? result : result with { Issues = InvalidIssues(result.Issues) };
        }
        catch (Exception ex) when (ex is RLoopException or JsonException or InvalidOperationException or ArgumentException)
        {
            return new ApplyValidationResult(false, Definition?.SchemaVersion, 0, 0, 0, false,
                [new ApplyValidationIssue("CREATION_DOCUMENT_INVALID", "Invalid creation definition: " + ex.Message, "$.definition")]);
        }
    }

    /// <summary>Confirms that planning is tied to the exact BOM-stripped raw envelope that was parsed.</summary>
    public string ValidatePlanInput()
    {
        var source = SourceText;
        var hash = InputHash;
        if (source is null || !IsLowerSha256(hash) || !string.Equals(HashSourceText(source), hash, StringComparison.Ordinal))
            throw Invalid("Creation plan requires intact raw source text and its matching lowercase SHA-256 input hash.");
        return hash!;
    }

    private IReadOnlyList<ApplyValidationIssue> ValidateEnvelopeShape()
    {
        var issues = new List<ApplyValidationIssue>();
        void Add(string code, string message, string path) => issues.Add(new(code, message, path));

        if (SchemaVersion != "creation-1") Add("CREATION_DOCUMENT_INVALID", "schemaVersion must be exactly 'creation-1'.", "$.schemaVersion");
        if (Kind != "resoloop-creation") Add("CREATION_DOCUMENT_INVALID", "kind must be exactly 'resoloop-creation'.", "$.kind");
        if (CreationVersion != "1") Add("CREATION_DOCUMENT_INVALID", "creationVersion must be exactly '1'.", "$.creationVersion");
        var sourceText = SourceText;
        var inputHash = InputHash;
        if ((sourceText is null) != (inputHash is null) || sourceText is not null &&
            (!IsLowerSha256(inputHash) || !string.Equals(HashSourceText(sourceText), inputHash, StringComparison.Ordinal)))
            Add("CREATION_DOCUMENT_INVALID", "Attached raw source text and input hash do not match.", "$");
        if (CreationId is null || CreationId.Length is < 1 or > 128 || CreationId.Any(char.IsControl))
            Add("CREATION_ID_INVALID", "creationId must be 1..128 characters and contain no control characters.", "$.creationId");
        if (Definition is null)
        {
            Add("CREATION_DOCUMENT_INVALID", "definition is required.", "$.definition");
            return issues;
        }
        if (Definition.Slot is null)
            Add("CREATION_DOCUMENT_INVALID", "definition.slot must be an object.", "$.definition.slot");
        else
            CheckChildSlots(Definition.Children, "$.definition.children");
        if (!string.Equals(CreationId, Definition.Ownership?.Key, StringComparison.Ordinal))
            Add("CREATION_DOCUMENT_INVALID", "creationId must exactly match definition.ownership.key.", "$.definition.ownership.key");
        if (Authoring is not null && Definition.Authoring is not null && Authoring != Definition.Authoring)
            Add("CREATION_DOCUMENT_INVALID", "authoring cannot differ between the envelope and definition.", "$.authoring");
        CheckEmpty(Objects, "objects");
        CheckEmpty(Variables, "variables");
        CheckEmpty(Requirements, "requirements");
        CheckEmpty(Bindings, "bindings");
        CheckEmpty(Scripts, "scripts");
        return issues;

        void CheckChildSlots(IReadOnlyList<ApplyNodeSpec>? children, string path)
        {
            for (var index = 0; index < (children?.Count ?? 0); index++)
            {
                var childPath = $"{path}[{index}]";
                var child = children![index];
                if (child is null)
                {
                    Add("CREATION_DOCUMENT_INVALID", "Each definition child must be an object.", childPath);
                    continue;
                }
                if (child.Slot is null)
                {
                    Add("CREATION_DOCUMENT_INVALID", "Each definition child requires a slot object.", childPath + ".slot");
                    continue;
                }
                CheckChildSlots(child.Children, childPath + ".children");
            }
        }

        void CheckEmpty(IReadOnlyList<JsonElement>? values, string name)
        {
            if (values is { Count: > 0 }) Add("CREATION_DOCUMENT_INVALID", $"{name} is not supported in this version.", "$." + name);
        }
    }

    private static IReadOnlyList<JsonElement>? ReadArray(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) ? value.EnumerateArray().Select(item => item.Clone()).ToArray() : null;

    private static string HashSourceText(string sourceText) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourceText)));

    private static bool IsLowerSha256(string? value) => value is { Length: 64 } &&
        value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static IReadOnlyList<ApplyValidationIssue> InvalidIssues(IReadOnlyList<ApplyValidationIssue> issues) =>
        issues.Select(issue => issue with
        {
            Code = "CREATION_DOCUMENT_INVALID",
            Message = $"Invalid creation definition ({issue.Code}): {issue.Message}"
        }).ToArray();

    public static bool IsEnvelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is "creationId" or "definition") return true;
            if (property.Name == "kind" && property.Value.ValueKind == JsonValueKind.String &&
                property.Value.GetString()?.Contains("creation", StringComparison.OrdinalIgnoreCase) == true) return true;
            if (property.Name == "schemaVersion" && property.Value.ValueKind == JsonValueKind.String &&
                property.Value.GetString()?.StartsWith("creation-", StringComparison.OrdinalIgnoreCase) == true) return true;
        }
        return false;
    }

    public static void RejectLegacyInput(JsonElement root)
    {
        if (IsEnvelope(root))
            throw new RLoopException("CREATION_MODE_MISMATCH",
                "Creation envelopes require --mode regenerate for the legacy plan/diff/apply entry points.",
                ExitCodes.InvalidArguments);
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("kind", out var kind) &&
            kind.ValueKind == JsonValueKind.String && kind.GetString() == "resoloop-creation-build-bundle")
            throw new RLoopException("CREATION_MODE_MISMATCH", "Creation build bundles are not accepted by legacy apply entry points.", ExitCodes.InvalidArguments);
    }

    internal static void RejectLegacyText(string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        try
        {
            using var parsed = JsonDocument.Parse(text);
            RejectLegacyInput(parsed.RootElement);
        }
        catch (JsonException) { }
    }

    private static void RequireString(JsonElement root, string name, string expected)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() != expected)
            throw Invalid($"{name} must be exactly '{expected}'.");
    }

    private static RLoopException CreationIdInvalid(string message) => new("CREATION_ID_INVALID", message, ExitCodes.ValidationFailed);

    private static RLoopException Invalid(string message, Exception? inner = null) => new(
        "CREATION_DOCUMENT_INVALID", message, ExitCodes.ValidationFailed, innerException: inner);
}
