using System.Globalization;
using System.Text.Json;

namespace RLoop.Core;

public static class ApplyCatalogValidator
{
    public static async Task<ApplyValidationResult> ValidateFileAsync(ApplyDocument document, string catalogFile,
        CancellationToken cancellationToken = default)
        => (await ValidateFileSnapshotAsync(document, catalogFile, cancellationToken)).Result;

    internal static async Task<(ApplyValidationResult Result, ApplyCatalog? Catalog)> ValidateFileSnapshotAsync(
        ApplyDocument document, string catalogFile, CancellationToken cancellationToken)
    {
        try
        {
            var catalog = ApplyCatalog.Load(catalogFile);
            return (await ApplyDocumentValidator.ValidateAsync(document, cancellationToken: cancellationToken, catalog: catalog), catalog);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            var baseline = await ApplyDocumentValidator.ValidateAsync(document, cancellationToken: cancellationToken);
            return (ApplyDiagnostics.Complete(document, baseline with { Valid = false, Issues = baseline.Issues.Concat(UnavailableIssues(document,
                $"Cannot read catalog '{catalogFile}': {ex.Message}")).ToArray() }), null);
        }
    }

    public static IReadOnlySet<string> UsedTypes(ApplyDocument document, ApplyCatalog catalog) =>
        Components(document).Select(c => catalog.Find(c.Spec.Type)?.FullName ?? c.Spec.Type).ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<(ApplyComponentSpec Spec, ApplyIssuePath Path)> Components(ApplyDocument document)
    {
        IEnumerable<(ApplyComponentSpec, ApplyIssuePath)> Walk(IReadOnlyList<ApplyComponentSpec>? components, IReadOnlyList<ApplyNodeSpec>? children, ApplyIssuePath path)
        {
            for (var i = 0; i < (components?.Count ?? 0); i++) if (components![i] is { } component) yield return (component, path.Property("components").Index(i));
            for (var i = 0; i < (children?.Count ?? 0); i++) if (children![i] is { } child)
                foreach (var component in Walk(child.Components, child.Children, path.Property("children").Index(i))) yield return component;
        }
        return Walk(document.Components, document.Children, ApplyIssuePath.Root);
    }

    public static IReadOnlyList<ApplyValidationIssue> Validate(ApplyDocument document, ApplyCatalog catalog)
    {
        var issues = new List<ApplyValidationIssue>();
        var components = Components(document).ToArray();
        string? currentKey = null; string? currentMember = null;
        var expectedEvidence = ApplyDiagnosticValue.Unknown;
        var observedEvidence = ApplyDiagnosticValue.Unknown;
        void Add(string code, string message, ApplyIssuePath path, object? expected = null, bool expectedKnown = false, object? observed = null, bool observedKnown = false) =>
            ApplyDiagnostics.Add(issues, code, message, path, currentKey, currentMember,
                expectedKnown ? expected : expectedEvidence.Value, expectedKnown || expectedEvidence.Status == "known",
                observedKnown ? observed : observedEvidence.Value, observedKnown || observedEvidence.Status == "known");
        var keys = components.Where(c => !string.IsNullOrEmpty(c.Spec.Key)).GroupBy(c => c.Spec.Key!)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Spec, StringComparer.Ordinal);
        void Unavailable(string type, string member, ApplyIssuePath path, string reason) =>
            Add("APPLY_CATALOG_UNAVAILABLE", $"Type '{type}', member '{member}': {reason}", path);
        var unavailable = catalog.UnavailableReason();
        if (unavailable is not null)
        {
            foreach (var (component, path) in components)
            {
                currentKey = component.Key; currentMember = null;
                expectedEvidence = ApplyDiagnosticValue.Unknown; observedEvidence = ApplyDiagnosticValue.Unknown;
                Unavailable(component.Type, "(type)", path.Property("type"), unavailable);
                foreach (var (field, valuePath) in Fields(component, path))
                { currentMember = field.Key; observedEvidence = ApplyDiagnosticValue.Known(field.Value.Clone()); Unavailable(component.Type, field.Key, valuePath, unavailable); }
            }
            if (issues.Count == 0) Unavailable("(catalog)", "(identity)", ApplyIssuePath.Root, unavailable);
            return issues;
        }

        void Conversion(string type, string member, ApplyIssuePath path) => Add("VALUE_CONVERSION_FAILED",
            $"Cannot convert type '{type}', member '{member}' to a finite Single within its representable range.", path);

        void Value(string? typeName, JsonElement value, string owner, string member, ApplyIssuePath path, HashSet<string> active)
        {
            var type = catalog.Find(typeName);
            observedEvidence = ApplyDiagnosticValue.Known(value.Clone());
            expectedEvidence = type is null ? ApplyDiagnosticValue.Unknown : ApplyDiagnosticValue.Known(type);
            if (type is null || !active.Add(type.FullName))
            { Unavailable(owner, member, path, $"Value type '{typeName}' is missing, unconfirmed or recursive."); return; }
            try
            {
                switch (type.Representation)
                {
                    case "single":
                        var raw = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
                        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                            !double.IsFinite(number) || number > float.MaxValue || number < -float.MaxValue) Conversion(owner, member, path);
                        break;
                    case "nullable":
                        if (catalog.Find(type.ElementType) is null) { Unavailable(owner, member, path, $"Nullable element '{type.ElementType}' is unconfirmed."); break; }
                        if (value.ValueKind != JsonValueKind.Null && !(value.ValueKind == JsonValueKind.String && value.GetString() == "null"))
                            Value(type.ElementType, value, owner, member, path, active);
                        break;
                    case "tuple":
                        if (catalog.Find(type.ElementType) is null) { Unavailable(owner, member, path, $"Tuple element '{type.ElementType}' is unconfirmed."); break; }
                        if (type.TupleSize is < 2 or > 4) { Unavailable(owner, member, path, $"Tuple definition '{type.FullName}' is incomplete."); break; }
                        if (value.ValueKind == JsonValueKind.String)
                        {
                            var text = value.GetString()!.Trim();
                            if (text.StartsWith('[') || text.StartsWith('{'))
                            {
                                try { using var tuple = JsonDocument.Parse(text); ValueTuple(tuple.RootElement); }
                                catch (JsonException) { Conversion(owner, member, path); }
                            }
                            else
                            {
                                var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                                if (parts.Length != type.TupleSize) Conversion(owner, member, path);
                                else for (var i = 0; i < parts.Length; i++) Value(type.ElementType, JsonSerializer.SerializeToElement(parts[i]), owner, member, path.Index(i), active);
                            }
                        }
                        else ValueTuple(value);
                        void ValueTuple(JsonElement tuple)
                        {
                            if (tuple.ValueKind == JsonValueKind.Array && tuple.GetArrayLength() == type.TupleSize)
                            { var i = 0; foreach (var item in tuple.EnumerateArray()) Value(type.ElementType, item, owner, member, path.Index(i++), active); }
                            else if (tuple.ValueKind == JsonValueKind.Object)
                            {
                                var axes = tuple.TryGetProperty("x", out _) ? new[] { "x", "y", "z", "w" } : new[] { "r", "g", "b", "a" };
                                foreach (var axis in axes.Take(type.TupleSize))
                                    if (tuple.TryGetProperty(axis, out var item)) Value(type.ElementType, item, owner, member, path.Property(axis), active);
                                    else { observedEvidence = ApplyDiagnosticValue.Unknown; Conversion(owner, member, path.Property(axis)); }
                            }
                            else Conversion(owner, member, path);
                        }
                        break;
                    case "enum":
                        if (type.EnumValues is null || type.IsFlags is null)
                        { Unavailable(owner, member, path, $"Enum '{type.FullName}' values/flags were not acquired."); break; }
                        var enumRaw = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
                        var names = enumRaw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                        if (names.Length == 0 || names.Any(n => !type.EnumValues.ContainsKey(n) && !long.TryParse(n, out _)))
                            Add("ENUM_VALUE_INVALID", $"Type '{owner}', member '{member}': invalid value for '{type.FullName}'.", path);
                        else if (!type.IsFlags.Value && names.Length > 1)
                            Add("ENUM_FLAGS_INVALID", $"Type '{owner}', member '{member}': '{type.FullName}' accepts one enum value.", path);
                        break;
                    case "other": break;
                    default: Unavailable(owner, member, path, $"Value representation '{type.Representation}' is unconfirmed."); break;
                }
            }
            finally { active.Remove(type.FullName); }
        }

        // True = proven assignable, false = proven incompatible, null = insufficient evidence.
        bool? Assignable(string? sourceName, string? targetName)
        {
            var source = catalog.Find(sourceName); var target = catalog.Find(targetName);
            if (source is null || target is null) return null;
            var visited = new HashSet<string>(StringComparer.Ordinal); var active = new HashSet<string>(StringComparer.Ordinal);
            var complete = true; var matched = false;
            void Walk(CatalogType type)
            {
                if (active.Contains(type.FullName)) { complete = false; return; }
                if (!visited.Add(type.FullName)) return;
                if (type.FullName == target.FullName) matched = true;
                if (!type.ClosureComplete || type.IsGeneric) complete = false;
                active.Add(type.FullName);
                foreach (var parent in (type.BaseType is null ? Array.Empty<string>() : new[] { type.BaseType }).Concat(type.Interfaces))
                { var next = catalog.Find(parent); if (next is null) complete = false; else Walk(next); }
                active.Remove(type.FullName);
            }
            // Exact confirmed identity needs no inheritance inference.
            if (source.FullName == target.FullName) return true;
            Walk(source);
            return matched ? true : complete && !target.IsGeneric ? false : null;
        }

        string? SourceType(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.String || !StableSelectorSyntax.TryParse(value.GetString()!, out var selector)) return null;
            if (selector!.Kind == "component" && keys.TryGetValue(selector.Key, out var component)) return component.Type;
            if (selector.Kind == "member" && keys.TryGetValue(selector.Key, out component))
            {
                var sourceMember = catalog.Find(component.Type)?.Members?.GetValueOrDefault(selector.MemberName!);
                return sourceMember is { Confirmed: true } ? sourceMember.MemberType : null;
            }
            // Slots, assets, external IDs and Slot members require actual evidence unavailable in this IR.
            return null;
        }

        void Member(CatalogMember descriptor, JsonElement value, string owner, string name, ApplyIssuePath path, int depth)
        {
            expectedEvidence = descriptor.Confirmed ? ApplyDiagnosticValue.Known(descriptor) : ApplyDiagnosticValue.Unknown;
            observedEvidence = ApplyDiagnosticValue.Known(value.Clone());
            if (!descriptor.Confirmed || depth > 64) { Unavailable(owner, name, path, "Member definition is unconfirmed or exceeds depth bound."); return; }
            if (descriptor.Kind is "list" or "array" or "dictionary" or "syncObject" && value.ValueKind == JsonValueKind.String)
            {
                var raw = value.GetString()!.Trim();
                if (raw.StartsWith('[') || raw.StartsWith('{'))
                {
                    try { using var structured = JsonDocument.Parse(raw); Member(descriptor, structured.RootElement, owner, name, path, depth + 1); }
                    catch (JsonException) { Add("VALUE_CONVERSION_FAILED", $"Type '{owner}', member '{name}': malformed structured value.", path); }
                    return;
                }
            }
            switch (descriptor.Kind)
            {
                case "field": Value(descriptor.ValueType, value, owner, name, path, []); break;
                case "reference":
                    if (catalog.Find(descriptor.TargetType) is null) { Unavailable(owner, name, path, $"Target type '{descriptor.TargetType}' is unconfirmed."); break; }
                    if (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.String && value.GetString() == "null") break;
                    if (value.ValueKind == JsonValueKind.String && StableSelectorSyntax.TryParse(value.GetString()!, out var selected) &&
                        selected!.Kind == "member" && keys.TryGetValue(selected.Key, out var selectedComponent) &&
                        catalog.Find(selectedComponent.Type) is { MembersComplete: true } selectedType &&
                        selectedType.Members?.ContainsKey(selected.MemberName!) != true)
                    {
                        Add("APPLY_MEMBER_REFERENCE_NOT_FOUND", $"Member reference '{value.GetString()}' was not found.", path,
                            selectedType.Members?.Keys.ToArray(), true, value.Clone(), true);
                        break;
                    }
                    var source = SourceType(value); var assignable = Assignable(source, descriptor.TargetType);
                    if (assignable == false) Add("APPLY_REFERENCE_TYPE_MISMATCH", $"Type '{owner}', member '{name}': source '{source}' is incompatible with confirmed target '{descriptor.TargetType}'.", path, descriptor.TargetType, true, source, true);
                    else if (assignable is null) Unavailable(owner, name, path, $"Reference source '{source ?? value.GetRawText()}' or target '{descriptor.TargetType}' has insufficient type closure.");
                    break;
                case "list": case "array": case "dictionary":
                    if (descriptor.Element is null || descriptor.Kind == "dictionary" && catalog.Find(descriptor.KeyType) is null)
                    { Unavailable(owner, name, path, "Element/key definition is missing."); break; }
                    if (!ElementReady(descriptor.Element, depth + 1))
                    { Unavailable(owner, name, path, "Element type definition is missing or unconfirmed."); break; }
                    if (descriptor.Kind == "list" && value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())) break;
                    if (descriptor.Kind == "dictionary" && value.ValueKind == JsonValueKind.Object)
                        foreach (var item in value.EnumerateObject()) Member(descriptor.Element, item.Value, owner, name, path.Property(item.Name), depth + 1);
                    else if (descriptor.Kind != "dictionary" && value.ValueKind == JsonValueKind.Array)
                    { var i = 0; foreach (var item in value.EnumerateArray()) Member(descriptor.Element, item, owner, name, path.Index(i++), depth + 1); }
                    else if (descriptor.Kind == "list") Member(descriptor.Element, value, owner, name, path, depth + 1);
                    else Add("VALUE_CONVERSION_FAILED", $"Type '{owner}', member '{name}' requires a {descriptor.Kind} value.", path);
                    break;
                case "syncObject":
                    var nested = catalog.Find(descriptor.MemberType);
                    if (nested?.Members is null) { Unavailable(owner, name, path, $"SyncObject '{descriptor.MemberType}' definition is missing."); break; }
                    if (value.ValueKind != JsonValueKind.Object) { Add("SYNC_OBJECT_VALUE_INVALID", "SyncObject values must use a JSON object.", path); break; }
                    foreach (var item in value.EnumerateObject()) CheckMember(nested, item.Name, item.Value, path.Property(item.Name), depth + 1);
                    break;
                default: Unavailable(owner, name, path, $"Member kind '{descriptor.Kind}' is unsupported/unconfirmed."); break;
            }
        }

        bool ElementReady(CatalogMember member, int depth)
        {
            if (!member.Confirmed || depth > 64) return false;
            return member.Kind switch
            {
                "field" => catalog.Find(member.ValueType) is not null,
                "reference" => catalog.Find(member.TargetType) is not null,
                "syncObject" => catalog.Find(member.MemberType)?.Members is not null,
                "list" or "array" => member.Element is not null && ElementReady(member.Element, depth + 1),
                "dictionary" => catalog.Find(member.KeyType) is not null && member.Element is not null && ElementReady(member.Element, depth + 1),
                _ => false
            };
        }

        void CheckMember(CatalogType type, string name, JsonElement value, ApplyIssuePath path, int depth)
        {
            expectedEvidence = ApplyDiagnosticValue.Unknown;
            observedEvidence = ApplyDiagnosticValue.Known(value.Clone());
            if (type.Members?.TryGetValue(name, out var descriptor) == true)
            {
                if (descriptor is null) Unavailable(type.FullName, name, path, "Member definition is missing.");
                else Member(descriptor, value, type.FullName, name, path, depth);
            }
            else if (type.MembersComplete) Add("COMPONENT_MEMBER_NOT_FOUND", $"Member '{name}' does not exist on confirmed type '{type.FullName}'.", path, type.Members?.Keys.ToArray(), true, value.Clone(), true);
            else Unavailable(type.FullName, name, path, "Member was outside the acquired scope.");
        }
        foreach (var (component, path) in components)
        {
            currentKey = component.Key; currentMember = null;
            expectedEvidence = ApplyDiagnosticValue.Unknown; observedEvidence = ApplyDiagnosticValue.Unknown;
            var type = catalog.Find(component.Type);
            if (type is null) Unavailable(component.Type, "(type)", path.Property("type"), "Component type is missing or unconfirmed.");
            foreach (var (field, valuePath) in Fields(component, path))
            {
                currentMember = field.Key;
                expectedEvidence = ApplyDiagnosticValue.Unknown; observedEvidence = ApplyDiagnosticValue.Known(field.Value.Clone());
                if (type is null) Unavailable(component.Type, field.Key, valuePath, "Component type is missing or unconfirmed.");
                else CheckMember(type, field.Key, field.Value, valuePath, 0);
            }
        }
        return issues;
    }

    private static IEnumerable<(KeyValuePair<string, JsonElement> Field, ApplyIssuePath Path)> Fields(ApplyComponentSpec component, ApplyIssuePath path)
    {
        foreach (var field in component.Fields ?? new Dictionary<string, JsonElement>()) yield return (field, path.Property("fields").Property(field.Key));
        foreach (var field in component.InitialFields ?? new Dictionary<string, JsonElement>()) yield return (field, path.Property("initialFields").Property(field.Key));
    }

    private static IReadOnlyList<ApplyValidationIssue> UnavailableIssues(ApplyDocument document, string reason)
    {
        var issues = new List<ApplyValidationIssue>();
        foreach (var (component, path) in Components(document))
        {
            ApplyDiagnostics.Add(issues, "APPLY_CATALOG_UNAVAILABLE", $"Type '{component.Type}': {reason}", path.Property("type"), component.Key, null);
            foreach (var (field, valuePath) in Fields(component, path))
                ApplyDiagnostics.Add(issues, "APPLY_CATALOG_UNAVAILABLE", $"Type '{component.Type}', member '{field.Key}': {reason}", valuePath, component.Key, field.Key);
        }
        if (issues.Count == 0) issues.Add(new("APPLY_CATALOG_UNAVAILABLE", reason, "$"));
        return issues;
    }

}
