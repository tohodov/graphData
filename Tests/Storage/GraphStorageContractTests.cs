using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Storage;

public abstract partial class GraphStorageContractTests {
    protected IGraphStorage Storage { get; private set; } = default!;

    [TestInitialize]
    public async Task TestInitializeAsync() {
        Storage = await CreateStorageAsync();
    }

    [TestCleanup]
    public async Task TestCleanupAsync() {
        if (Storage is not null) {
            switch (Storage) {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }

            Storage = null!;
        }

        await OnCleanupAsync();
    }

    protected virtual Task OnCleanupAsync() => Task.CompletedTask;

    protected abstract Task<IGraphStorage> CreateStorageAsync();

    protected Task<Node> CreateNode(string? name = null) => Storage.Create(name ?? Guid.NewGuid().ToString(), null, new() {
        { "type", "test" },
        { "created", DateTime.UtcNow.ToString("O")
    } });
}
[TestCategory(nameof(IGraphStorage.Create))]
partial class GraphStorageContractTests {
    [TestMethod]
    public async Task ShouldPersistMetadata() {
        var created = await CreateNode();
        var retrieved = await Storage.Get(created.Name);

        Assert.IsNotNull(retrieved);
        Assert.AreEqual(created.Name, retrieved.Name);
        Assert.AreEqual(created.Name, retrieved.Name);
        CollectionAssert.AreEquivalent(created.Attributes.ToList(), retrieved.Attributes.ToList());
    }
}
[TestCategory(nameof(GraphSearchService))]
partial class GraphStorageContractTests {
    [TestMethod]
    public async Task Search_ShouldFindTextMatchBelowHierarchyAncestor() {
        var pistols = await Storage.Create("pistols");
        await Storage.Create("double-action revolvers", pistols);
        await Storage.Create("rifles");

        var service = new GraphSearchService(Storage);
        var matches = await service.SearchNodesAsync(new NodeSearchQuery {
            Text = "double-action",
            DescendantOf = new HierarchySearchPattern {
                NodeName = pistols.Name,
                MaxDepth = 3
            }
        });

        Assert.AreEqual(1, matches.Count);
        var match = matches.Single();
        Assert.AreEqual("pistols/double-action revolvers", match.Node.Name);
        Assert.IsTrue(match.MatchedBy.Any(x => x.StartsWith("descendant-of:pistols", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(match.MatchedBy.Any(x => x.StartsWith("text:double-action", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task Search_ShouldFindNodesConnectedToAllAnchors() {
        var america = await Storage.Create("america");
        var assaultRifles = await Storage.Create("assault rifles");
        var m16 = await Storage.Create("m16");
        var unrelated = await Storage.Create("unrelated");

        await Storage.Connect(america, m16);
        await Storage.Connect(assaultRifles, m16);
        await Storage.Connect(america, unrelated);

        var service = new GraphSearchService(Storage);
        var matches = await service.SearchNodesAsync(new NodeSearchQuery {
            ConnectedToAll = [
                new ConnectionSearchPattern {
                    NodeName = america.Name,
                    MaxDepth = 1
                },
                new ConnectionSearchPattern {
                    NodeName = assaultRifles.Name,
                    MaxDepth = 1
                }
            ]
        });

        Assert.AreEqual(1, matches.Count);
        Assert.AreEqual(m16.Name, matches.Single().Node.Name);
    }
}
[TestCategory(nameof(IGraphStorage.Get))]
partial class GraphStorageContractTests {
    [TestMethod]
    public async Task NodeMissing() {
        var result = await Storage.Get(Guid.NewGuid().ToString());
        Assert.IsNull(result);
    }
}
[TestCategory(nameof(IGraphStorage.Update))]
partial class GraphStorageContractTests {
    [TestMethod]
    public async Task ShouldPersistChanges() {
        var node = await CreateNode();
        var attributes = new Dictionary<string, string>(node.Attributes);
        attributes["type"] = "updated";
        attributes["extra"] = "value";

        await Storage.Update(node.Name, attributes);
        var retrieved = await Storage.Get(node.Name);

        Assert.IsNotNull(retrieved);
        CollectionAssert.AreEquivalent(attributes.ToList(), retrieved.Attributes.ToList());
    }
}
[TestCategory(nameof(IGraphStorage.Delete))]
partial class GraphStorageContractTests {
    [TestMethod]
    public async Task ShouldRemoveNodeAndIncidentConnections() {
        var first = await CreateNode();
        var second = await CreateNode();
        var third = await CreateNode();

        await Storage.Connect(first, second);
        await Storage.Connect(second, third);

        await Storage.Delete(second.Name);

        Assert.IsNull(await Storage.Get(second.Name));
        Assert.IsFalse((await Storage.GetConnectedNodesAsync(first)).Any(x => x.Name == second.Name));
        Assert.IsFalse((await Storage.GetConnectedNodesAsync(third)).Any(x => x.Name == second.Name));
    }
}
[TestCategory(nameof(IGraphStorage.Connect))]
partial class GraphStorageContractTests {
    [TestMethod]
    public async Task ShouldReturnMutualConnections() {
        var first = await CreateNode();
        var second = await CreateNode();

        await Storage.Connect(first, second);

        var firstConnections = await Storage.GetConnectedNodesAsync(first);
        var secondConnections = await Storage.GetConnectedNodesAsync(second);

        Assert.IsTrue(firstConnections.Any(x => x.Name == second.Name));
        Assert.IsTrue(secondConnections.Any(x => x.Name == first.Name));
    }
    [TestMethod]
    public async Task ShouldIgnoreSelfConnection() {
        var node = await CreateNode();

        await Storage.Connect(node, node);

        var connections = (await Storage.GetConnectedNodesAsync(node)).ToList();
        CollectionAssert.DoesNotContain(connections, node.Name);
    }
}
[TestCategory(nameof(IGraphStorage.GetSubgraphAsync))]
partial class GraphStorageContractTests {
    [TestMethod]
    public async Task ShouldRespectDepth() {
        var first = await CreateNode();
        var second = await CreateNode();
        var third = await CreateNode();
        var fourth = await CreateNode();

        await Storage.Connect(first, second);
        await Storage.Connect(second, third);
        await Storage.Connect(third, fourth);

        var query = new SubgraphQuery {
            RootNodeIds = new[] { first.Name },
            MaxDepth = 2
        };

        var subgraph = await Storage.GetSubgraphAsync(query);

        Assert.AreEqual(3, subgraph.Nodes.Count);
        Assert.IsTrue(subgraph.Nodes.Contains(first));
        Assert.IsTrue(subgraph.Nodes.Contains(second));
        Assert.IsTrue(subgraph.Nodes.Contains(third));
        Assert.IsFalse(subgraph.Nodes.Contains(fourth));

        var children = subgraph.Nodes.First(x => x.Name == first.Name).Edges.Values.SelectMany(x => new[] { x.Node1, x.Node2 }).Distinct().Except([first]).ToArray();
        Assert.IsTrue(children.Any(x => x.Name == second.Name));
        Assert.IsFalse(children.Any(x => x.Name == third.Name));
    }
}
