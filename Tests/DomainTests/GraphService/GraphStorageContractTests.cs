using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

public abstract partial class GraphStorageContractTests {
    private IGraphStorage Storage { get; set; } = default!;
    private GraphService Service { get; set; } = default!;

    [TestInitialize]
    public async Task TestInitializeAsync() {//TODO переписать на конструктор
        Storage = (IGraphStorage)await CreateStorageAsync();
        Service = new GraphService(Storage, new(Storage), new CancellationTokensAccessorMock(), GraphSchemaRegistry.Create());
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

    protected abstract Task<object> CreateStorageAsync();

    internal async Task<NodeState> CreateNode(string? name = null) {
        var result = await Storage.Create(new(name ?? Guid.NewGuid().ToString()), null, new Dictionary<string, string>() {
            { "type", "test" },
            { "created", DateTime.UtcNow.ToString("O") }
        });
        return result;
    }
}
[TestCategory(nameof(IGraphStorage.Create))]
partial class GraphStorageContractTests {
    [TestMethod]
    public async Task ShouldPersistMetadata() {
        var created = await CreateNode();
        var retrieved = await Storage.Get(created.GlobalId);

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
        var pistols = await Storage.Create(new("pistols"));
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
        var america = await Storage.Create(new("america"));
        var assaultRifles = await Storage.Create(new("assault rifles"));
        var m16 = await Storage.Create(new("m16"));
        var unrelated = await Storage.Create(new("unrelated"));

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
        var source = await Storage.Create(new("source"));
        var marker = await Storage.Create(new("marker"), attributes: new Dictionary<string, string> {
            ["id"] = "Y"
        });
        var unrelated = await Storage.Create(new("unrelated"), attributes: new Dictionary<string, string> {
            ["id"] = "Y"
        });

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
        var isolated = await Storage.Create(new("isolated"));
        var connected = await Storage.Create(new("connected"));
        var neighbor = await Storage.Create(new("neighbor"));

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
        var result = await Storage.Get(new InternalId(Guid.NewGuid().ToString()));
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
        var retrieved = await Storage.Get(node.GlobalId);
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

        var deleted = await Storage.Get(second.GlobalId);
        Assert.IsNull(deleted);
        Assert.IsFalse(first.Nodes.Any(x => x.LocalId == second.LocalId));
        Assert.IsFalse(third.Nodes.Any(x => x.LocalId == second.LocalId));
    }
}
[TestCategory(nameof(IGraphStorage.Connect))]
partial class GraphStorageContractTests {
    [TestMethod]
    public async Task ShouldReturnMutualConnections() {
        var first = await CreateNode();
        var second = await CreateNode();

        await Storage.Connect(first.GlobalId, second.GlobalId);

        Assert.IsTrue(first.Nodes.Any(x => x.LocalId == second.LocalId));
        Assert.IsTrue(second.Nodes.Any(x => x.LocalId == first.LocalId));
    }
    [TestMethod]
    public async Task ShouldReturnNestedConnectionsWithGlobalIds() {
        var root = await Storage.Create(new("root"));
        var leftParent = await Storage.Create(new("left"), root.GlobalId);
        var rightParent = await Storage.Create(new("right"), root.GlobalId);
        var left = await Storage.Create(new("node-a"), leftParent.GlobalId);
        var right = await Storage.Create(new("node-b"), rightParent.GlobalId);

        await Storage.Connect(left.GlobalId, right.GlobalId);

        Assert.IsTrue(await Storage.GetNeighbors(left).AnyAsync(node => node.GlobalId == right.GlobalId));
    }

    [TestMethod]
    public async Task ShouldReturnHierarchyConnections() {
        var parent = await Storage.Create(new("parent"));
        var child = await Storage.Create(new("child"), parent.GlobalId);

        Assert.IsTrue(parent.Nodes.Any(node => node.GlobalId == child.GlobalId));
        Assert.IsTrue(child.Nodes.Any(node => node.GlobalId == parent.GlobalId));
    }

    [TestMethod]
    public async Task ShouldIgnoreSelfConnection() {
        var node = await CreateNode();

        await Storage.Connect(node.GlobalId, node.GlobalId);

        CollectionAssert.DoesNotContain(node.Nodes.ToArray(), node);
    }
}
[TestCategory(nameof(GraphService.GetSubgraph))]
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

        var subgraph = (await Service.GetSubgraph([first.GlobalId], 2)).Value!;
        var subgraphNodeIds = subgraph.Nodes.Select(static node => node.GlobalId).ToArray();

        Assert.AreEqual(3, subgraph.Nodes.Count);
        Assert.IsTrue(subgraphNodeIds.Contains(first.GlobalId));
        Assert.IsTrue(subgraphNodeIds.Contains(second.GlobalId));
        Assert.IsTrue(subgraphNodeIds.Contains(third.GlobalId));
        Assert.IsFalse(subgraphNodeIds.Contains(fourth.GlobalId));

        var children = subgraph.Nodes
            .First(x => x.LocalId == first.LocalId)
            .Edges
            .SelectMany(x => new[] { x.Node1, x.Node2 })
            .DistinctBy(static node => node.GlobalId)
            .Where(node => node.GlobalId != first.GlobalId)
            .ToArray();
        Assert.IsTrue(children.Any(x => x.LocalId == second.LocalId));
        Assert.IsFalse(children.Any(x => x.LocalId == third.LocalId));
    }

    [TestMethod]
    public async Task ShouldTraverseHierarchy() {
        var root = await Storage.Create(new("root"));
        var weapons = await Storage.Create(new("weapons"), root.GlobalId);
        var ak47 = await Storage.Create(new("ak_47"), weapons.GlobalId);

        var subgraph = (await Service.GetSubgraph([root.GlobalId], 2)).Value!;

        CollectionAssert.AreEquivalent(
            new[] { root.GlobalId, weapons.GlobalId, ak47.GlobalId },
            subgraph.Nodes.Select(static node => node.GlobalId).ToArray());
    }

    [TestMethod]
    public async Task EmptyQueryShouldStartFromTopLevelRoots() {
        var firstRoot = await Storage.Create(new("first"));
        var secondRoot = await Storage.Create(new("second"));
        var child = await Storage.Create(new("child"), firstRoot.GlobalId);

        var subgraph = (await Service.GetSubgraph([], 0)).Value!;

        CollectionAssert.AreEquivalent(
            new[] { firstRoot.GlobalId, secondRoot.GlobalId },
            subgraph.Nodes.Select(static node => node.GlobalId).ToArray());
        CollectionAssert.DoesNotContain(subgraph.Nodes.Select(static node => node.GlobalId).ToArray(), child.GlobalId);
    }

    [TestMethod]
    public async Task EmptyNodeIdentifierShouldStartFromTopLevelRoots() {
        var firstRoot = await Storage.Create(new("first"));
        var secondRoot = await Storage.Create(new("second"));
        await Storage.Create(new("child"), firstRoot.GlobalId);

        var subgraph = (await Service.GetSubgraph([new InternalId()], 0)).Value!;

        CollectionAssert.AreEquivalent(
            new[] { firstRoot.GlobalId, secondRoot.GlobalId },
            subgraph.Nodes.Select(static node => node.GlobalId).ToArray());
    }
}
[TestCategory(nameof(GraphService.AddSubgraph))]
partial class GraphStorageContractTests {
    [TestMethod]
    public async Task AddSubgraph_ShouldPersistVirtualNodesAndConnections() {
        var catalog = new Node(new InternalId("catalog"));
        var weapon = new Node(new InternalId("catalog", "ak-47"));
        weapon.Attributes["displayName"] = "AK-47";
        var weaponType = new Node(new InternalId("graphdata", "types", "nodes", "Weapon"));

        catalog.Nodes.Add(weapon);
        weapon.Nodes.Add(weaponType);

        var result = await Service.AddSubgraph(catalog);

        Assert.AreEqual(ServiceResultStatus.Ok, result.Status, result.Error);
        CollectionAssert.IsSubsetOf(
            new[] { catalog.GlobalId, weapon.GlobalId, weaponType.GlobalId },
            result.Value!.Nodes.Select(static node => node.GlobalId).ToArray());

        var persistedWeapon = await Storage.Get(weapon.GlobalId);
        Assert.IsNotNull(persistedWeapon);
        Assert.AreEqual("AK-47", persistedWeapon.Attributes["displayName"]);
        Assert.IsNotNull(await Storage.Get(GraphSystemNodeIds.NodeTypeRoot));

        Assert.IsTrue(persistedWeapon.Nodes.Any(node => node.GlobalId == weaponType.GlobalId));
    }

    [TestMethod]
    public async Task AddSubgraph_ShouldPreserveExistingNodeAttributes() {
        await Storage.Create(
            new("catalog"),
            attributes: new Dictionary<string, string> {
                ["color"] = "#123456"
            });
        var catalog = new Node(new InternalId("catalog"));
        catalog.Attributes["color"] = "#abcdef";
        catalog.Attributes["generated"] = "true";

        var result = await Service.AddSubgraph(catalog);

        Assert.AreEqual(ServiceResultStatus.Ok, result.Status, result.Error);
        var persisted = await Storage.Get(catalog.GlobalId);
        Assert.IsNotNull(persisted);
        Assert.AreEqual("#123456", persisted.Attributes["color"]);
        Assert.IsFalse(persisted.Attributes.ContainsKey("generated"));
    }
}
