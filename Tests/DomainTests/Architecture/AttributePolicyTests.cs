using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Architecture;

[RelevantTestClass]
public sealed class AttributePolicyTests
{
    [TestMethod]
    public void DomainLogic_DoesNotUseNodeAttributes()
    {
        var root = RepoRoot();
        var allowedFiles = new HashSet<string> {
            "Domain/Models/Node.cs",
            "Domain/Services/GraphSearchService.cs"
        };

        var violations = Directory
            .EnumerateFiles(Path.Combine(root, "Domain"), "*.cs", SearchOption.AllDirectories)
            .Select(file => new {
                File = file,
                RelativePath = Path.GetRelativePath(root, file).Replace('\\', '/'),
                Text = File.ReadAllText(file, Encoding.UTF8)
            })
            .Where(file => !allowedFiles.Contains(file.RelativePath))
            .Where(file =>
                file.Text.Contains(".Attributes", StringComparison.Ordinal) ||
                file.Text.Contains("AttributeNames", StringComparison.Ordinal))
            .Select(file => file.RelativePath)
            .ToArray();

        if (violations.Length > 0)
            Assert.Fail("Domain logic must not use node attributes: " + string.Join(", ", violations));
    }

    [TestMethod]
    public void Domain_DoesNotExposeInternalAttributeNameConstants()
    {
        var files = Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "Domain"), "*AttributeNames*.cs", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/'))
            .ToArray();

        if (files.Length > 0)
            Assert.Fail("Domain must not expose internal attribute name constants: " + string.Join(", ", files));
    }

    [TestMethod]
    public void Initializer_DoesNotUseAttributesAsCompletionState()
    {
        var source = File.ReadAllText(
            Path.Combine(RepoRoot(), "Domain", "Services", "GraphStorageInitializer.cs"),
            Encoding.UTF8);
        var forbiddenReads = new[] {
            ".Attributes.TryGetValue",
            ".Attributes.ContainsKey",
            ".Attributes[",
            ".Attributes.Any"
        };
        var violations = forbiddenReads
            .Where(pattern => source.Contains(pattern, StringComparison.Ordinal))
            .ToArray();

        if (violations.Length > 0)
            Assert.Fail("Initializer completion state must be graph topology, not attributes: " + string.Join(", ", violations));
    }

    private static string RepoRoot([CallerFilePath] string sourcePath = "")
    {
        foreach (var candidate in new[] {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
            Path.GetDirectoryName(sourcePath)
        }) {
            var directory = new DirectoryInfo(candidate!);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GraphData.slnx"))) {
                directory = directory.Parent;
            }

            if (directory is not null)
                return directory.FullName;
        }

        Assert.Fail("Could not locate GraphData.slnx from test output directory, current directory, or source path.");
        throw new InvalidOperationException("Unreachable after Assert.Fail.");
    }
}
