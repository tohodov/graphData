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

    protected async Task<Node> CreateNode(string? name = null) {
        var result = await Storage.Create(new(name ?? Guid.NewGuid().ToString()), null, new Dictionary<string, string>() {
            { "type", "test" },
            { "created", DateTime.UtcNow.ToString("O") }
        });
        return result.Value!;
    }
}
[TestCategory(nameof(IGraphStorage.Create))]
partial class GraphStorageContractTests {
    [TestMethod]
    public async Task ShouldPersistMetadata() {
        var created = await CreateNode();
        var retrieved = (await Storage.Get(created.GlobalId)).Value!;

        Assert.IsNotNull(retrieved);
        Assert.AreEqual(created.LocalId, retrieved.LocalId);
        Assert.AreEqual(created.LocalId, retrieved.LocalId);
        CollectionAssert.AreEquivalent(created.Attributes.ToList(), retrieved.Attributes.ToList());
    }
}
[TestCategory(nameof(GraphSearchService))]
partial class GraphStorageContractTests {
    [TestMethod]
    public async Task Search_ShouldFindTextMatchBelowHierarchyAncestor() {
        var pistols = (await Storage.Create(new("pistols"))).Value!;
        await Storage.Create(new("double-action revolvers"), pistols.GlobalId);
        await Storage.Create(new("rifles"));

        var service = new GraphSearchService(Storage);
        var matches = await service.SearchNodesStreamAsync(new NodeSearchQuery {
            Return = ["n"],
            Where = new AllNodeSearchExpression {
                Expressions = [
                    new NodeTextSearchExpression {
                        Node = Var("n"),
                        Value = "double-action"
                    },
                    new NodeDescendantSearchExpression {
                        Ancestor = Literal(pistols.LocalId.ToString()),
                        Descendant = Var("n"),
                        MaxDepth = 3
                    }
                ]
            }
        }).ToArrayAsync();

        Assert.AreEqual(1, matches.Length);
        var match = matches.Single();
        Assert.AreEqual("pistols/double-action revolvers", match.Node.GlobalId.ToString());
        Assert.IsTrue(match.MatchedBy.Any(x => x.StartsWith("descendant-of:pistols", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(match.MatchedBy.Any(x => x.StartsWith("text:double-action", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task Search_ShouldFindNodesConnectedToAllAnchors() {
        var america = (await Storage.Create(new("america"))).Value!;
        var assaultRifles = (await Storage.Create(new("assault rifles"))).Value!;
        var m16 = (await Storage.Create(new("m16"))).Value!;
        var unrelated = (await Storage.Create(new("unrelated"))).Value!;

        await Storage.Connect(america.GlobalId, m16.GlobalId);
        await Storage.Connect(assaultRifles.GlobalId, m16.GlobalId);
        await Storage.Connect(america.GlobalId, unrelated.GlobalId);

        var service = new GraphSearchService(Storage);
        var matches = await service.SearchNodesStreamAsync(new NodeSearchQuery {
            Return = ["n"],
            Where = new AllNodeSearchExpression {
                Expressions = [
                    new NodePathSearchExpression {
                        Left = Var("n"),
                        Right = Literal(america.LocalId.ToString()),
                        MaxDepth = 1
                    },
                    new NodePathSearchExpression {
                        Left = Var("n"),
                        Right = Literal(assaultRifles.LocalId.ToString()),
                        MaxDepth = 1
                    }
                ]
            }
        }).ToArrayAsync();

        Assert.AreEqual(1, matches.Length);
        Assert.AreEqual(m16.LocalId, matches.Single().Node.LocalId);
    }

    [TestMethod]
    public async Task Search_ShouldReturnSolutionsForVariableConnectedToAttributeMatch() {
        var source = (await Storage.Create(new("source"))).Value!;
        var marker = (await Storage.Create(new("marker"), attributes: new Dictionary<string, string> {
            ["id"] = "Y"
        })).Value!;
        var unrelated = (await Storage.Create(new("unrelated"), attributes: new Dictionary<string, string> {
            ["id"] = "Y"
        })).Value!;

        await Storage.Connect(source.GlobalId, marker.GlobalId);

        var service = new GraphSearchService(Storage);
        var matches = await service.SearchNodesStreamAsync(new NodeSearchQuery {
            Return = ["n", "x"],
            Where = new AllNodeSearchExpression {
                Expressions = [
                    new NodeConnectedSearchExpression {
                        Left = Var("n"),
                        Right = Var("x")
                    },
                    new NodeAttributeSearchExpression {
                        Node = Var("x"),
                        Key = "id",
                        Value = "Y"
                    }
                ]
            }
        }).ToArrayAsync();

        Assert.AreEqual(1, matches.Length);
        var match = matches.Single();
        Assert.AreEqual(source.LocalId, match.Bindings["n"].LocalId);
        Assert.AreEqual(marker.LocalId, match.Bindings["x"].LocalId);
        Assert.AreNotEqual(unrelated.LocalId, match.Bindings["x"].LocalId);
    }

    [TestMethod]
    public async Task Search_ShouldFindIsolatedNodesWithNegatedExistentialRelation() {
        var isolated = (await Storage.Create(new("isolated"))).Value!;
        var connected = (await Storage.Create(new("connected"))).Value!;
        var neighbor = (await Storage.Create(new("neighbor"))).Value!;

        await Storage.Connect(connected.GlobalId, neighbor.GlobalId);

        var service = new GraphSearchService(Storage);
        var matches = await service.SearchNodesStreamAsync(new NodeSearchQuery {
            Return = ["x"],
            Where = new NotNodeSearchExpression {
                Expression = new ExistsNodeSearchExpression {
                    Variables = ["y"],
                    Expression = new NodeConnectedSearchExpression {
                        Left = Var("x"),
                        Right = Var("y")
                    }
                }
            }
        }).ToArrayAsync();

        CollectionAssert.AreEquivalent(
            new[] { isolated.LocalId },
            matches.Select(static match => match.Node.LocalId).ToArray());
    }

    private static NodeVariableSearchSelector Var(string name) => new() { Name = name };

    private static NodeLiteralSearchSelector Literal(string name) => new() { Name = name };
}
[TestCategory(nameof(IGraphStorage.Get))]
partial class GraphStorageContractTests {
    [TestMethod]
    public async Task NodeMissing() {
        var result = await Storage.Get(new NodeGlobalId(Guid.NewGuid().ToString()));
        Assert.IsNull(result);
    }
}
[TestCategory(nameof(IGraphStorage.Update))]
partial class GraphStorageContractTests {
    [TestMethod]
    public async Task ShouldPersistChanges() {
        var node = await CreateNode("node");
        var attributes = new Dictionary<string, string>(node.Attributes);
        attributes["type"] = "updated";
        attributes["extra"] = "value";
        node.Attributes = attributes;
        var retrieved = (await Storage.Get(node.GlobalId)).Value!;
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

        await Storage.Connect(first.GlobalId, second.GlobalId);
        await Storage.Connect(second.GlobalId, third.GlobalId);

        await Storage.Delete(second.GlobalId);

        Assert.IsNull(await Storage.Get(second.GlobalId));
        Assert.IsFalse((await Storage.GetConnectedNodesAsync(first)).Value!.Any(x => x.LocalId == second.LocalId));
        Assert.IsFalse((await Storage.GetConnectedNodesAsync(third)).Value!.Any(x => x.LocalId == second.LocalId));
    }
}
[TestCategory(nameof(IGraphStorage.Connect))]
partial class GraphStorageContractTests {
    [TestMethod]
    public async Task ShouldReturnMutualConnections() {
        var first = await CreateNode();
        var second = await CreateNode();

        await Storage.Connect(first.GlobalId, second.GlobalId);

        var firstConnections = (await Storage.GetConnectedNodesAsync(first)).Value!;
        var secondConnections = (await Storage.GetConnectedNodesAsync(second)).Value!;

        Assert.IsTrue(firstConnections.Any(x => x.LocalId == second.LocalId));
        Assert.IsTrue(secondConnections.Any(x => x.LocalId == first.LocalId));
    }
    [TestMethod]
    public async Task ShouldIgnoreSelfConnection() {
        var node = await CreateNode();

        await Storage.Connect(node.GlobalId, node.GlobalId);

        var connections = (await Storage.GetConnectedNodesAsync(node)).Value!.ToList();
        CollectionAssert.DoesNotContain(connections, node);
    }
}
[TestCategory(nameof(IGraphStorage.GetSubgraphAsync))]
partial class GraphStorageContractTests {
    [TestMethod]
    public async Task ShouldRespectDepth() {
        var first = await CreateNode("first");
        var second = await CreateNode("second");
        var third = await CreateNode("third");
        var fourth = await CreateNode("fourth");

        await Storage.Connect(first.GlobalId, second.GlobalId);
        await Storage.Connect(second.GlobalId, third.GlobalId);
        await Storage.Connect(third.GlobalId, fourth.GlobalId);

        var query = new SubgraphQuery {
            Nodes = [first.GlobalId],
            MaxDepth = 2
        };

        var subgraph = (await Storage.GetSubgraphAsync(query)).Value!;

        Assert.AreEqual(3, subgraph.Nodes.Count);
        Assert.IsTrue(subgraph.Nodes.Contains(first));
        Assert.IsTrue(subgraph.Nodes.Contains(second));
        Assert.IsTrue(subgraph.Nodes.Contains(third));
        Assert.IsFalse(subgraph.Nodes.Contains(fourth));

        var children = subgraph.Nodes.First(x => x.LocalId == first.LocalId).Edges.SelectMany(x => new[] { x.Node1, x.Node2 }).Distinct().Except([first]).ToArray();
        Assert.IsTrue(children.Any(x => x.LocalId == second.LocalId));
        Assert.IsFalse(children.Any(x => x.LocalId == third.LocalId));
    }
}
