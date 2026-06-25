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
    private NtfsGraphStorageOptions options = null!;
    private string? snapshotRoot;
    private IReadOnlyList<string> baselineEntries = Array.Empty<string>();
    private bool parentInitiallyExisted;

    protected override Task<object> CreateStorageAsync()
    {
        var folder = "E:\\TTT";
        if (!Directory.Exists(folder))
            folder = Path.GetTempPath();
        snapshotRoot = Path.Combine(folder, "GraphDataTests");
        parentInitiallyExisted = Directory.Exists(snapshotRoot);
        baselineEntries = parentInitiallyExisted
            ? SnapshotFileSystem(snapshotRoot)
            : Array.Empty<string>();

        var rootPath = Path.Combine(snapshotRoot!, Guid.NewGuid().ToString("N"));
        options = new NtfsGraphStorageOptions
        {
            RootPath = rootPath
        };

        IGraphStorage storage = new SymLinkGraphStorage(Options.Create(options), new CancellationTokensAccessorMock(), NullLogger<SymLinkGraphStorage>.Instance);
        return Task.FromResult<object>(storage);
    }

    protected override async Task OnCleanupAsync()
    {
        try
        {
            if (!string.IsNullOrEmpty(options?.RootPath) && Directory.Exists(options.RootPath))
            {
                Directory.Delete(options.RootPath, recursive: true);
            }

            if (snapshotRoot is not null)
            {
                if (!parentInitiallyExisted && Directory.Exists(snapshotRoot) &&
                    !Directory.EnumerateFileSystemEntries(snapshotRoot).Any())
                {
                    Directory.Delete(snapshotRoot);
                }

                if (parentInitiallyExisted)
                {
                    Assert.IsTrue(Directory.Exists(snapshotRoot), $"The directory '{snapshotRoot}' should exist after cleanup.");

                    var currentEntries = SnapshotFileSystem(snapshotRoot);
                    CollectionAssert.AreEquivalent(baselineEntries.ToList(), currentEntries.ToList(),
                        "The test left unexpected files, directories, or links in the NTFS test root.");
                }
                else
                {
                    Assert.IsFalse(Directory.Exists(snapshotRoot),
                        $"The temporary test root '{snapshotRoot}' should not remain after cleanup.");
                }
            }
        }
        finally
        {
            options = null!;
            snapshotRoot = null;
            baselineEntries = Array.Empty<string>();
            parentInitiallyExisted = false;

            await base.OnCleanupAsync();
        }
    }

    [TestMethod]
    public async Task CreateNodeAsync_ShouldCreateDirectoryAndMetadataFile()
    {
        var node = await CreateNode();
        var nodeDirectory = Path.Combine(options.RootPath, node.LocalId);
        Assert.IsTrue(Directory.Exists(nodeDirectory));

        var metadataPath = Path.Combine(nodeDirectory, options.MetadataFileName);
        Assert.IsTrue(File.Exists(metadataPath));
    }

    [TestMethod]
    public async Task ConnectNodesAsync_ShouldCreateDirectoryLinks()
    {
        var first = await CreateNode();
        var second = await CreateNode();

        first.Nodes.Add(second);

        var firstLink = Path.Combine(options.RootPath, first.LocalId, second.LocalId);
        var secondLink = Path.Combine(options.RootPath, second.LocalId, first.LocalId);

        Assert.IsTrue(Directory.Exists(firstLink) || File.Exists(firstLink));
        Assert.IsTrue(Directory.Exists(secondLink) || File.Exists(secondLink));

        var expectedFirstTarget = Path.Combine(options.RootPath, second.LocalId);
        var expectedSecondTarget = Path.Combine(options.RootPath, first.LocalId);

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
