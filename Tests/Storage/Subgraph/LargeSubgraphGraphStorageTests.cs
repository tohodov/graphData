using System;
using System.IO;
using System.Threading.Tasks;
using GraphData.Core.Abstractions;
using GraphData.SubgraphStorage;
using GraphData.SubgraphStorage.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Storage.Subgraph;

[TestClass]
public sealed class LargeSubgraphGraphStorageTests : GraphStorageContractTests
{
    private string? _rootPath;
    private LargeSubgraphGraphStorageOptions _options = null!;

    protected override Task<IGraphStorage> CreateStorageAsync()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "GraphDataTests", "Subgraph", Guid.NewGuid().ToString("N"));
        _options = new LargeSubgraphGraphStorageOptions
        {
            RootPath = _rootPath,
            BucketPrefixLength = 3
        };

        IGraphStorage storage = new LargeSubgraphGraphStorage(Options.Create(_options), NullLogger<LargeSubgraphGraphStorage>.Instance);
        return Task.FromResult(storage);
    }

    protected override Task OnCleanupAsync()
    {
        try
        {
            if (!string.IsNullOrEmpty(_rootPath) && Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
        finally
        {
            _rootPath = null;
            _options = null!;
        }

        return base.OnCleanupAsync();
    }

    [TestMethod]
    public async Task CreateNodeAsync_ShouldShardMetadataByPrefix()
    {
        var first = CreateMetadata(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var second = CreateMetadata(Guid.Parse("00000000-0000-0000-0000-0000000000FF"));
        var third = CreateMetadata(Guid.Parse("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF"));

        await Storage.CreateNodeAsync(first).ConfigureAwait(false);
        await Storage.CreateNodeAsync(second).ConfigureAwait(false);
        await Storage.CreateNodeAsync(third).ConfigureAwait(false);

        Assert.IsNotNull(_rootPath);
        var metadataDirectory = Path.Combine(_rootPath!, _options.MetadataDirectoryName);
        var metadataFiles = Directory.GetFiles(metadataDirectory, "*.json");

        Assert.IsTrue(metadataFiles.Length >= 2, "Nodes should be sharded across multiple metadata buckets.");
    }

    [TestMethod]
    public async Task ConnectNodesAsync_ShouldPersistConnectionsInSharedBucket()
    {
        var first = CreateMetadata(Guid.Parse("10000000-0000-0000-0000-000000000000"));
        var second = CreateMetadata(Guid.Parse("1F000000-0000-0000-0000-000000000000"));

        await Storage.CreateNodeAsync(first).ConfigureAwait(false);
        await Storage.CreateNodeAsync(second).ConfigureAwait(false);

        await Storage.ConnectNodesAsync(first.Id, second.Id).ConfigureAwait(false);

        Assert.IsNotNull(_rootPath);
        var connectionsDirectory = Path.Combine(_rootPath!, _options.ConnectionsDirectoryName);
        var bucketFile = Path.Combine(connectionsDirectory, "1f0.json");
        Assert.IsTrue(File.Exists(bucketFile));

        var content = await File.ReadAllTextAsync(bucketFile).ConfigureAwait(false);
        Assert.IsTrue(content.Contains(second.Id.ToString("D"), StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(content.Contains(first.Id.ToString("D"), StringComparison.OrdinalIgnoreCase));
    }
}
