using Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

public class GraphStorageContractTests : StorageTests {
    internal async Task<NodeState> CreateNode(string? name = null) {
        var result = await Storage.Create(new(name ?? Guid.NewGuid().ToString()), null, new Dictionary<string, string>() {
            { "type", "test" },
            { "created", DateTime.UtcNow.ToString("O") }
        });
        return result;
    }
    [TestMethod]
    public async Task ShouldPersistMetadata() {
        var created = await CreateNode();
        var retrieved = await Storage.Get(created.GlobalId);

        Assert.IsNotNull(retrieved);
        Assert.AreEqual(created.LocalId, retrieved.LocalId);
        Assert.AreEqual(created.LocalId, retrieved.LocalId);
        CollectionAssert.AreEquivalent(created.Attributes.ToList(), retrieved.Attributes.ToList());
    }
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
    [TestMethod]
    public async Task NodeMissing() {
        var result = await Storage.Get(new InternalId(Guid.NewGuid().ToString()));
        Assert.IsNull(result);
    }
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
    [TestMethod]
    [TestCategory(nameof(Node))]
    [TestCategory(nameof(IGraphStorage.Update))]
    public async Task NodeAttributes_ShouldPersistDictionaryMutations() {
        var node = await CreateNode("node");

        node.Attributes["type"] = "updated";
        node.Attributes["extra"] = "value";
        node.Attributes.Remove("created");

        var retrieved = await Storage.Get(node.GlobalId);
        Assert.IsNotNull(retrieved);
        Assert.AreEqual("updated", retrieved.Attributes["type"]);
        Assert.AreEqual("value", retrieved.Attributes["extra"]);
        Assert.IsFalse(retrieved.Attributes.ContainsKey("created"));
    }

    [TestMethod]
    [TestCategory(nameof(Node))]
    [TestCategory(nameof(IGraphStorage.Connect))]
    public async Task NodeCollection_ShouldConnectAndDisconnectNodes() {
        var first = await CreateNode("first");
        var second = await CreateNode("second");

        Assert.AreEqual(0, first.Nodes.Count);

        first.Nodes.Add(second);

        Assert.IsTrue(first.Nodes.Any(node => node.GlobalId == second.GlobalId));
        Assert.IsTrue(second.Nodes.Any(node => node.GlobalId == first.GlobalId));

        Assert.IsTrue(first.Nodes.Remove(second));
        Assert.IsFalse(first.Nodes.Any(node => node.GlobalId == second.GlobalId));
        Assert.IsFalse(second.Nodes.Any(node => node.GlobalId == first.GlobalId));
    }

    [TestMethod]
    [TestCategory(nameof(Node))]
    [TestCategory(nameof(IGraphStorage.Connect))]
    public async Task NodeTraverse_ShouldIterateGraphRecursivelyWithoutDuplicates() {
        var first = await CreateNode("first");
        var second = await CreateNode("second");
        var third = await CreateNode("third");
        var fourth = await CreateNode("fourth");

        first.Nodes.Add(second);
        second.Nodes.Add(third);
        third.Nodes.Add(first);
        third.Nodes.Add(fourth);

        CollectionAssert.AreEquivalent(
            new[] { first.GlobalId, second.GlobalId, third.GlobalId, fourth.GlobalId },
            await first.Nodes.Traverse().Select(static node => node.GlobalId).ToArrayAsync());
    }
}
