using RLoop.Core;

namespace RLoop.Tests;

public sealed class ApplyRecoverySkillTests
{
    [Fact]
    public void S3InitInstallsApplyRecoveryReferenceFromEmbeddedBundle()
    {
        var root = Path.Combine(Path.GetTempPath(), "resoloop-recovery-skill-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = BundledSkillManager.Sync(root, update: true);
            Assert.True(result.Synchronized);
            var installed = Path.Combine(root, ".agents", "skills", "resonite-build", "references", "apply-recovery.md");
            Assert.True(File.Exists(installed));
            Assert.Contains("--discard-pending OPERATION_ID --yes", File.ReadAllText(installed));
            Assert.Contains("previousStatePending", File.ReadAllText(installed));
            Assert.Contains("references/apply-recovery.md", File.ReadAllText(Path.Combine(root, ".agents", "skills", "resonite-build", "SKILL.md")));
            Assert.Empty(BundledSkillManager.Sync(root, update: true).Updated);
        }
        finally
        {
            if (Directory.Exists(root) && Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(root, recursive: true);
        }
    }
}
