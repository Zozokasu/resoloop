using System.Numerics;
using System.Text.Json.Nodes;

namespace RLoop.Core;

public sealed record CanvasFrame(string SlotId, string View, string BoundsSource, float[] LocalSize,
    float[] LocalCenter, IReadOnlyList<float[]> RootSpaceCorners, ApplyCameraSpec Camera, float Margin,
    string Capability = "Frames the live Canvas collider rectangle; excludes overflow, curved UI, occlusion and moving-target guarantees. Target transforms are never changed.");

public static class CanvasFraming
{
    private const string CanvasType = "[FrooxEngine]FrooxEngine.UIX.Canvas";
    private const string BoxType = "[FrooxEngine]FrooxEngine.BoxCollider";

    public static async Task<CanvasFrame> ObserveAsync(IResoniteClient client, string slotId, string view = "front",
        int width = 1280, int height = 720, float fieldOfView = 60, float margin = 1.1f,
        CancellationToken cancellationToken = default)
    {
        if (slotId == "Root") throw Invalid("Choose the exact Canvas Slot, not Root.");
        var slot = await client.GetSlotAsync(slotId, 0, false, cancellationToken);
        var canvases = slot.Components.Where(c => c.Type == CanvasType).ToArray();
        if (canvases.Length != 1) throw Invalid("--frame requires exactly one Canvas on the selected Slot; it does not search descendants.");
        var definition = await client.DescribeComponentTypeAsync(CanvasType, cancellationToken);
        if (!definition.Members.Any(m => m.Name == "Collider")) throw Invalid("Runtime Reflection does not expose Canvas.Collider.");
        var canvas = await client.GetComponentAsync(canvases[0].Id, cancellationToken);
        var colliderId = canvas.Members.GetValueOrDefault("Collider")?.TargetId;
        if (colliderId is null || !slot.Components.Any(c => c.Id == colliderId && c.Type == BoxType))
            throw Invalid("Canvas must reference a BoxCollider on its own Slot.");
        var colliderDefinition = await client.DescribeComponentTypeAsync(BoxType, cancellationToken);
        if (!new[] { "Size", "Offset" }.All(name => colliderDefinition.Members.Any(m => m.Name == name)))
            throw Invalid("Runtime Reflection does not expose BoxCollider.Size/Offset.");
        var collider = await client.GetComponentAsync(colliderId, cancellationToken);
        var size = Vector(collider.Members.GetValueOrDefault("Size")?.Value);
        var center = Vector(collider.Members.GetValueOrDefault("Offset")?.Value);
        if (size.X <= 0 || size.Y <= 0 || Math.Abs(size.Z) > 0.0001f)
            throw Invalid("Canvas collider must be a positive, flat XY rectangle; use an explicit camera for other geometry.");

        var transform = Matrix4x4.Identity;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = slot;
        while (current.Id != "Root")
        {
            if (!seen.Add(current.Id) || seen.Count > 64) throw Invalid("Canvas ancestor chain is cyclic or exceeds 64 Slots.");
            if (current.Position is not { } p || current.Rotation is not { } r || current.Scale is not { } s)
                throw Invalid("A live ancestor transform is unavailable.");
            if (s.X <= 0 || s.Y <= 0 || s.Z <= 0) throw Invalid("Mirrored or zero scales require an explicit camera.");
            var q = new Quaternion(r.X, r.Y, r.Z, r.W);
            if (!float.IsFinite(q.LengthSquared()) || Math.Abs(q.LengthSquared() - 1) > .01f) throw Invalid("Invalid ancestor rotation.");
            var local = Matrix4x4.CreateScale(s.X, s.Y, s.Z) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q)) *
                        Matrix4x4.CreateTranslation(p.X, p.Y, p.Z);
            transform *= local;
            if (string.IsNullOrEmpty(current.ParentId)) throw Invalid("Canvas is not attached to Root.");
            if (current.ParentId == "Root") break;
            current = await client.GetSlotAsync(current.ParentId, 0, false, cancellationToken);
        }
        return Fit(slotId, size, center, transform, view, width, height, fieldOfView, margin);
    }

    public static CanvasFrame Fit(string slotId, Vector3 size, Vector3 center, Matrix4x4 transform,
        string view = "front", int width = 1280, int height = 720, float fieldOfView = 60, float margin = 1.1f)
    {
        if (view is not ("front" or "rear")) throw Invalid("--view must be front or rear.");
        if (width is < 64 or > 8192 || height is < 64 or > 8192 || !float.IsFinite(margin) || margin is < 1 or > 3)
            throw Invalid("Width/height must be 64..8192 and margin must be 1..3.");
        if (size.X <= 0 || size.Y <= 0 || Math.Abs(size.Z) > .0001f) throw Invalid("Expected a positive flat XY rectangle.");
        var target = Vector3.Transform(center, transform);
        var x = Vector3.TransformNormal(Vector3.UnitX, transform);
        var y = Vector3.TransformNormal(Vector3.UnitY, transform);
        var normal = Vector3.Normalize(Vector3.Cross(x, y));
        var corners = new[] { new Vector3(-1,-1,0), new Vector3(1,-1,0), new Vector3(1,1,0), new Vector3(-1,1,0) }
            .Select(sign => Vector3.Transform(center + sign * size / 2, transform)).ToArray();
        if (!Finite(target) || !Finite(normal) || corners.Any(c => !Finite(c))) throw Invalid("Canvas geometry is degenerate or non-finite.");
        // UIX front is local -Z. Match LiveCaptureService's world-up camera orientation, including rolled panels.
        var toward = view == "front" ? normal : -normal;
        var orientation = LiveCaptureService.CameraRotation(new(Array(target - toward), Array(target), fieldOfView));
        var q = new Quaternion(orientation.X, orientation.Y, orientation.Z, orientation.W);
        var right = Vector3.Transform(Vector3.UnitX, q);
        var up = Vector3.Transform(Vector3.UnitY, q);
        var tanV = MathF.Tan(fieldOfView * MathF.PI / 360);
        var tanH = tanV * width / height;
        var distance = corners.Max(c => Math.Max(Math.Abs(Vector3.Dot(c - target, right)) / tanH,
            Math.Abs(Vector3.Dot(c - target, up)) / tanV)) * margin;
        // Keep small targets away from the camera near plane.
        distance = Math.Max(.1f, distance);
        if (!float.IsFinite(distance)) throw Invalid("Computed camera distance is non-finite.");
        var camera = new ApplyCameraSpec(Array(target - toward * distance), Array(target), fieldOfView, width, height);
        return new(slotId, view, "live Canvas.Collider -> BoxCollider.Size/Offset", Array(size), Array(center), corners.Select(Array).ToArray(), camera, margin);
    }

    private static float[] Array(Vector3 v) => [v.X, v.Y, v.Z];
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static Vector3 Vector(JsonNode? node)
    {
        try
        {
            float Part(int i, string key) => node is JsonArray a ? a[i]!.GetValue<float>() : node![key]!.GetValue<float>();
            var value = new Vector3(Part(0, "x"), Part(1, "y"), Part(2, "z"));
            if (!Finite(value)) throw Invalid("Non-finite Canvas collider values.");
            return value;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or ArgumentOutOfRangeException or FormatException)
        { throw Invalid("Canvas collider Size/Offset must expose three finite numbers."); }
    }
    private static RLoopException Invalid(string message) => new("CAPTURE_FRAME_UNSUPPORTED", message, ExitCodes.ValidationFailed);
}
