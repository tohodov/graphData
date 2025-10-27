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
public sealed class RandomAccessGraphStorageTests : GraphStorageContractTests
{
    private string? _rootPath;

    protected override Task<IGraphStorage> CreateStorageAsync()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "GraphDataTests", "RandomAccess", Guid.NewGuid().ToString("N"));
        var options = new RandomAccessGraphStorageOptions
        {
            RootPath = _rootPath,
            MetadataFileExtension = ".json",
            ConnectionsFileExtension = ".json"
        };

        IGraphStorage storage = new RandomAccessGraphStorage(Options.Create(options), NullLogger<RandomAccessGraphStorage>.Instance);
        return Task.FromResult(storage);
    }

    protected override Task OnCleanupAsync()
    {
        if (!string.IsNullOrEmpty(_rootPath) && Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }

        _rootPath = null;
        return base.OnCleanupAsync();
    }

    [TestMethod]
    public async Task CreateNodeAsync_ShouldCreateDedicatedFiles()
    {
        var metadata = CreateMetadata();
        await Storage.CreateNodeAsync(metadata).ConfigureAwait(false);

        Assert.IsNotNull(_rootPath);
        var metadataFile = Path.Combine(_rootPath!, "metadata", metadata.Id.ToString("D") + ".json");
        var connectionsFile = Path.Combine(_rootPath!, "connections", metadata.Id.ToString("D") + ".json");

        Assert.IsTrue(File.Exists(metadataFile));
        Assert.IsTrue(File.Exists(connectionsFile));
    }

    [TestMethod]
    public async Task ConnectNodesAsync_ShouldPersistIndividualAdjacencyLists()
    {
        var first = CreateMetadata();
        var second = CreateMetadata();

        await Storage.CreateNodeAsync(first).ConfigureAwait(false);
        await Storage.CreateNodeAsync(second).ConfigureAwait(false);

        await Storage.ConnectNodesAsync(first.Id, second.Id).ConfigureAwait(false);

        Assert.IsNotNull(_rootPath);
        var firstConnections = await File.ReadAllTextAsync(Path.Combine(_rootPath!, "connections", first.Id.ToString("D") + ".json")).ConfigureAwait(false);
        var secondConnections = await File.ReadAllTextAsync(Path.Combine(_rootPath!, "connections", second.Id.ToString("D") + ".json")).ConfigureAwait(false);

        StringAssert.Contains(firstConnections, second.Id.ToString("D"));
        StringAssert.Contains(secondConnections, first.Id.ToString("D"));
    }
}
