using RLoop.Core;

namespace RLoop.Tests;

public sealed class VariablesAuthoringSkillTests
{
    [Fact]
    public void SyncInstallsVariablesAuthoringReferenceAndSkillLinkResolves()
    {
        var root = Path.Combine(Path.GetTempPath(), "resoloop-variables-skill-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(BundledSkillManager.Sync(root, update: true).Synchronized);
            var skillDir = Path.Combine(root, ".agents", "skills", "resonite-build");
            var referencePath = Path.Combine(skillDir, "references", "variables-authoring.md");
            Assert.True(File.Exists(referencePath));

            var skill = File.ReadAllText(Path.Combine(skillDir, "SKILL.md"));
            const string link = "(references/variables-authoring.md)";
            Assert.Contains(link, skill);
            var linkStart = skill.IndexOf(link, StringComparison.Ordinal);
            var relative = skill.Substring(linkStart + 1, "references/variables-authoring.md".Length);
            Assert.True(File.Exists(Path.Combine(skillDir, relative)));
            Assert.Contains("resonite-build/references/variables-authoring.md", BundledSkillManager.Files);

            var reference = File.ReadAllText(referencePath);
            Assert.Contains("resoloop-jsx/variables", reference);
            Assert.Contains("Fixture.Objects.Lamp", reference);
            Assert.Contains("has not been verified against a live Resonite session", reference);
        }
        finally
        {
            if (Directory.Exists(root) && Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(root, recursive: true);
        }
    }
}
