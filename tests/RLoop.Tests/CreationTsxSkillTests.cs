using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationTsxSkillTests
{
    [Fact]
    public void SyncedSkillDocumentsTsxCreationContractAndPreflightBoundary()
    {
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var root = Path.GetFullPath(Path.Combine(tempRoot, "resoloop-creation-tsx-skill-" + Guid.NewGuid().ToString("N")));
        try
        {
            Assert.Equal(tempRoot, Path.GetDirectoryName(root));
            Assert.True(BundledSkillManager.Sync(root, update: true).Synchronized);

            var skillDir = Path.GetFullPath(Path.Combine(root, ".agents", "skills", "resonite-build"));
            var skill = File.ReadAllText(Path.Combine(skillDir, "SKILL.md"));
            var workflow = File.ReadAllText(Path.Combine(skillDir, "references", "creation-regenerate.md"));

            Assert.Contains("--target creation", skill);
            Assert.Contains("resoloop validate NEW_FILE.json --json", skill);
            Assert.Contains("ownership", skill);
            Assert.Contains("same-name sibling Slots", skill);
            Assert.Contains("unverified live", skill);

            Assert.Contains("CreationRegeneratePreflight", workflow);
            Assert.Contains("unsupported Asset, Flux, Script, and other creation declarations stop at that preflight.", workflow);
            Assert.Contains("Mode-free `validate`", workflow);
            Assert.Contains("does not run `CreationRegeneratePreflight`", workflow);
            Assert.DoesNotContain("There is no TSX creation entrypoint", workflow);
            Assert.DoesNotContain("JSON-only whole-root", skill);
        }
        finally
        {
            if (Directory.Exists(root) && Path.GetDirectoryName(root) == tempRoot)
                Directory.Delete(root, recursive: true);
        }
    }
}
