using System.Security.Cryptography;
using System.Text.Json;

namespace RLoop.Core;

public sealed record CreationStamp(int Version, string CreationId, string OwnerNonce, string GenerationId)
{
    public static string CreateOwnerNonce() => RandomHex();

    public static string CreateGenerationId() => RandomHex();

    public static CreationStamp Create(string creationId, string ownerNonce)
    {
        ValidateCreationId(creationId);
        if (!IsLowerHex(ownerNonce)) throw Invalid("ownerNonce must be 64 lowercase hexadecimal characters.");
        return new(1, creationId, ownerNonce, CreateGenerationId());
    }

    public string Serialize()
    {
        Validate();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WriteString("creationId", CreationId);
            writer.WriteString("ownerNonce", OwnerNonce);
            writer.WriteString("generationId", GenerationId);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static CreationStamp Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid("Stamp must be a JSON object.");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw Invalid($"Duplicate stamp property '{property.Name}'.");
                if (property.Name is not ("version" or "creationId" or "ownerNonce" or "generationId"))
                    throw Invalid($"Unknown stamp property '{property.Name}'.");
            }

            var versionElement = Required(root, "version", JsonValueKind.Number);
            if (!versionElement.TryGetInt32(out var version) || version != 1) throw Invalid("Stamp version must be 1.");
            var stamp = new CreationStamp(version, RequiredString(root, "creationId"),
                RequiredString(root, "ownerNonce"), RequiredString(root, "generationId"));
            stamp.Validate();
            return stamp;
        }
        catch (JsonException ex)
        {
            throw Invalid("Stamp is not valid JSON: " + ex.Message, ex);
        }
    }

    public static bool TryParse(string? json, out CreationStamp? stamp)
    {
        try
        {
            stamp = Parse(json ?? string.Empty);
            return true;
        }
        catch (RLoopException)
        {
            stamp = null;
            return false;
        }
    }

    private void Validate()
    {
        if (Version != 1) throw Invalid("Stamp version must be 1.");
        ValidateCreationId(CreationId);
        if (!IsLowerHex(OwnerNonce)) throw Invalid("ownerNonce must be 64 lowercase hexadecimal characters.");
        if (!IsLowerHex(GenerationId)) throw Invalid("generationId must be 64 lowercase hexadecimal characters.");
    }

    private static JsonElement Required(JsonElement root, string name, JsonValueKind kind)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != kind)
            throw Invalid($"Stamp property '{name}' must be {kind}.");
        return value;
    }

    private static string RequiredString(JsonElement root, string name) => Required(root, name, JsonValueKind.String).GetString()!;

    private static void ValidateCreationId(string value)
    {
        if (value is null || value.Length is < 1 or > 128 || value.Any(char.IsControl))
            throw Invalid("creationId must be 1..128 characters and contain no control characters.");
    }

    private static bool IsLowerHex(string? value) => value is { Length: 64 } && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string RandomHex() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private static RLoopException Invalid(string message, Exception? inner = null) => new(
        "CREATION_OWNER_UNPROVEN", message, ExitCodes.ValidationFailed, innerException: inner);
}
