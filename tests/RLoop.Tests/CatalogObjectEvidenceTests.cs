using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CatalogObjectEvidenceTests
{
    private const string Boolean = "[System.Private.CoreLib]System.Boolean";
    private const string Int32 = "[System.Private.CoreLib]System.Int32";
    private const string String = "[System.Private.CoreLib]System.String";
    private const string Single = "[System.Private.CoreLib]System.Single";

    private static CatalogType Scalar(string name, string representation) =>
        new(name, true, true, null, [], Representation: representation);

    private static string Field(string valueType) => $"[FrooxEngine]FrooxEngine.IField<{valueType}>";

    private static ApplyCatalog Make(IReadOnlyList<CatalogType> types, IReadOnlyDictionary<string, string>? aliases = null,
        bool synthetic = false)
    {
        var content = new CatalogContent(types, aliases ?? new Dictionary<string, string>());
        var identity = new CatalogIdentity("fixture", "fixture", "fixture", ApplyCatalog.CurrentMapperVersion,
            new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero));
        return new(ApplyCatalog.CurrentFormatVersion, identity, identity, "live", synthetic,
            ApplyCatalog.Hash(content), content);
    }

    [Fact]
    public void EmitsStrictReferencesAliasesAndCompactOrdinalMetadata()
    {
        var boolField = Field(Boolean);
        var intField = Field(Int32);
        var stringField = Field(String);
        var singleField = Field(Single);
        var owner = new CatalogType("Fixture.Owner", true, true, null, [], MembersComplete: true,
            Members: new Dictionary<string, CatalogMember>
            {
                ["SingleRef"] = new("reference", TargetType: "Alias.SingleField"),
                ["StringRef"] = new("reference", TargetType: stringField),
                ["IntRef"] = new("reference", TargetType: intField),
                ["BoolRefAgain"] = new("reference", TargetType: boolField),
                ["BoolRef"] = new("reference", TargetType: boolField),
                ["SingleRefDirect"] = new("reference", TargetType: singleField),
            });
        var types = new[]
        {
            Scalar(Boolean, "boolean"), Scalar(Int32, "int32"), Scalar(String, "string"), Scalar(Single, "single"),
            new CatalogType(boolField, true, true, null, [], IsGeneric: true),
            new CatalogType(intField, true, true, null, [], IsGeneric: true),
            new CatalogType(stringField, true, true, null, [], IsGeneric: true),
            new CatalogType(singleField, true, true, null, [], IsGeneric: true), owner,
        };
        var aliases = new Dictionary<string, string>
        {
            ["Alias.SingleField"] = singleField,
            ["Fixture.OwnerAlias"] = owner.FullName,
        };

        var generated = CatalogTypesGenerator.Generate(Make(types, aliases), "custom-runtime.ts");
        foreach (var line in new[]
        {
            "        \"BoolRef\": FieldRef<boolean, \"boolean\"> | UncheckedReference | null;",
            "        \"BoolRefAgain\": FieldRef<boolean, \"boolean\"> | UncheckedReference | null;",
            "        \"IntRef\": FieldRef<number, \"int32\"> | UncheckedReference | null;",
            "        \"StringRef\": FieldRef<string, \"string\"> | UncheckedReference | null;",
            "        \"SingleRef\": FieldRef<number, \"single\"> | UncheckedReference | null;",
            "        \"SingleRefDirect\": FieldRef<number, \"single\"> | UncheckedReference | null;",
        }) Assert.Contains(line, generated.Text);
        Assert.DoesNotContain("| UncheckedReference | string", generated.Text);
        Assert.Contains("memberMetadata: {\"BoolRef\": CM0; \"BoolRefAgain\": CM0; \"IntRef\": CM1; " +
            "\"SingleRef\": CM2; \"SingleRefDirect\": CM3; \"StringRef\": CM4;};", generated.Text);
        Assert.Contains("type CM0 = {", generated.Text);
        Assert.Contains("type CM1 = {", generated.Text);
        Assert.Contains("type CM4 = {", generated.Text);
        Assert.DoesNotContain("type CM5 =", generated.Text);
        Assert.Contains("\"runtimeFile\":\"custom-runtime.ts\"", generated.Text.Split('\n')[0]);

        using var runtime = ReadRuntime(generated.RuntimeText!);
        var entries = runtime.RootElement.GetProperty("types");
        var canonical = entries.GetProperty(owner.FullName);
        var alias = entries.GetProperty("Fixture.OwnerAlias");
        Assert.Equal(owner.FullName, alias.GetProperty("canonicalType").GetString());
        Assert.Equal(canonical.GetProperty("members").GetRawText(), alias.GetProperty("members").GetRawText());
        var lines = generated.Text.Split('\n');
        string OwnerBlock(string name)
        {
            var start = Array.IndexOf(lines, "    " + JsonSerializer.Serialize(name) + ": {");
            Assert.True(start >= 0);
            var next = Array.FindIndex(lines, start + 1, line => line.StartsWith("    \"", StringComparison.Ordinal));
            return string.Join('\n', lines[start..(next < 0 ? lines.Length : next)]);
        }
        var aliasBlock = OwnerBlock("Fixture.OwnerAlias").Replace("Fixture.OwnerAlias", owner.FullName, StringComparison.Ordinal);
        Assert.Equal(OwnerBlock(owner.FullName), aliasBlock);
    }

    [Fact]
    public void PreservesUnknownAndUnclosedFallbacksAndUnconfirmedRawEvidence()
    {
        var intField = Field(Int32);
        var unclosedField = "[FrooxEngine]FrooxEngine.IField<T>";
        var owner = new CatalogType("Fixture.Owner", true, true, null, [], MembersComplete: true,
            Members: new Dictionary<string, CatalogMember>
            {
                ["Unknown"] = new("reference", TargetType: "Fixture.MissingField"),
                ["UnclosedTarget"] = new("reference", TargetType: unclosedField),
            });
        var genericOwner = new CatalogType("Fixture.Generic<T>", true, false, null, [], IsGeneric: true,
            MembersComplete: true, Members: new Dictionary<string, CatalogMember>
            { ["Target"] = new("reference", TargetType: intField) });
        var unconfirmedMember = new CatalogType("Fixture.UnconfirmedMember", true, true, null, [], MembersComplete: true,
            Members: new Dictionary<string, CatalogMember>
            { ["Raw"] = new("field", ValueType: Boolean, Confirmed: false) });
        var unconfirmedOwner = new CatalogType("Fixture.UnconfirmedOwner", false, false, null, [], MembersComplete: true,
            Members: new Dictionary<string, CatalogMember>
            { ["Raw"] = new("field", ValueType: Boolean) });
        var generated = CatalogTypesGenerator.Generate(Make([
            Scalar(Boolean, "boolean"), Scalar(Int32, "int32"), new(intField, true, true, null, [], IsGeneric: true),
            new(unclosedField, true, false, null, [], IsGeneric: true), owner, genericOwner, unconfirmedMember, unconfirmedOwner,
        ]));

        Assert.Contains("\"Unknown\": string | null;", generated.Text);
        Assert.Contains("\"UnclosedTarget\": JsonValue;", generated.Text);
        Assert.Contains("\"Target\": JsonValue;", generated.Text);
        using var runtime = ReadRuntime(generated.RuntimeText!);
        var table = runtime.RootElement.GetProperty("types");
        foreach (var typeName in new[] { unconfirmedMember.FullName, unconfirmedOwner.FullName })
        {
            var type = table.GetProperty(typeName);
            var member = type.GetProperty("members").GetProperty("Raw");
            Assert.False(member.GetProperty("confirmed").GetBoolean());
            Assert.Null(member.GetProperty("scalar").GetString());
            Assert.Null(member.GetProperty("fieldInterface").GetString());
            Assert.Null(member.GetProperty("fieldTarget").GetString());
        }
        Assert.Contains("type CM", generated.Text);
        Assert.Contains("\"scalar\":null", generated.Text);
        Assert.Contains("\"fieldTarget\":null", generated.Text);
    }

    [Fact]
    public void UsesRequiredErrorCodesForInvalidLeafAndUnavailableCatalog()
    {
        var catalog = Make([Scalar(Boolean, "boolean")]);
        var invalid = Assert.Throws<RLoopException>(() => CatalogTypesGenerator.Generate(catalog, "sub/runtime.ts"));
        Assert.Equal("INVALID_ARGUMENT", invalid.Code);
        Assert.Equal(ExitCodes.InvalidArguments, invalid.ExitCode);

        var unavailable = Assert.Throws<RLoopException>(() => CatalogTypesGenerator.Generate(Make([], synthetic: true)));
        Assert.Equal("APPLY_CATALOG_UNAVAILABLE", unavailable.Code);
        Assert.Equal(ExitCodes.ValidationFailed, unavailable.ExitCode);
    }

    [Fact]
    public async Task GeneratedProtoPairTypeChecksWithRealTypeScriptWithoutEmit()
    {
        const string marker = "value \"__proto__\": unchanged";
        var owner = new CatalogType("Fixture.AliasOwner", true, true, null, [], MembersComplete: true,
            Members: new Dictionary<string, CatalogMember> { ["__proto__"] = new("other", MemberType: marker) });
        var catalog = Make([owner], new Dictionary<string, string> { ["__proto__"] = owner.FullName });
        var generated = CatalogTypesGenerator.Generate(catalog);

        var package = new DirectoryInfo(AppContext.BaseDirectory);
        while (package is not null && !File.Exists(Path.Combine(package.FullName, "tools", "resoloop-jsx", "package.json")))
            package = package.Parent;
        Assert.NotNull(package);
        var jsxPackage = Path.Combine(package!.FullName, "tools", "resoloop-jsx");
        var compiler = Path.Combine(jsxPackage, "node_modules", "typescript", "bin", "tsc");
        Assert.True(File.Exists(compiler), "The installed TypeScript compiler is required for the generated-pair type probe.");
        var temporary = Path.Combine(jsxPackage, ".catalog-proto-compile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            File.WriteAllText(Path.Combine(temporary, "catalog-types.d.ts"), generated.Text, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(temporary, "catalog-fields.ts"), generated.RuntimeText!, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(temporary, "assertion.ts"), """
                import type { catalog } from "./catalog-fields.js";
                import type { CatalogComponentRegistry } from "resoloop-jsx";
                declare const runtime: typeof catalog;
                const canonical: CatalogComponentRegistry["Fixture.AliasOwner"]["memberMetadata"]["__proto__"] =
                  runtime.types["Fixture.AliasOwner"].members["__proto__"];
                const alias: CatalogComponentRegistry["__proto__"]["memberMetadata"]["__proto__"] =
                  runtime.types["__proto__"].members["__proto__"];
                void [canonical, alias];
                """, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(temporary, "tsconfig.json"), JsonSerializer.Serialize(new
            {
                compilerOptions = new
                {
                    target = "ES2022", module = "NodeNext", moduleResolution = "NodeNext", strict = true,
                    noEmit = true, baseUrl = "..", paths = new Dictionary<string, string[]> { ["resoloop-jsx"] = ["src/index.ts"] },
                },
                files = new[] { "catalog-fields.ts", "catalog-types.d.ts", "assertion.ts" },
            }), new UTF8Encoding(false));

            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = jsxPackage,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(compiler);
            start.ArgumentList.Add("--project");
            start.ArgumentList.Add(Path.Combine(temporary, "tsconfig.json"));
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(true);
                Assert.Fail("TypeScript generated-pair probe timed out after 45 seconds.");
            }
            Assert.True(process.ExitCode == 0, await output + await error);
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    private static JsonDocument ReadRuntime(string text)
    {
        const string prefix = "export const catalog = ";
        const string suffix = " as const satisfies CatalogRuntimeTable;\n";
        Assert.Contains(prefix, text);
        Assert.EndsWith(suffix, text, StringComparison.Ordinal);
        return JsonDocument.Parse(text[(text.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length)..^suffix.Length]);
    }
}
