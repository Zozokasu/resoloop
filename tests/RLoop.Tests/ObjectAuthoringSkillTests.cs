using RLoop.Core;

namespace RLoop.Tests;

public sealed class ObjectAuthoringSkillTests
{
    [Fact]
    public void SyncInstallsObjectAuthoringReferenceAndSkillLinkResolves()
    {
        WithInstalledSkill((skillDir, skill) =>
        {
            var reference = Path.Combine(skillDir, "references", "object-authoring.md");
            Assert.True(File.Exists(reference));
            Assert.Contains("(references/object-authoring.md)", skill);
            var linkStart = skill.IndexOf("(references/object-authoring.md)", StringComparison.Ordinal);
            var relative = skill.Substring(linkStart + 1, "references/object-authoring.md".Length);
            Assert.True(File.Exists(Path.Combine(skillDir, relative)));
            Assert.Contains("resonite-build/references/object-authoring.md", BundledSkillManager.Files);
        });
    }

    [Fact]
    public void ReferenceListsWorkflowStepsInTheFixedOrder()
    {
        WithInstalledSkill((skillDir, _) =>
        {
            var text = ReadReference(skillDir);
            string[] steps =
            [
                "\n1. Reflection.", "\n2. Bounded capture.", "\n3. Catalog types.", "\n4. defineObject.",
                "\n5. Offline validate, diff and plan decision.", "\n6. Authorized apply.", "\n7. Exact-target readback.",
            ];
            AssertInOrder(text, steps);
        });
    }

    [Fact]
    public void ReferenceKeepsAuthorizationAndCleanupSafetyInTheApplyStep()
    {
        WithInstalledSkill((skillDir, _) =>
        {
            var text = ReadReference(skillDir);
            var apply = text.IndexOf("\n6. Authorized apply.", StringComparison.Ordinal);
            var readback = text.IndexOf("\n7. Exact-target readback.", StringComparison.Ordinal);
            Assert.True(apply >= 0 && readback > apply);
            var step = text.Substring(apply, readback - apply);
            AssertInOrder(step,
            [
                "authorization for the intended session", "`ResoLoop_Test*` Slot", "`finally`",
                "no pending operation remains", "ownership, state and session", "read back",
                "exact `ResoLoop_Test*` Slot", "Never target Root", "keep the explicit `--yes`",
            ]);
            Assert.Contains("`--discard-pending`", step);
            Assert.Contains("never add it automatically", step);
            var reflection = text.Substring(0, text.IndexOf("\n2. Bounded capture.", StringComparison.Ordinal));
            Assert.Contains("needs explicit authorization", reflection);
        });
    }

    [Fact]
    public void ReferenceMatchesReadmeApiVersionsAndStatesLimits()
    {
        WithInstalledSkill((skillDir, skill) =>
        {
            var text = ReadReference(skillDir);
            AssertInOrder(text,
            [
                "experimental", "not been verified in a live Resonite session", "Toggle", "512 requested names",
                "two minutes", "`CATALOG_CAPTURE_INCOMPLETE`", "generator 3", "`componentFields(type, catalog)`",
                "`defineObject<P>()({ exports, body })`", "`Def.instance(id)`", "`ref.export(handle, name)`",
                "^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", "`instanceId::localKey`", "Nested Objects are unsupported",
                "schema 1", "version 3", "--catalog NEW_CATALOG.json", "`validate --strict`",
                "Moving a Component", "runtime values may be lost", "does not guarantee",
            ]);
            AssertInOrder(text, ["boolean", "int32", "string", "single"]);
            Assert.Contains("Catalog format 2 and mapper version 3 are unchanged", text);
            Assert.Contains("generator version 3", skill);
            Assert.DoesNotContain("generator version is 2", skill);
        });
    }

    private static void AssertInOrder(string text, IReadOnlyList<string> parts)
    {
        var position = 0;
        foreach (var part in parts)
        {
            var found = text.IndexOf(part, position, StringComparison.Ordinal);
            Assert.True(found >= 0, $"'{part}' is missing or out of order after offset {position}.");
            position = found + part.Length;
        }
    }

    private static string ReadReference(string skillDir) =>
        Normalize(File.ReadAllText(Path.Combine(skillDir, "references", "object-authoring.md")));

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static void WithInstalledSkill(Action<string, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "resoloop-object-skill-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(BundledSkillManager.Sync(root, update: true).Synchronized);
            var skillDir = Path.Combine(root, ".agents", "skills", "resonite-build");
            check(skillDir, Normalize(File.ReadAllText(Path.Combine(skillDir, "SKILL.md"))));
        }
        finally
        {
            if (Directory.Exists(root) && Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(root, recursive: true);
        }
    }
}
