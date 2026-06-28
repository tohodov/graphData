using Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[RelevantTestClass]
public sealed class DslTests : GraphServiceTests {
    [TestMethod]
    public async Task LINQ_Traverse_TEMP() {
        var weaponTypeBaseNode = ((Node)await Service.CreateNode("weapon", Service.TypesRoot.GlobalId))!;
        var weaponType = (await Service.GetTypeNode(weaponTypeBaseNode))!;
        var node = ((Node)await Service.CreateNode("ak47", Service.Root.GlobalId, weaponType))!;
        await Service.CreateNode("m16", Service.Root.GlobalId, weaponType);
        await Service.CreateNode("mp5", Service.Root.GlobalId, weaponType);

        var type = node.Incidences
            .OfType<InstanceOf.InstanceEnd>()
            .Select(x => x.Type)
            .First();
        Assert.AreEqual<NodeLocalId>("weapon", type.LocalId);

        var metaType = type.Incidences
            .OfType<InstanceOf.InstanceEnd>()
            .FirstOrDefault();
        Assert.IsNull(metaType);

        var instances = type.Incidences
            .OfType<InstanceOf.TypeEnd>()
            .Select(x => x.Instance)
            .ToArray();
        CollectionAssert.AreEquivalent(new NodeLocalId[] { "ak47", "m16", "mp5" }, instances.Select(x => x.LocalId).ToArray());
    }

    [TestMethod]
    public async Task LINQ_Traverse() {
        var weaponType = new NodeType("weapon");
        Service.TypesRoot.Nodes.Add(weaponType);
        var ak47 = new InstanceNode("ak47", weaponType);
        Service.Root.Nodes.Add(ak47);
        Service.Root.Nodes.Add(new InstanceNode("m16", weaponType));
        Service.Root.Nodes.Add(new InstanceNode("mp5", weaponType));

        var type = ak47.Incidences
            .OfType<InstanceOf.InstanceEnd>()
            .Select(x => x.Type)
            .First();
        await AssertNode("weapon", type);

        var metaType = type.Incidences
            .OfType<InstanceOf.InstanceEnd>()
            .FirstOrDefault();
        Assert.IsNull(metaType);

        var instances = type.Incidences
            .OfType<InstanceOf.TypeEnd>()
            .Select(x => x.Instance)
            .ToArray();
        CollectionAssert.AreEquivalent(new NodeLocalId[] { "ak47", "m16", "mp5" }, instances.Select(x => x.LocalId).ToArray());
        foreach (var instance in instances)
            await AssertNode(type.LocalId, type);
    }

    [TestMethod]
    public async Task NodesAdd_ShouldMaterializePreparedVirtualSubgraphUnderStorageParent() {
        var parent = new Node("parent");
        var child = new Node("child");
        parent.Nodes.Add(child);

        Service.Root.Nodes.Add(parent);

        await AssertNode("parent", parent);
        await AssertNode("child", child);

        var storedChild = await Storage.Get(new NodePath("parent", "child"));
        Assert.IsNotNull(storedChild);
        Assert.AreEqual(child.GlobalId, storedChild.GlobalId);

        var wrongRootChild = await Storage.Get(new NodePath("child"));
        Assert.IsNull(wrongRootChild);
    }

    [TestMethod]
    public async Task NodeNodes_ShouldReflectFolderChangesAfterFirstRead() {
        var parent = (await Service.CreateNode("dsl-nodes-parent")).Value!;
        var first = (await Service.CreateNode("first", parent.GlobalId)).Value!;
        var secondPath = Path.Combine(StorageOptions.RootPath, parent.LocalId, "second");

        CollectionAssert.AreEquivalent(
            new[] { first.LocalId },
            ReadNodeLocalIds(parent));

        Directory.CreateDirectory(secondPath);

        CollectionAssert.AreEquivalent(
            new[] { first.LocalId, new NodeLocalId("second") },
            ReadNodeLocalIds(parent));

        Directory.Delete(secondPath);

        CollectionAssert.AreEquivalent(
            new[] { first.LocalId },
            ReadNodeLocalIds(parent));
    }

    [TestMethod]
    public async Task NodeEdges_ShouldReflectFolderChangesAfterFirstRead() {
        var parent = (await Service.CreateNode("dsl-edges-parent")).Value!;
        var first = (await Service.CreateNode("first", parent.GlobalId)).Value!;
        var secondPath = Path.Combine(StorageOptions.RootPath, parent.LocalId, "second");

        CollectionAssert.AreEquivalent(
            new[] { first.LocalId },
            ReadEdgeNeighborLocalIds(parent));

        Directory.CreateDirectory(secondPath);

        CollectionAssert.AreEquivalent(
            new[] { first.LocalId, new NodeLocalId("second") },
            ReadEdgeNeighborLocalIds(parent));

        Directory.Delete(secondPath);

        CollectionAssert.AreEquivalent(
            new[] { first.LocalId },
            ReadEdgeNeighborLocalIds(parent));
    }

    [TestMethod]
    public async Task NodeIncidences_ShouldReflectDslTypeAttachmentsAfterFirstRead() {
        var weaponTypeBaseNode = (await Service.CreateNode("dsl-incidence-weapon", Service.TypesRoot.GlobalId)).Value!;
        var weaponType = (await Service.GetTypeNode(weaponTypeBaseNode))!;

        CollectionAssert.AreEquivalent(
            Array.Empty<NodeLocalId>(),
            ReadTypedInstanceLocalIds(weaponType));

        var ak47 = (await Service.CreateNode("dsl-incidence-ak47", Service.Root.GlobalId, weaponType)).Value!;

        CollectionAssert.AreEquivalent(
            new[] { ak47.LocalId },
            ReadTypedInstanceLocalIds(weaponType));

        var m16 = (await Service.CreateNode("dsl-incidence-m16", Service.Root.GlobalId, weaponType)).Value!;

        CollectionAssert.AreEquivalent(
            new[] { ak47.LocalId, m16.LocalId },
            ReadTypedInstanceLocalIds(weaponType));
    }

    [TestMethod]
    public async Task EdgesRemove_ShouldKeepHierarchyChildByMovingItThroughRemainingEdge() {
        var oldParent = new Node("old-parent");
        var child = new Node("child");
        var newParent = new Node("new-parent");
        oldParent.Nodes.Add(child);
        Service.Root.Nodes.Add(oldParent);
        Service.Root.Nodes.Add(newParent);
        child.Edges.Add(new Edge(child, newParent));
        var hierarchyEdge = oldParent.Edges.Single(edge => Connects(edge, oldParent, child));

        oldParent.Edges.Remove(hierarchyEdge);

        var oldChild = await Service.GetNodeAsync(new InternalId("old-parent", "child"));
        Assert.AreEqual(ServiceResultStatus.NotFound, oldChild.Status);

        var movedChild = await Service.GetNodeAsync(new InternalId("new-parent", "child"));
        Assert.AreEqual(ServiceResultStatus.Ok, movedChild.Status);
        Assert.IsNotNull(movedChild.Value);
        Assert.AreEqual<NodeLocalId>("child", movedChild.Value.LocalId);

        var reloadedOldParent = await Service.GetNodeAsync(new InternalId("old-parent"));
        Assert.AreEqual(ServiceResultStatus.Ok, reloadedOldParent.Status);
        Assert.IsNotNull(reloadedOldParent.Value);
        Assert.IsFalse(reloadedOldParent.Value.Nodes.Any(node => node.LocalId == "child"));

        var reloadedNewParent = await Service.GetNodeAsync(new InternalId("new-parent"));
        Assert.AreEqual(ServiceResultStatus.Ok, reloadedNewParent.Status);
        Assert.IsNotNull(reloadedNewParent.Value);
        Assert.IsTrue(reloadedNewParent.Value.Nodes.Any(node => node.LocalId == "child"));
    }

    [TestMethod]
    public async Task NodesRemove_ShouldDeleteHierarchyChild() {
        var parent = new Node("parent");
        var child = new Node("child");
        parent.Nodes.Add(child);
        Service.Root.Nodes.Add(parent);

        parent.Nodes.Remove(child);

        var reloadedParent = await Service.GetNodeAsync(new InternalId("parent"));
        Assert.AreEqual(ServiceResultStatus.Ok, reloadedParent.Status);
        Assert.IsNotNull(reloadedParent.Value);
        Assert.IsFalse(reloadedParent.Value.Nodes.Any(node => node.LocalId == "child"));

        var deletedChild = await Service.GetNodeAsync(new InternalId("parent", "child"));
        Assert.AreEqual(ServiceResultStatus.NotFound, deletedChild.Status);
    }

    [TestMethod]
    public async Task NodesRemove_ShouldKeepHierarchyChildWhenItHasOtherEdges() {
        var oldParent = new Node("nodes-remove-old-parent");
        var child = new Node("child");
        var newParent = new Node("nodes-remove-new-parent");
        oldParent.Nodes.Add(child);
        Service.Root.Nodes.Add(oldParent);
        Service.Root.Nodes.Add(newParent);
        child.Edges.Add(new Edge(child, newParent));

        oldParent.Nodes.Remove(child);

        var oldChild = await Service.GetNodeAsync(new InternalId("nodes-remove-old-parent", "child"));
        Assert.AreEqual(ServiceResultStatus.NotFound, oldChild.Status);

        var movedChild = await Service.GetNodeAsync(new InternalId("nodes-remove-new-parent", "child"));
        Assert.AreEqual(ServiceResultStatus.Ok, movedChild.Status);
        Assert.IsNotNull(movedChild.Value);
        Assert.AreEqual<NodeLocalId>("child", movedChild.Value.LocalId);

        var reloadedNewParent = await Service.GetNodeAsync(new InternalId("nodes-remove-new-parent"));
        Assert.AreEqual(ServiceResultStatus.Ok, reloadedNewParent.Status);
        Assert.IsNotNull(reloadedNewParent.Value);
        Assert.IsTrue(reloadedNewParent.Value.Nodes.Any(node => node.LocalId == "child"));
    }

    [TestMethod]
    public void EdgesRemove_ShouldThrowWhenHierarchyEdgeIsChildsOnlyEdge() {
        var parent = new Node("parent");
        var child = new Node("child");
        parent.Nodes.Add(child);
        Service.Root.Nodes.Add(parent);
        var hierarchyEdge = parent.Edges.Single(edge => Connects(edge, parent, child));

        Assert.ThrowsException<InvalidOperationException>(() => parent.Edges.Remove(hierarchyEdge));
    }

    private static bool Connects(Edge edge, Node first, Node second) =>
        edge.Node1.GlobalId == first.GlobalId && edge.Node2.GlobalId == second.GlobalId
        || edge.Node1.GlobalId == second.GlobalId && edge.Node2.GlobalId == first.GlobalId;

    private static NodeLocalId[] ReadNodeLocalIds(Node node) =>
        node.Nodes.Select(static child => child.LocalId).ToArray();

    private static NodeLocalId[] ReadEdgeNeighborLocalIds(Node node) =>
        node.Edges
            .Select(edge => edge.Node1.GlobalId == node.GlobalId ? edge.Node2 : edge.Node1)
            .Where(neighbor => neighbor.GlobalId != node.GlobalId)
            .Select(static neighbor => neighbor.LocalId)
            .ToArray();

    private static NodeLocalId[] ReadTypedInstanceLocalIds(Node type) =>
        type.Incidences
            .OfType<InstanceOf.TypeEnd>()
            .Select(static incidence => incidence.Instance.LocalId)
            .ToArray();
}
