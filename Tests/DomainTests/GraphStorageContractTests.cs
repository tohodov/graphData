using Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Storage;

[RelevantTestClass]
public class GraphStorageContractTests : StorageTests {
    internal async Task<NodeBacking> CreateNode(string? name = null) {
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
        Assert.IsFalse(await first.Nodes.AnyAsync(x => x.LocalId == second.LocalId));
        Assert.IsFalse(await third.Nodes.AnyAsync(x => x.LocalId == second.LocalId));
    }

    [TestMethod]
    public async Task DeleteSubtree_RemovesExternalBacklinksAfterReopen() {
        var owner = await Storage.Create("subtree-owner");
        var child = await Storage.Create("subtree-child", owner.GlobalId);
        var external = await Storage.Create("subtree-external");
        await Storage.Connect(child.GlobalId, external.GlobalId);

        Assert.IsTrue(await external.Nodes.AnyAsync(node => node.GlobalId == child.GlobalId));

        await Storage.Delete(owner.GlobalId);

        IGraphStorage reopened = new SymLinkGraphStorage(
            Options.Create(StorageOptions),
            new CancellationTokensAccessorMock());
        var reopenedExternal = await reopened.Get(external.GlobalId);
        Assert.IsNotNull(reopenedExternal);
        Assert.IsNull(await reopened.Get(child.GlobalId));
        Assert.IsFalse(await reopenedExternal.Nodes.AnyAsync(node => node.GlobalId == child.GlobalId));
        Assert.IsFalse(new DirectoryInfo(Path.Combine(StorageOptions.RootPath, external.LocalId))
            .EnumerateFileSystemInfos()
            .Any(entry => entry.Attributes.HasFlag(FileAttributes.ReparsePoint)));
    }

    [TestMethod]
    public async Task MoveSubtree_RejectsEdgeThatWouldBecomeHierarchicalWithoutMutation() {
        var oldParent = await Storage.Create("move-old-parent");
        var moving = await Storage.Create("move-root", oldParent.GlobalId);
        var descendant = await Storage.Create("move-descendant", moving.GlobalId);
        var newParent = await Storage.Create("move-new-parent");
        await Storage.Connect(moving.GlobalId, newParent.GlobalId);
        await Storage.Connect(descendant.GlobalId, newParent.GlobalId);

        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => Storage.Disconnect(oldParent.GlobalId, moving.GlobalId));
        StringAssert.Contains(error.Message, "ancestor-descendant connection");

        IGraphStorage reopened = new SymLinkGraphStorage(
            Options.Create(StorageOptions),
            new CancellationTokensAccessorMock());
        var reopenedOldParent = await reopened.Get(oldParent.GlobalId);
        var reopenedMoving = await reopened.Get(moving.GlobalId);
        var reopenedDescendant = await reopened.Get(descendant.GlobalId);
        var reopenedNewParent = await reopened.Get(newParent.GlobalId);
        Assert.IsNotNull(reopenedOldParent);
        Assert.IsNotNull(reopenedMoving);
        Assert.IsNotNull(reopenedDescendant);
        Assert.IsNotNull(reopenedNewParent);
        var selectedConnectionPath = Path.Combine(
            StorageOptions.RootPath,
            "move-new-parent",
            "move-root");
        Assert.IsTrue(File.GetAttributes(selectedConnectionPath).HasFlag(FileAttributes.ReparsePoint));
        Assert.IsTrue(Directory.Exists(Path.Combine(
            StorageOptions.RootPath,
            "move-old-parent",
            "move-root")));
        Assert.IsTrue(await reopenedOldParent.Nodes.AnyAsync(node => node.GlobalId == moving.GlobalId));
        Assert.IsTrue(await reopenedMoving.Nodes.AnyAsync(node => node.GlobalId == newParent.GlobalId));
        Assert.IsTrue(await reopenedDescendant.Nodes.AnyAsync(node => node.GlobalId == newParent.GlobalId));
        Assert.IsTrue(await reopenedNewParent.Nodes.AnyAsync(node => node.GlobalId == moving.GlobalId));
        Assert.IsTrue(await reopenedNewParent.Nodes.AnyAsync(node => node.GlobalId == descendant.GlobalId));
    }

    [TestMethod]
    public async Task Connections_RejectDistinctNeighborsWithSameLocalId() {
        var firstOwner = await Storage.Create("first-owner");
        var secondOwner = await Storage.Create("second-owner");
        var firstEndpoint = await Storage.Create("endpoint", firstOwner.GlobalId);
        var secondEndpoint = await Storage.Create("endpoint", secondOwner.GlobalId);
        var participant = await Storage.Create("participant");

        await Storage.Connect(participant.GlobalId, firstEndpoint.GlobalId);
        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => Storage.Connect(participant.GlobalId, secondEndpoint.GlobalId));
        StringAssert.Contains(error.Message, "namespace entry");

        var neighbors = await participant.Nodes
            .Where(node => node.LocalId == "endpoint")
            .Select(static node => node.GlobalId)
            .ToArrayAsync();
        CollectionAssert.AreEquivalent(new[] { firstEndpoint.GlobalId }, neighbors);
        Assert.IsFalse(new DirectoryInfo(Path.Combine(StorageOptions.RootPath, participant.LocalId))
            .EnumerateFileSystemInfos()
            .Any(entry => entry.Name.Contains('~')));
    }

    [TestMethod]
    public async Task Connections_RejectSecondHierarchyEdge() {
        var parent = await Storage.Create("duplicate-hierarchy-parent");
        var child = await Storage.Create("duplicate-hierarchy-child", parent.GlobalId);

        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => Storage.Connect(parent.GlobalId, child.GlobalId));

        StringAssert.Contains(error.Message, "hierarchy connection");
        Assert.AreEqual(1, await parent.Nodes.CountAsync(node => node.GlobalId == child.GlobalId));
        Assert.AreEqual(1, await child.Nodes.CountAsync(node => node.GlobalId == parent.GlobalId));
    }

    [TestMethod]
    public async Task Connections_RejectSecondJunctionEdge() {
        var left = await Storage.Create("duplicate-junction-left");
        var right = await Storage.Create("duplicate-junction-right");
        await Storage.Connect(left.GlobalId, right.GlobalId);

        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => Storage.Connect(left.GlobalId, right.GlobalId));

        StringAssert.Contains(error.Message, "already connected");
        Assert.AreEqual(1, await left.Nodes.CountAsync(node => node.GlobalId == right.GlobalId));
        Assert.AreEqual(1, await right.Nodes.CountAsync(node => node.GlobalId == left.GlobalId));
    }

    [TestMethod]
    public async Task ShouldRejectDeletingStorageRoot() {
        var node = await CreateNode("node");

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => Storage.Delete(new NodePath()));

        Assert.IsTrue(Directory.Exists(StorageOptions.RootPath));
        Assert.IsNotNull(await Storage.Get(node.GlobalId));
    }

    [TestMethod]
    public async Task ShouldReturnMutualConnections() {
        var first = await CreateNode();
        var second = await CreateNode();

        await Storage.Connect(first.GlobalId, second.GlobalId);

        Assert.IsTrue(await first.Nodes.AnyAsync(x => x.LocalId == second.LocalId));
        Assert.IsTrue(await second.Nodes.AnyAsync(x => x.LocalId == first.LocalId));
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

        Assert.IsTrue(await parent.Nodes.AnyAsync(node => node.GlobalId == child.GlobalId));
        Assert.IsTrue(await child.Nodes.AnyAsync(node => node.GlobalId == parent.GlobalId));
    }

    [TestMethod]
    public async Task ShouldIgnoreSelfConnection() {
        var node = await CreateNode();

        await Storage.Connect(node.GlobalId, node.GlobalId);

        CollectionAssert.DoesNotContain(await node.Nodes.ToArrayAsync(), node);
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

        Assert.AreEqual(0, await first.Nodes.CountAsync());

        await first.Nodes.Add(second);

        Assert.IsTrue(await first.Nodes.AnyAsync(node => node.GlobalId == second.GlobalId));
        Assert.IsTrue(await second.Nodes.AnyAsync(node => node.GlobalId == first.GlobalId));

        await first.Nodes.Remove(second);
        Assert.IsFalse(await first.Nodes.AnyAsync(node => node.GlobalId == second.GlobalId));
        Assert.IsFalse(await second.Nodes.AnyAsync(node => node.GlobalId == first.GlobalId));
    }

    [TestMethod]
    [TestCategory(nameof(Node))]
    [TestCategory(nameof(IGraphStorage.Connect))]
    public async Task NodeTraverse_ShouldIterateGraphRecursivelyWithoutDuplicates() {
        var first = await CreateNode("first");
        var second = await CreateNode("second");
        var third = await CreateNode("third");
        var fourth = await CreateNode("fourth");

        await first.Nodes.Add(second);
        await second.Nodes.Add(third);
        await third.Nodes.Add(first);
        await third.Nodes.Add(fourth);

        CollectionAssert.AreEquivalent(
            new[] { first.GlobalId, second.GlobalId, third.GlobalId, fourth.GlobalId },
            await first.Traverse().Select(static node => node.GlobalId).ToArrayAsync());
    }
}
