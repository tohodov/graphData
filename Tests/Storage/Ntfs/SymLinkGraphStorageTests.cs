using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GraphData.Core.Abstractions;
using GraphData.NtfsStorage;
using GraphData.NtfsStorage.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Storage.Ntfs;

[TestClass]
public sealed class SymLinkGraphStorageTests : GraphStorageContractTests
{
    private NtfsGraphStorageOptions _options = null!;
    private string? _snapshotRoot;
    private IReadOnlyList<string> _baselineEntries = Array.Empty<string>();
    private bool _parentInitiallyExisted;

    protected override Task<IGraphStorage> CreateStorageAsync()
    {
        _snapshotRoot = Path.Combine(Path.GetTempPath(), "GraphDataTests");
        _parentInitiallyExisted = Directory.Exists(_snapshotRoot);
        _baselineEntries = _parentInitiallyExisted
            ? SnapshotFileSystem(_snapshotRoot)
            : Array.Empty<string>();

        var rootPath = Path.Combine(_snapshotRoot!, Guid.NewGuid().ToString("N"));
        _options = new NtfsGraphStorageOptions
        {
            RootPath = rootPath
        };

        IGraphStorage storage = new SymLinkGraphStorage(Options.Create(_options), NullLogger<SymLinkGraphStorage>.Instance);
        return Task.FromResult(storage);
    }

    protected override async Task OnCleanupAsync()
    {
        try
        {
            if (!string.IsNullOrEmpty(_options?.RootPath) && Directory.Exists(_options.RootPath))
            {
                Directory.Delete(_options.RootPath, recursive: true);
            }

            if (_snapshotRoot is not null)
            {
                if (!_parentInitiallyExisted && Directory.Exists(_snapshotRoot) &&
                    !Directory.EnumerateFileSystemEntries(_snapshotRoot).Any())
                {
                    Directory.Delete(_snapshotRoot);
                }

                if (_parentInitiallyExisted)
                {
                    Assert.IsTrue(Directory.Exists(_snapshotRoot), $"The directory '{_snapshotRoot}' should exist after cleanup.");

                    var currentEntries = SnapshotFileSystem(_snapshotRoot);
                    CollectionAssert.AreEquivalent(_baselineEntries.ToList(), currentEntries.ToList(),
                        "The test left unexpected files, directories, or links in the NTFS test root.");
                }
                else
                {
                    Assert.IsFalse(Directory.Exists(_snapshotRoot),
                        $"The temporary test root '{_snapshotRoot}' should not remain after cleanup.");
                }
            }
        }
        finally
        {
            _options = null!;
            _snapshotRoot = null;
            _baselineEntries = Array.Empty<string>();
            _parentInitiallyExisted = false;

            await base.OnCleanupAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CreateNodeAsync_ShouldCreateDirectoryAndMetadataFile()
    {
        var metadata = CreateMetadata();

        await Storage.CreateNodeAsync(metadata).ConfigureAwait(false);

        var nodeDirectory = Path.Combine(_options.RootPath, metadata.Id.ToString("D"));
        Assert.IsTrue(Directory.Exists(nodeDirectory));

        var metadataPath = Path.Combine(nodeDirectory, _options.MetadataFileName);
        Assert.IsTrue(File.Exists(metadataPath));
    }

    [TestMethod]
    public async Task ConnectNodesAsync_ShouldCreateDirectoryLinks()
    {
        var first = CreateMetadata();
        var second = CreateMetadata();
        await Storage.CreateNodeAsync(first).ConfigureAwait(false);
        await Storage.CreateNodeAsync(second).ConfigureAwait(false);

        await Storage.ConnectNodesAsync(first.Id, second.Id).ConfigureAwait(false);

        var firstLink = Path.Combine(_options.RootPath, first.Id.ToString("D"), second.Id.ToString("D"));
        var secondLink = Path.Combine(_options.RootPath, second.Id.ToString("D"), first.Id.ToString("D"));

        Assert.IsTrue(Directory.Exists(firstLink) || File.Exists(firstLink));
        Assert.IsTrue(Directory.Exists(secondLink) || File.Exists(secondLink));

        var expectedFirstTarget = Path.Combine(_options.RootPath, second.Id.ToString("D"));
        var expectedSecondTarget = Path.Combine(_options.RootPath, first.Id.ToString("D"));

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
