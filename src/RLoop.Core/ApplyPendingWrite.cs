using System.Text.Json.Nodes;

namespace RLoop.Core;

// operationId identifies local evidence; it is not a server request ID or an idempotency key.
internal sealed class ApplyPendingWrite
{
    public string OperationId { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "";
    public string Key { get; set; } = "";
    public string OwnershipKey { get; set; } = "";
    public ApplySessionObservation? Session { get; set; }
    public string? ConnectionGeneration { get; set; }
    public string? BuildId { get; set; }
    public string? BuildIrHash { get; set; }
    public string? CatalogHash { get; set; }
    public string InputHash { get; set; } = "";
    public string? Id { get; set; }
    public string? ParentId { get; set; }
    public string? Type { get; set; }
    public string SendStatus { get; set; } = "possiblySent";
    public bool ResponseReceived { get; set; }
    public bool ResponseAccepted { get; set; }
    public JsonNode? Precondition { get; set; }
    public SlotUpdateRequest? SlotValues { get; set; }
    public Dictionary<string, string> Members { get; set; } = new(StringComparer.Ordinal);
    public List<string> Confirmed { get; set; } = [];
    public JsonNode? Observed { get; set; }
    public Dictionary<string, string> Completeness { get; set; } = new() { ["readback"] = "unknown" };
    public Dictionary<string, ApplySlotOwnershipEvidence> OwnershipSlots { get; set; } = [];
    public ApplyStateSlot? SlotBinding { get; set; }
    public ApplyStateComponent? ComponentBinding { get; set; }
    public ApplyStateAsset? AssetBinding { get; set; }
    public List<string> RemoveSlots { get; set; } = [];
    public List<string> RemoveComponents { get; set; } = [];
}

internal sealed record ApplySlotOwnershipEvidence(string Id, string Name, string? ParentId);
