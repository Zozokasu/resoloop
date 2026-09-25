using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class AgentEfficiencyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-efficiency-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void BriefPlanHasOneReviewableListAndSummaryReallyOmitsDetails()
    {
        var entries = new[] { new ApplyPlanEntry("delete", "slot", "Root/Owned/Stale", "stale", Reason: "not in declaration") };
        var plan = new ApplyPlanResult(true, "1", "owned", "state.json", null, entries, 0, 0, 0, Deletes: 1);
        var detail = JsonSerializer.SerializeToNode(BriefOutput.Plan(plan, entries))!;
        Assert.False(detail.AsObject().ContainsKey("Changes"));
        Assert.Equal("stale", detail["operations"]![0]!["Key"]!.GetValue<string>());
        Assert.Equal("not in declaration", detail["operations"]![0]!["Reason"]!.GetValue<string>());
        var summary = JsonSerializer.SerializeToNode(BriefOutput.Plan(plan, []))!;
        Assert.Empty(summary["operations"]!.AsArray());
        Assert.Equal(1, summary["Deletes"]!.GetValue<int>());
    }

    [Fact]
    public void BriefAuditKeepsIncompleteEvidenceAndIssuesWithoutSlotDump()
    {
        var issue = new UixAuditIssue("UIX_OBSERVATION_TRUNCATED", "warning", "Narrow scope", "root");
        var audit = new UixAuditReport("root", true, true, "partial", true, 8, [], [issue]);
        var brief = JsonSerializer.SerializeToNode(BriefOutput.Project(audit))!;
        Assert.True(brief["Truncated"]!.GetValue<bool>());
        Assert.True(brief["StructuralOnly"]!.GetValue<bool>());
        Assert.Equal("partial", brief["Verification"]!.GetValue<string>());
        Assert.False(brief.AsObject().ContainsKey("Slots"));
        Assert.Single(brief["issues"]!.AsArray());
    }

    [Fact]
    public void ReportPreservesFullEvidenceAndRefusesExistingFile()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "report.json");
        using (var writer = new OutputWriter(true, true))
        {
            writer.OpenReport(path);
            writer.Success(new { detail = "preserved" }, briefData: new { count = 1 });
        }
        var original = File.ReadAllText(path);
        Assert.Equal("preserved", JsonNode.Parse(original)!["data"]!["detail"]!.GetValue<string>());
        using var second = new OutputWriter(true);
        Assert.Equal("REPORT_CREATE_FAILED", Assert.Throws<RLoopException>(() => second.OpenReport(path)).Code);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void BriefAndReportDoNotConsumeCommands()
    {
        var args = ParsedArguments.Parse(["--brief", "diff", "main.json", "--report", "plan.json", "--json"]);
        Assert.Equal(["diff", "main.json"], args.Positionals);
        Assert.True(args.Has("brief"));
        Assert.Equal("plan.json", args.Option("report"));
    }

    [Fact]
    public void BriefTestRetainsFailedAndUnverifiedCases()
    {
        var tests = new ApplyTestReport(false, true, 3, 2,
            [new("verified", true, false, true, "restored", []),
             new("manual outstanding", true, true, false, "structural only", []),
             new("failure", false, false, true, "value mismatch", [])]);
        var brief = JsonSerializer.SerializeToNode(BriefOutput.Project(tests))!;
        Assert.False(brief["Passed"]!.GetValue<bool>());
        Assert.Equal(3, brief["Total"]!.GetValue<int>());
        Assert.Equal(2, brief["tests"]!.AsArray().Count);
        Assert.Equal("manual outstanding", brief["tests"]![0]!["Name"]!.GetValue<string>());
        Assert.Equal("failure", brief["tests"]![1]!["Name"]!.GetValue<string>());
    }

    [Fact]
    public void ErrorReportKeepsFullAuditAndDiagnosticCode()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "error.json");
        var audit = new UixAuditReport("owned", false, true, "partial", false, 1,
            [new("owned", "Owned", "Root", "Root/Owned", "unknown", [])],
            [new("UIX_GRAPHIC_CONFLICT", "error", "Separate Graphics", "owned")]);
        using (var writer = new OutputWriter(true, true))
        {
            writer.OpenReport(path);
            writer.Error(new RLoopException("UIX_AUDIT_FAILED", "Audit failed", ExitCodes.ValidationFailed,
                new Dictionary<string, object?> { ["report"] = audit }));
        }
        var saved = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.False(saved["ok"]!.GetValue<bool>());
        Assert.Equal("UIX_AUDIT_FAILED", saved["error"]!["code"]!.GetValue<string>());
        Assert.Single(saved["error"]!["context"]!["report"]!["slots"]!.AsArray());
    }

    [Fact]
    public void MissingRecipeParametersFailBeforeApply()
    {
        Directory.CreateDirectory(root);
        UixRecipes.Export("button", Path.Combine(root, "button.json"));
        var path = Path.Combine(root, "main.json");
        File.WriteAllText(path, """
        {"include":"button.json","children":[{"$prototype":"uix.button","$with":{"key":"missing-rect"}}]}
        """);
        Assert.Equal("APPLY_PARAMETER_NOT_FOUND", Assert.Throws<RLoopException>(() => ApplyDocument.Load(path)).Code);
    }

    [Fact]
    public async Task RecipesComposeCallerVisualsAndTypedStateBindings()
    {
        Directory.CreateDirectory(root);
        foreach (var recipe in UixRecipes.Catalog) UixRecipes.Export(recipe.Name, Path.Combine(root, recipe.Name + ".json"));
        var path = Path.Combine(root, "main.json");
        File.WriteAllText(path, """
        {
          "include":["button.json","boolean-state.json","scroll-content.json"],
          "schemaVersion":"1","ownership":{"key":"example"},"slot":{"key":"root","name":"Owned","parent":"Root"},
          "children":[
            {"$prototype":"uix.button","$with":{"key":"accept","rect":{"OffsetMin":[7,9]}},
              "components":[{"key":"custom-hit","type":"FrooxEngine.UIX.Image","fields":{"InteractionTarget":true}}],
              "children":[{"slot":{"key":"arbitrary-face","name":"Any Shape"},"components":[
                {"key":"face","type":"FrooxEngine.UIX.Image","initialFields":{"Tint":[0,0,0,1]}}]}]},
            {"$prototype":"uix.boolean-state","$with":{"key":"feedback","valueType":"colorX",
              "source":"$member:accept-button.IsPressed","target":"$member:face.Tint","off":[0,0,0,1],"on":[1,1,1,1]}},
            {"$prototype":"uix.scroll-content","$with":{"key":"scroll","rect":{},"viewport":"$component:accept-rect"}}
          ]
        }
        """);
        var doc = ApplyDocument.Load(path);
        var validation = await ApplyDocumentValidator.ValidateAsync(doc);
        Assert.True(validation.Valid, JsonSerializer.Serialize(validation.Issues));
        Assert.Equal(3, doc.Compilation!.Instances);
        var button = doc.Children![0];
        Assert.Equal(3, button.Components!.Count);
        Assert.Single(button.Children!);
        Assert.Equal(7, button.Components[0].Fields!["OffsetMin"][0].GetInt32());
        Assert.Empty(button.Components[1].Fields!["ColorDrivers"].EnumerateArray());
        Assert.DoesNotContain("IsPressed", button.Components[1].Fields!.Keys);
        var driver = doc.Children[1].Components![0];
        Assert.Equal("[FrooxEngine]FrooxEngine.BooleanValueDriver<colorX>", driver.Type);
        Assert.Equal("$member:face.Tint", driver.Fields!["TargetField"].GetString());
        Assert.Equal("$member:feedback-driver.State", doc.Children[1].Components![1].Fields!["Target"].GetString());
        Assert.DoesNotContain("NormalizedPosition", doc.Children[2].Components![1].Fields!.Keys);
    }

    [Fact]
    public void RecipeAssetsHaveNoVisualDefaultsAndSyncInstallsThem()
    {
        ProjectInitializer.Initialize(root);
        foreach (var recipe in UixRecipes.Catalog)
        {
            var text = UixRecipes.Read(recipe.Name);
            Assert.DoesNotContain("UIX.Image", text);
            Assert.DoesNotContain("FrooxEngine.UIX.Text\"", text);
            Assert.DoesNotContain("Material", text);
            Assert.DoesNotContain("Tint", text);
            Assert.DoesNotContain("OffsetMin", text);
            Assert.Equal(text.Replace("\r\n", "\n"), File.ReadAllText(Path.Combine(root, ".agents/skills/resonite-uix/recipes", recipe.Name + ".json")));
        }
        var local = Path.Combine(root, ".agents/skills/resonite-uix/recipes/button.json");
        File.AppendAllText(local, "\n ");
        Assert.Equal("SKILL_SYNC_CONFLICT", Assert.Throws<RLoopException>(() => BundledSkillManager.Sync(root, true)).Code);
    }

    [Fact]
    public void ExportRejectsUnknownNamesAndDoesNotOverwrite()
    {
        var path = Path.Combine(root, "button.json");
        Assert.Equal("UIX_RECIPE_NOT_FOUND", Assert.Throws<RLoopException>(() => UixRecipes.Export("../button", path)).Code);
        Assert.False(Directory.Exists(root));
        UixRecipes.Export("button", path);
        Assert.Equal("UIX_RECIPE_EXPORT_FAILED", Assert.Throws<RLoopException>(() => UixRecipes.Export("button", path)).Code);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
