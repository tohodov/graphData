using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Storage;

public abstract class GraphStorageContractTests
{
    protected IGraphStorage Storage { get; private set; } = default!;

    [TestInitialize]
    public async Task TestInitializeAsync()
    {
        Storage = await CreateStorageAsync().ConfigureAwait(false);
    }

    [TestCleanup]
    public async Task TestCleanupAsync()
    {
        if (Storage is not null)
        {
            switch (Storage)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }

            Storage = null!;
        }

        await OnCleanupAsync().ConfigureAwait(false);
    }

    protected virtual Task OnCleanupAsync() => Task.CompletedTask;

    protected abstract Task<IGraphStorage> CreateStorageAsync();

    protected static NodeMetadata CreateMetadata(Guid? id = null)
    {
        return new NodeMetadata
        {
            Id = id ?? Guid.NewGuid(),
            Name = $"Node-{Guid.NewGuid():N}",
            Attributes = new Dictionary<string, string>
            {
                ["type"] = "test",
                ["created"] = DateTime.UtcNow.ToString("O")
            }
        };
    }

    [TestMethod]
    public async Task CreateNodeAsync_ShouldPersistMetadata()
    {
        var metadata = CreateMetadata();

        var created = await Storage.CreateNodeAsync(metadata).ConfigureAwait(false);
        var retrieved = await Storage.GetNodeMetadataAsync(created.Id).ConfigureAwait(false);

        Assert.IsNotNull(retrieved);
        Assert.AreEqual(created.Id, retrieved!.Id);
        Assert.AreEqual(created.Name, retrieved.Name);
        CollectionAssert.AreEquivalent(created.Attributes.ToList(), retrieved.Attributes.ToList());
    }

    [TestMethod]
    public async Task GetNodeMetadataAsync_ShouldReturnNull_WhenNodeMissing()
    {
        var result = await Storage.GetNodeMetadataAsync(Guid.NewGuid()).ConfigureAwait(false);
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task UpdateMetadataAsync_ShouldPersistChanges()
    {
        var metadata = CreateMetadata();
        await Storage.CreateNodeAsync(metadata).ConfigureAwait(false);

        var updated = metadata with
        {
            Name = metadata.Name + "-updated",
            Attributes = new Dictionary<string, string>
            {
                ["type"] = "updated",
                ["extra"] = "value"
            }
        };

        await Storage.UpdateMetadataAsync(updated).ConfigureAwait(false);
        var retrieved = await Storage.GetNodeMetadataAsync(metadata.Id).ConfigureAwait(false);

        Assert.IsNotNull(retrieved);
        Assert.AreEqual(updated.Name, retrieved!.Name);
        CollectionAssert.AreEquivalent(updated.Attributes.ToList(), retrieved.Attributes.ToList());
    }

    [TestMethod]
    public async Task ConnectNodesAsync_ShouldReturnMutualConnections()
    {
        var first = CreateMetadata();
        var second = CreateMetadata();
        await Storage.CreateNodeAsync(first).ConfigureAwait(false);
        await Storage.CreateNodeAsync(second).ConfigureAwait(false);

        await Storage.ConnectNodesAsync(first.Id, second.Id).ConfigureAwait(false);

        var firstConnections = (await Storage.GetConnectedNodesAsync(first.Id).ConfigureAwait(false)).ToList();
        var secondConnections = (await Storage.GetConnectedNodesAsync(second.Id).ConfigureAwait(false)).ToList();

        CollectionAssert.Contains(firstConnections, second.Id);
        CollectionAssert.Contains(secondConnections, first.Id);
    }

    [TestMethod]
    public async Task ConnectNodesAsync_ShouldIgnoreSelfConnection()
    {
        var node = CreateMetadata();
        await Storage.CreateNodeAsync(node).ConfigureAwait(false);

        await Storage.ConnectNodesAsync(node.Id, node.Id).ConfigureAwait(false);

        var connections = (await Storage.GetConnectedNodesAsync(node.Id).ConfigureAwait(false)).ToList();
        CollectionAssert.DoesNotContain(connections, node.Id);
    }
}
