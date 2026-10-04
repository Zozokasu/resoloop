using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RLoop.Core;

/// <summary>Expands the reusable authoring syntax into the deliberately small schema-v1 apply model.</summary>
public static class ApplyDocumentCompiler
{
    private const int MaxFiles = 64;
    private const int MaxExpandedNodes = 10_000;
    private const int MaxExpandedBytes = 10 * 1024 * 1024;

    public sealed record Result(string Json, ApplyCompilationSummary Summary);

    public static Result Compile(string path)
    {
        var context = new Context();
        var root = LoadMerged(Path.GetFullPath(path), context, []);
        return CompileRoot(root, context);
    }

    internal static Result CompileBundleIr(string text, ApplyBuildBundle? bundle = null)
    {
        var root = JsonNode.Parse(text) as JsonObject ?? throw new JsonException("The root must be an object.");
        if (root.ContainsKey("include")) ApplyBuildBundle.Fail("inputUnknown", "Bundle IR cannot read external includes.");
        return CompileRoot(root, new Context { Bundle = bundle });
    }

    private static Result CompileRoot(JsonObject root, Context context)
    {
        if (root.Remove("limits", out var limits))
        {
            if (limits is not JsonObject settings || settings.Any(pair => pair.Key != "expandedNodes") ||
                settings["expandedNodes"] is not JsonValue limit || !limit.TryGetValue<int>(out var value) || value is < 1 or > 250_000)
                Fail("APPLY_LIMITS_INVALID", "limits must contain only expandedNodes, an integer from 1 to 250000. The default is 10000 JSON nodes.");
            context.NodeLimit = limits!["expandedNodes"]!.GetValue<int>();
        }
        var variables = ReadObject(root["parameters"] as JsonObject);
        MergeVariables(variables, root["variables"] as JsonObject, "variables");
        var prototypes = root["prototypes"] as JsonObject ?? new JsonObject();
        context.Prototypes = prototypes.Count;
        root.Remove("include");
        root.Remove("parameters");
        root.Remove("variables");
        root.Remove("prototypes");
        ExpandValue(root, variables, prototypes, context, "$", allowPrototype: false);
        if (root.ContainsKey("$draftKeys"))
            Fail("APPLY_DRAFT_KEY_UNSTABLE", "Draft index-derived keys cannot be validated or applied. Copy the intended effective keys into explicit key props and rebuild without generated keys.");
        ExpandScopes(root);
        ResolveFieldAliases(root, context.Bundle);
        DetectStableKeyConflicts(root);
        context.ExpandedNodes = CountNodes(root);
        if (context.ExpandedNodes > context.NodeLimit)
            throw new RLoopException("APPLY_EXPANDED_NODE_LIMIT", $"Expanded document contains {context.ExpandedNodes} JSON nodes; the limit is {context.NodeLimit}.",
                ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["expandedJsonNodes"] = context.ExpandedNodes, ["limit"] = context.NodeLimit },
                ["JSON nodes include fields and values, not just Slots. Includes share one budget. Review content size before setting limits.expandedNodes (maximum 250000); the 10 MiB byte limit still applies."]);
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var bytes = System.Text.Encoding.UTF8.GetByteCount(json);
        if (bytes > MaxExpandedBytes)
            Fail("APPLY_EXPANDED_SIZE_LIMIT", $"Expanded document is {bytes} bytes; the limit is {MaxExpandedBytes}.");
        return new Result(json, new ApplyCompilationSummary(context.Files.Count, context.Prototypes,
            context.Instances, context.Repeated, context.ExpandedNodes, bytes, context.NodeLimit));
    }

    private static JsonObject LoadMerged(string path, Context context, IReadOnlyList<string> stack)
    {
        if (stack.Contains(path, StringComparer.OrdinalIgnoreCase))
            Fail("APPLY_INCLUDE_CYCLE", $"Include cycle detected: {string.Join(" -> ", stack.Append(path))}");
        if (!File.Exists(path)) Fail("APPLY_INCLUDE_NOT_FOUND", $"Included apply file '{path}' does not exist.");
        context.Files.Add(path);
        if (context.Files.Count > MaxFiles) Fail("APPLY_INCLUDE_LIMIT", $"An apply document may expand at most {MaxFiles} source files.");
        JsonObject current;
        try
        {
            current = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                      ?? throw new JsonException("The root must be an object.");
        }
        catch (JsonException ex)
        {
            throw new RLoopException("APPLY_DOCUMENT_INVALID", $"Invalid apply document '{path}': {ex.Message}",
                ExitCodes.ValidationFailed, innerException: ex);
        }

        var merged = new JsonObject();
        var includes = current["include"] switch
        {
            JsonValue value when value.TryGetValue<string>(out var single) => new[] { single },
            JsonArray array => array.Select(x => x?.GetValue<string>() ?? string.Empty).ToArray(),
            null => [],
            _ => throw new RLoopException("APPLY_INCLUDE_INVALID", "include must be a path or array of paths.", ExitCodes.ValidationFailed)
        };
        foreach (var include in includes)
        {
            var resolved = Path.GetFullPath(include, Path.GetDirectoryName(path)!);
            Merge(merged, LoadMerged(resolved, context, stack.Append(path).ToArray()), includeLayer: true);
        }
        current.Remove("include");
        Merge(merged, current, includeLayer: false);
        return merged;
    }

    private static void Merge(JsonObject target, JsonObject source, bool includeLayer)
    {
        foreach (var property in source)
        {
            if (property.Value is null) { target[property.Key] = null; continue; }
            if (target[property.Key] is JsonObject targetObject && property.Value is JsonObject sourceObject)
            {
                if (property.Key is "prototypes" or "assets" or "cameras" or "parameters" or "variables")
                {
                    foreach (var named in sourceObject)
                    {
                        if (targetObject.ContainsKey(named.Key))
                            Fail("APPLY_INCLUDE_KEY_CONFLICT", $"Multiple source files declare '{property.Key}.{named.Key}'.");
                        targetObject[named.Key] = named.Value?.DeepClone();
                    }
                    continue;
                }
                Merge(targetObject, sourceObject, includeLayer);
                continue;
            }
            if (target[property.Key] is JsonArray targetArray && property.Value is JsonArray sourceArray &&
                property.Key is "children" or "components" or "tests")
            {
                foreach (var item in sourceArray) targetArray.Add(item?.DeepClone());
                continue;
            }
            target[property.Key] = property.Value.DeepClone();
        }
    }

    private static Dictionary<string, JsonNode?> ReadObject(JsonObject? source) =>
        source?.ToDictionary(x => x.Key, x => x.Value?.DeepClone(), StringComparer.Ordinal)
        ?? new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

    private static void MergeVariables(Dictionary<string, JsonNode?> target, JsonObject? source, string label)
    {
        if (source is null) return;
        foreach (var pair in source)
            if (!target.TryAdd(pair.Key, pair.Value?.DeepClone()))
                Fail("APPLY_PARAMETER_CONFLICT", $"{label} '{pair.Key}' is already declared.");
    }

    private static void ExpandValue(JsonNode node, IReadOnlyDictionary<string, JsonNode?> variables,
        JsonObject prototypes, Context context, string path, bool allowPrototype = true)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(x => x.Key).ToArray())
            {
                // Child instances own their parameter/repeat scope. Do not expand them twice
                // or substitute their parameters before Instantiate has established that scope.
                if (key == "children" && obj[key] is JsonArray children)
                {
                    ExpandNodeArray(children, variables, prototypes, context, path + ".children");
                    continue;
                }
                if (obj[key] is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                    obj[key] = Substitute(text, variables, path + "." + key);
                else if (obj[key] is { } child)
                    ExpandValue(child, variables, prototypes, context, path + "." + key);
            }
            return;
        }
        if (node is JsonArray values)
            for (var i = 0; i < values.Count; i++)
                if (values[i] is JsonValue value && value.TryGetValue<string>(out var text)) values[i] = Substitute(text, variables, path + $"[{i}]");
                else if (values[i] is { } child) ExpandValue(child, variables, prototypes, context, path + $"[{i}]");
    }

    private static void ExpandNodeArray(JsonArray array, IReadOnlyDictionary<string, JsonNode?> variables,
        JsonObject prototypes, Context context, string path)
    {
        var output = new JsonArray();
        for (var i = 0; i < array.Count; i++)
        {
            var source = array[i] as JsonObject ?? throw new RLoopException("APPLY_NODE_INVALID", $"{path}[{i}] must be an object.", ExitCodes.ValidationFailed);
            var repeat = source["$repeat"]?.DeepClone() as JsonObject;
            if (repeat is not null) SubstituteTree(repeat, variables, path + $"[{i}].$repeat");
            var count = repeat?["count"]?.GetValue<int>() ?? 1;
            if (count < 0 || count > MaxExpandedNodes) Fail("APPLY_REPEAT_LIMIT", $"{path}[{i}] repeat count is outside 0..{MaxExpandedNodes}.");
            var variable = repeat?["as"]?.GetValue<string>() ?? "index";
            var offset = repeat?["offset"] is JsonArray offsetArray
                ? offsetArray.Select(x => x?.GetValue<float>() ?? 0).ToArray() : null;
            if (offset is { Length: not 3 }) Fail("APPLY_REPEAT_OFFSET_INVALID", $"{path}[{i}].$repeat.offset requires three numbers.");
            for (var repeatIndex = 0; repeatIndex < count; repeatIndex++)
            {
                var scoped = new Dictionary<string, JsonNode?>(variables, StringComparer.Ordinal)
                {
                    [variable] = JsonValue.Create(repeatIndex)
                };
                var node = Instantiate(source, scoped, prototypes, context, path + $"[{i}]");
                node.Remove("$repeat");
                if (offset is not null && node["slot"] is JsonObject slot)
                {
                    var position = slot["position"] as JsonArray ?? new JsonArray(0, 0, 0);
                    while (position.Count < 3) position.Add(0);
                    for (var axis = 0; axis < 3; axis++)
                        position[axis] = (position[axis]?.GetValue<float>() ?? 0) + offset[axis] * repeatIndex;
                    slot["position"] = position;
                }
                ExpandValue(node, scoped, prototypes, context, path + $"[{i}:{repeatIndex}]");
                output.Add(node);
                if (count > 1) context.Repeated++;
            }
        }
        array.Clear();
        foreach (var item in output) array.Add(item?.DeepClone());
    }

    private static JsonObject Instantiate(JsonObject source, IReadOnlyDictionary<string, JsonNode?> variables,
        JsonObject prototypes, Context context, string path)
    {
        if (source.ContainsKey("$recipe"))
        {
            if (source.ContainsKey("$prototype") || source.ContainsKey("$instance"))
                Fail("APPLY_RECIPE_INVALID", path + " cannot combine $recipe with $prototype/$instance.");
            if (source["$recipe"] is not JsonValue recipeValue || !recipeValue.TryGetValue<string>(out var recipeName))
                Fail("APPLY_RECIPE_INVALID", path + ".$recipe must be a recipe name.");
            var recipe = UixRecipes.Describe(source["$recipe"]!.GetValue<string>());
            if (source["$with"] is not JsonObject suppliedRecipe)
                Fail("APPLY_RECIPE_INVALID", path + " requires $with containing the recipe parameters.");
            var parameters = (JsonObject)source["$with"]!.DeepClone();
            SubstituteTree(parameters, variables, path + ".$with");
            var missing = recipe.Parameters.Keys.Where(key => !parameters.ContainsKey(key)).ToArray();
            var unknown = parameters.Select(pair => pair.Key).Except(recipe.Parameters.Keys).ToArray();
            if (missing.Length > 0 || unknown.Length > 0)
                Fail("APPLY_RECIPE_PARAMETERS", $"{path} recipe '{recipe.Name}': missing [{string.Join(", ", missing)}]; unknown [{string.Join(", ", unknown)}].");
            if (parameters["key"] is not JsonValue keyValue || !keyValue.TryGetValue<string>(out var localKey) ||
                string.IsNullOrWhiteSpace(localKey) || localKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
                Fail("APPLY_RECIPE_KEY_INVALID", path + ".$with.key must use ASCII letters, digits, '-' or '_'.");
            parameters["key"] = $"uix-{recipe.Name}--{parameters["key"]!.GetValue<string>()}";
            var recipeScope = new Dictionary<string, JsonNode?>(variables, StringComparer.Ordinal);
            foreach (var pair in parameters) recipeScope[pair.Key] = pair.Value?.DeepClone();
            var builtIn = (JsonObject)JsonNode.Parse(UixRecipes.Read(recipe.Name))!["prototypes"]![recipe.Prototype]!.DeepClone();
            SubstituteTree(builtIn, recipeScope, path);
            var recipeOverrides = (JsonObject)source.DeepClone();
            recipeOverrides.Remove("$recipe"); recipeOverrides.Remove("$with");
            // Overrides remain caller-owned, including keys and all visual fields.
            Merge(builtIn, recipeOverrides, includeLayer: false);
            context.Instances++;
            return builtIn;
        }
        var prototypeName = source["$prototype"]?.GetValue<string>() ?? source["$instance"]?.GetValue<string>();
        if (prototypeName is null) return (JsonObject)source.DeepClone();
        var prototype = prototypes[prototypeName] as JsonObject ?? throw new RLoopException(
            "APPLY_PROTOTYPE_NOT_FOUND", $"{path} references unknown prototype '{prototypeName}'.", ExitCodes.ValidationFailed);
        var scoped = new Dictionary<string, JsonNode?>(variables, StringComparer.Ordinal);
        if (source["$with"] is JsonObject supplied)
            foreach (var pair in supplied) scoped[pair.Key] = pair.Value?.DeepClone();
        var instance = (JsonObject)prototype.DeepClone();
        var overrides = (JsonObject)source.DeepClone();
        overrides.Remove("$prototype"); overrides.Remove("$instance"); overrides.Remove("$with");
        Merge(instance, overrides, includeLayer: false);
        SubstituteTree(instance, scoped, path);
        context.Instances++;
        return instance;
    }

    private static void SubstituteTree(JsonNode node, IReadOnlyDictionary<string, JsonNode?> variables, string path)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(x => x.Key).ToArray())
            {
                if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text)) obj[key] = Substitute(text, variables, path);
                else if (obj[key] is { } child) SubstituteTree(child, variables, path);
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue arrayValue && arrayValue.TryGetValue<string>(out var arrayText)) array[i] = Substitute(arrayText, variables, path);
                else if (array[i] is { } arrayChild) SubstituteTree(arrayChild, variables, path);
            }
        }
    }

    private static JsonNode? Substitute(string text, IReadOnlyDictionary<string, JsonNode?> variables, string path)
    {
        if (text.StartsWith("${", StringComparison.Ordinal) && text.EndsWith('}') && text.Count(x => x == '$') == 1)
        {
            var name = text[2..^1];
            if (!variables.TryGetValue(name, out var exact)) Fail("APPLY_PARAMETER_NOT_FOUND", $"{path} references unknown parameter '{name}'.");
            return exact?.DeepClone();
        }
        var result = text;
        foreach (var pair in variables)
        {
            var token = "${" + pair.Key + "}";
            if (!result.Contains(token, StringComparison.Ordinal)) continue;
            var replacement = pair.Value switch
            {
                null => string.Empty,
                JsonValue value when value.TryGetValue<string>(out var s) => s,
                JsonValue value => value.ToJsonString().Trim('"'),
                _ => pair.Value.ToJsonString()
            };
            result = result.Replace(token, replacement, StringComparison.Ordinal);
        }
        if (result.Contains("${", StringComparison.Ordinal)) Fail("APPLY_PARAMETER_NOT_FOUND", $"{path} contains an unresolved parameter in '{result}'.");
        return JsonValue.Create(result);
    }

    private static void ExpandScopes(JsonObject root)
    {
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        void Rewrite(JsonNode? node, string scope)
        {
            if (node is JsonObject obj)
                foreach (var key in obj.Select(pair => pair.Key).ToArray())
                {
                    if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text))
                        obj[key] = QualifySelector(scope, text);
                    else Rewrite(obj[key], scope);
                }
            else if (node is JsonArray array)
                for (var i = 0; i < array.Count; i++)
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var text))
                        array[i] = QualifySelector(scope, text);
                    else Rewrite(array[i], scope);
        }
        void Key(JsonObject spec, string scope, string path, string prefix)
        {
            if (scope.Length == 0) return;
            var local = spec["key"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            spec["key"] = StableKeyScope.QualifyLocal(scope, local, path + ".key");
            if (spec["migrateFrom"] is JsonValue migration && migration.TryGetValue<string>(out var previous))
                spec["migrateFrom"] = StableKeyScope.ResolveSelector(scope, prefix + previous)[prefix.Length..];
        }
        void Visit(JsonObject node, string scope, string path)
        {
            if (node.Remove("$scope", out var declaration))
            {
                var instance = declaration is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
                scope = StableKeyScope.QualifyLocal(scope, instance, path + ".$scope");
                if (!scopes.Add(scope)) Fail("APPLY_EXPANDED_KEY_CONFLICT", $"Instance scope '{scope}' is repeated at {path}.");
            }
            if (node["slot"] is JsonObject slot) Key(slot, scope, path + ".slot", "$slot:");
            if (node["components"] is JsonArray components)
                for (var i = 0; i < components.Count; i++)
                    if (components[i] is JsonObject component)
                    {
                        Key(component, scope, path + $".components[{i}]", "$component:");
                        if (component["fieldAliases"] is JsonObject aliases && scope.Length > 0)
                        {
                            var qualified = new JsonObject();
                            foreach (var alias in aliases)
                                qualified[StableKeyScope.QualifyLocal(scope, alias.Key, path + $".components[{i}].fieldAliases")] = alias.Value?.DeepClone();
                            component["fieldAliases"] = qualified;
                        }
                        Rewrite(component["fields"], scope);
                        Rewrite(component["initialFields"], scope);
                    }
            if (node["children"] is JsonArray children)
                for (var i = 0; i < children.Count; i++)
                    if (children[i] is JsonObject child) Visit(child, scope, path + $".children[{i}]");
        }
        Visit(root, "", "$");
    }

    private static void DetectStableKeyConflicts(JsonObject root)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string? key, string path)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (!keys.TryAdd(key, path)) Fail("APPLY_EXPANDED_KEY_CONFLICT", $"Stable key '{key}' is used by both '{keys[key]}' and '{path}'.");
        }
        void Visit(JsonObject node, string path)
        {
            if (node["slot"] is JsonObject slot) Add(slot["key"]?.GetValue<string>(), path + ".slot.key");
            if (node["components"] is JsonArray components)
                for (var i = 0; i < components.Count; i++) if (components[i] is JsonObject component) Add(component["key"]?.GetValue<string>(), path + $".components[{i}].key");
            if (node["children"] is JsonArray children)
                for (var i = 0; i < children.Count; i++) if (children[i] is JsonObject child) Visit(child, path + $".children[{i}]");
        }
        Visit(root, "$");
    }

    private static string QualifySelector(string scope, string text) =>
        scope.Length > 0 && text.StartsWith("$field:", StringComparison.Ordinal) && !text[7..].Contains("::", StringComparison.Ordinal)
            ? "$field:" + scope + "::" + text[7..] : StableKeyScope.ResolveSelector(scope, text);

    private static void ResolveFieldAliases(JsonObject root, ApplyBuildBundle? bundle)
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var components = new List<(JsonObject Spec, ApplyIssuePath Path)>();
        void FailAlias(string code, string message, ApplyIssuePath path, string? key, string? member, bool value = false, string? originalValue = null)
        {
            var entry = bundle?.FindEntry(path.Segments, "component", key, member);
            if (originalValue is not null && bundle?.MatchesOriginalString(path.Segments, originalValue) != true) entry = null;
            var source = (value ? entry?.ValueSource : entry?.Source) ?? ApplyDiagnosticSource.Unknown;
            var error = new RLoopException(code, message, ExitCodes.ValidationFailed,
                new Dictionary<string, object?> { ["jsonPath"] = path.JsonPath });
            var diagnostic = ApplyDiagnostics.Unknown(code, message, "compile", buildId: bundle?.BuildId);
            diagnostic = diagnostic with
            {
                EntityKind = "component", Key = key, Member = member, JsonPath = path.JsonPath,
                PathSegments = path.Segments, Source = source, Related = entry?.Related ?? [],
                Completeness = new Dictionary<string, string>(diagnostic.Completeness)
                { ["location"] = source.Status == "known" ? "complete" : "unknown" }
            };
            ApplyDiagnostics.AttachRuntime(error, [diagnostic]);
            throw error;
        }
        void Visit(JsonObject node, ApplyIssuePath path)
        {
            if (node["components"] is JsonArray list)
                for (var i = 0; i < list.Count; i++)
                    if (list[i] is JsonObject spec) components.Add((spec, path.Property("components").Index(i)));
            if (node["children"] is JsonArray children)
                for (var i = 0; i < children.Count; i++)
                    if (children[i] is JsonObject child) Visit(child, path.Property("children").Index(i));
        }
        Visit(root, ApplyIssuePath.Root);
        foreach (var (spec, path) in components)
        {
            var key = spec["key"] is JsonValue k && k.TryGetValue<string>(out var s) ? s : null;
            if (spec["fieldAliases"] is null) continue;
            if (spec["fieldAliases"] is not JsonObject declared)
                FailAlias("APPLY_FIELD_ALIAS_INVALID", "fieldAliases must be an object mapping aliases to member names.", path.Property("fieldAliases"), key, null);
            foreach (var alias in (JsonObject)spec["fieldAliases"]!)
            {
                var member = alias.Value is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
                var p = path.Property("fieldAliases").Property(alias.Key);
                if (string.IsNullOrWhiteSpace(alias.Key) || string.IsNullOrWhiteSpace(member) || string.IsNullOrWhiteSpace(key))
                    FailAlias("APPLY_FIELD_ALIAS_INVALID", "A field alias requires a non-empty alias, member name and explicit Component key.", p, key, alias.Key);
                if ((spec["fields"] as JsonObject)?.ContainsKey(member!) != true &&
                    (spec["initialFields"] as JsonObject)?.ContainsKey(member!) != true &&
                    (spec["propertyModes"] as JsonObject)?.ContainsKey(member!) != true)
                    FailAlias("APPLY_FIELD_ALIAS_MEMBER_UNDECLARED", $"Field alias '{alias.Key}' names undeclared member '{member}'.", p, key, alias.Key, originalValue: member);
                if (!aliases.TryAdd(alias.Key, "$member:" + key + "." + member))
                    FailAlias("APPLY_FIELD_ALIAS_DUPLICATE", $"Field alias '{alias.Key}' is declared more than once.", p, key, alias.Key, originalValue: member);
            }
        }
        void Rewrite(JsonNode? node, ApplyIssuePath path, string? key, string member)
        {
            if (node is JsonObject obj)
                foreach (var name in obj.Select(p => p.Key).ToArray())
                {
                    if (obj[name] is JsonValue v && v.TryGetValue<string>(out var text)) obj[name] = Resolve(text, path.Property(name), key, member);
                    else Rewrite(obj[name], path.Property(name), key, member);
                }
            else if (node is JsonArray array)
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue v && v.TryGetValue<string>(out var text)) array[i] = Resolve(text, path.Index(i), key, member);
                    else Rewrite(array[i], path.Index(i), key, member);
                }
        }
        string Resolve(string text, ApplyIssuePath path, string? key, string member)
        {
            if (!text.StartsWith("$field:", StringComparison.Ordinal)) return text;
            if (!aliases.TryGetValue(text[7..], out var selector))
                FailAlias("APPLY_FIELD_ALIAS_NOT_FOUND", $"Field alias '{text[7..]}' is not declared.", path, key, member, value: true, originalValue: text);
            bundle?.RecordLoweredSelector(path.Segments, text, selector!);
            return selector!;
        }
        foreach (var (spec, path) in components)
            foreach (var section in new[] { "fields", "initialFields" })
                if (spec[section] is JsonObject fields)
                    foreach (var member in fields.Select(p => p.Key).ToArray())
                    {
                        var p = path.Property(section).Property(member);
                        var key = spec["key"]?.GetValue<string>();
                        if (fields[member] is JsonValue v && v.TryGetValue<string>(out var text)) fields[member] = Resolve(text, p, key, member);
                        else Rewrite(fields[member], p, key, member);
                    }
        if (root["tests"] is JsonArray tests)
            for (var i = 0; i < tests.Count; i++)
                if (tests[i] is JsonObject test)
                {
                    var path = ApplyIssuePath.Root.Property("tests").Index(i);
                    void Target(JsonObject target, ApplyIssuePath p)
                    {
                        if (target["target"] is JsonValue v && v.TryGetValue<string>(out var text))
                            target["target"] = Resolve(text, p.Property("target"), null, "target");
                    }
                    if (test["assertions"] is JsonArray assertions)
                        for (var j = 0; j < assertions.Count; j++)
                            if (assertions[j] is JsonObject assertion) Target(assertion, path.Property("assertions").Index(j));
                    if (test["probe"] is JsonObject probe)
                    {
                        var p = path.Property("probe");
                        Target(probe, p);
                        if (probe["values"] is JsonObject values)
                        {
                            var resolved = new JsonObject();
                            foreach (var value in values)
                            {
                                var selector = Resolve(value.Key, p.Property("values").Property(value.Key), null, value.Key);
                                if (resolved.ContainsKey(selector))
                                    FailAlias("APPLY_FIELD_DUPLICATE", $"Probe member '{selector}' is declared more than once.", p.Property("values").Property(value.Key), null, value.Key);
                                resolved[selector] = value.Value?.DeepClone();
                            }
                            probe["values"] = resolved;
                        }
                    }
                }
    }

    private static int CountNodes(JsonNode node) => node switch
    {
        JsonObject obj => 1 + obj.Sum(x => x.Value is null ? 0 : CountNodes(x.Value)),
        JsonArray array => 1 + array.Sum(x => x is null ? 0 : CountNodes(x)),
        _ => 1
    };

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string code, string message) => throw new RLoopException(code, message, ExitCodes.ValidationFailed);

    private sealed class Context
    {
        public ApplyBuildBundle? Bundle { get; init; }
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Prototypes { get; set; }
        public int Instances { get; set; }
        public int Repeated { get; set; }
        public int ExpandedNodes { get; set; }
        public int NodeLimit { get; set; } = MaxExpandedNodes;
    }
}
