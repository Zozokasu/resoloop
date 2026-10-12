using System.Text.RegularExpressions;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationRegenerateSkillTests
{
    [Fact]
    public void SyncInstallsCreationWorkflowAndAllSkillLinksStayInsidePackage()
    {
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var root = Path.GetFullPath(Path.Combine(tempRoot, "resoloop-creation-skill-" + Guid.NewGuid().ToString("N")));
        try
        {
            Assert.Equal(tempRoot, Path.GetDirectoryName(root));
            Assert.True(BundledSkillManager.Sync(root, update: true).Synchronized);
            var skillDir = Path.GetFullPath(Path.Combine(root, ".agents", "skills", "resonite-build"));
            var skillPath = Path.Combine(skillDir, "SKILL.md");
            var referencePath = Path.Combine(skillDir, "references", "creation-regenerate.md");
            Assert.True(File.Exists(referencePath));
            Assert.Contains("resonite-build/references/creation-regenerate.md", BundledSkillManager.Files);

            var skill = File.ReadAllText(skillPath);
            const string link = "(references/creation-regenerate.md)";
            Assert.Contains(link, skill);
            Assert.True(File.Exists(Path.Combine(skillDir, "references", "creation-regenerate.md")));

            foreach (var markdown in Directory.EnumerateFiles(skillDir, "*.md", SearchOption.AllDirectories))
                AssertMarkdownLinksStayInside(skillDir, markdown);

            var reference = File.ReadAllText(referencePath);
            Assert.Contains("experimental", reference);
            Assert.Contains("creation-1", reference);
            Assert.Contains("--url auto --session S-ID", reference);
            Assert.Contains("manual", reference);
            Assert.Contains("Asset, Flux and Script", reference);
            Assert.Contains("does not restore", reference);
            Assert.Contains("--discard-pending OP", reference);
        }
        finally
        {
            if (Directory.Exists(root) && Path.GetDirectoryName(root) == tempRoot)
                Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertMarkdownLinksStayInside(string skillDir, string markdown)
    {
        var text = File.ReadAllText(markdown);
        foreach (Match match in Regex.Matches(text, @"\[[^\]]+\]\(([^)]+)\)"))
        {
            var target = match.Groups[1].Value.Trim();
            if (target.StartsWith('<') && target.Contains('>'))
                target = target[1..target.IndexOf('>')];
            else
                target = target.Split(' ', 2)[0];
            if (target.Length == 0 || target.StartsWith('#')) continue;
            Assert.False(Uri.TryCreate(target, UriKind.Absolute, out _), $"External URI in bundled skill: {markdown}: {target}");

            var pathPart = target.Split('#', 2)[0].Split('?', 2)[0];
            Assert.False(Path.IsPathRooted(pathPart), $"Rooted link in {markdown}: {target}");
            var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(markdown)!, pathPart.Replace('/', Path.DirectorySeparatorChar)));
            var relative = Path.GetRelativePath(skillDir, resolved);
            Assert.False(relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                $"Link escapes the bundled skill directory: {markdown}: {target}");
            Assert.True(File.Exists(resolved), $"Broken bundled skill link: {markdown}: {target}");
        }
    }
}
