using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Storage;

[RelevantTestClass]
public sealed class SymLinkGraphStorageTests : GraphStorageContractTests
{
    [TestMethod]
    public async Task CreateNodeAsync_ShouldCreateDirectoryAndMetadataFile()
    {
        var node = await CreateNode();
        var nodeDirectory = Path.Combine(StorageOptions.RootPath, node.LocalId);
        Assert.IsTrue(Directory.Exists(nodeDirectory));

        var metadataPath = Path.Combine(nodeDirectory, StorageOptions.MetadataFileName);
        Assert.IsTrue(File.Exists(metadataPath));
    }

    [TestMethod]
    public async Task ConnectNodesAsync_ShouldCreateDirectoryLinks()
    {
        var first = await CreateNode();
        var second = await CreateNode();

        await first.Nodes.Add(second);

        var firstLink = Path.Combine(StorageOptions.RootPath, first.LocalId, second.LocalId);
        var secondLink = Path.Combine(StorageOptions.RootPath, second.LocalId, first.LocalId);

        Assert.IsTrue(Directory.Exists(firstLink) || File.Exists(firstLink));
        Assert.IsTrue(Directory.Exists(secondLink) || File.Exists(secondLink));

        var expectedFirstTarget = Path.Combine(StorageOptions.RootPath, second.LocalId);
        var expectedSecondTarget = Path.Combine(StorageOptions.RootPath, first.LocalId);

        Assert.AreEqual(Path.GetFullPath(expectedFirstTarget), new DirectoryInfo(firstLink).LinkTarget);
        Assert.AreEqual(Path.GetFullPath(expectedSecondTarget), new DirectoryInfo(secondLink).LinkTarget);
    }

    private static IReadOnlyList<string> SnapshotFileSystem(string root)
    {
        var entries = new List<string>();
        if (!Directory.Exists(root))
        {
            return entries;
        }

        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(root));

        while (stack.Count > 0)
        {
            var directory = stack.Pop();

            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry.Name is "." or "..")
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, entry.FullName);
                var isDirectory = entry.Attributes.HasFlag(FileAttributes.Directory);
                var isLink = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
                var kind = isLink
                    ? "link"
                    : isDirectory
                        ? "dir"
                        : "file";

                entries.Add($"{kind}:{relative}");

                if (isDirectory && !isLink)
                {
                    stack.Push(new DirectoryInfo(entry.FullName));
                }
            }
        }

        entries.Sort(StringComparer.OrdinalIgnoreCase);
        return entries;
    }
}
