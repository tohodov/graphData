using Abstractions;
using GraphData.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[RelevantTestClass]
public sealed class DslTests : GraphDslTests {
    [TestMethod]
    public async Task LINQ_Traverse() {
        var weaponType = new NodeType("weapon");
        Graph.NodeTypes.Nodes.Add(weaponType);
        var ak47 = new InstanceNode("ak47", weaponType);
        Graph.Root.Nodes.Add(ak47);
        Graph.Root.Nodes.Add(new InstanceNode("m16", weaponType));
        Graph.Root.Nodes.Add(new InstanceNode("mp5", weaponType));

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

        Graph.Root.Nodes.Add(parent);

        await AssertNode("parent", parent);
        await AssertNode("child", child);

        var storedChild = await Storage.Get(new NodePath("parent", "child"));
        Assert.IsNotNull(storedChild);
        Assert.AreEqual(child.GlobalId, storedChild.GlobalId);

        var wrongRootChild = await Storage.Get(new NodePath("child"));
        Assert.IsNull(wrongRootChild);
    }

    [TestMethod]
    public async Task NodesAdd_ShouldThrowWhenExistingNodeIsAlreadyLinked() {
        var first = Graph.Root.Nodes.Create("existing-link-first");
        var second = Graph.Root.Nodes.Create("existing-link-second");
        first.Nodes.Add(second);
        Assert.ThrowsException<InvalidOperationException>(() => first.Nodes.Add(second));
    }

    [TestMethod]
    public async Task NodeNodes_ShouldReflectFolderChangesAfterFirstRead() {
        var parent = Graph.Root.Nodes.Create("dsl-nodes-parent");
        var first = parent.Nodes.Create("first");
        var secondPath = Path.Combine(StorageOptions.RootPath, parent.LocalId, "second");

        CollectionAssert.AreEquivalent(
            new[] { first.LocalId },
            parent.Nodes.Select(static child => child.LocalId).ToArray());

        Directory.CreateDirectory(secondPath);

        CollectionAssert.AreEquivalent(
            new[] { first.LocalId, new NodeLocalId("second") },
            parent.Nodes.Select(static child => child.LocalId).ToArray());

        Directory.Delete(secondPath);

        CollectionAssert.AreEquivalent(
            new[] { first.LocalId },
            parent.Nodes.Select(static child => child.LocalId).ToArray());
    }

    [TestMethod]
    public async Task NodeEdges_ShouldReflectFolderChangesAfterFirstRead() {
        var parent = Graph.Root.Nodes.Create("dsl-edges-parent");
        var first = parent.Nodes.Create("first");
        var secondPath = Path.Combine(StorageOptions.RootPath, parent.LocalId, "second");

        CollectionAssert.AreEquivalent(
            new[] { first.LocalId },
            parent.Edges
                .Select(edge => edge.Node1.GlobalId == parent.GlobalId ? edge.Node2 : edge.Node1)
                .Where(neighbor => neighbor.GlobalId != parent.GlobalId)
                .Select(static neighbor => neighbor.LocalId)
                .ToArray());

        Directory.CreateDirectory(secondPath);

        CollectionAssert.AreEquivalent(
            new[] { first.LocalId, new NodeLocalId("second") },
            parent.Edges
                .Select(edge => edge.Node1.GlobalId == parent.GlobalId ? edge.Node2 : edge.Node1)
                .Where(neighbor => neighbor.GlobalId != parent.GlobalId)
                .Select(static neighbor => neighbor.LocalId)
                .ToArray());

        Directory.Delete(secondPath);

        CollectionAssert.AreEquivalent(
            new[] { first.LocalId },
            parent.Edges
                .Select(edge => edge.Node1.GlobalId == parent.GlobalId ? edge.Node2 : edge.Node1)
                .Where(neighbor => neighbor.GlobalId != parent.GlobalId)
                .Select(static neighbor => neighbor.LocalId)
                .ToArray());
    }

    [TestMethod]
    public async Task NodeIncidences_ShouldReflectDslTypeAttachmentsAfterFirstRead() {
        var weaponType = new NodeType("weapon");
        Graph.NodeTypes.Nodes.Add(weaponType);

        CollectionAssert.AreEquivalent(
            Array.Empty<NodeLocalId>(),
            weaponType.Incidences
                .OfType<InstanceOf.TypeEnd>()
                .Select(static incidence => incidence.Instance.LocalId)
                .ToArray());

        var ak47 = new InstanceNode("ak47", weaponType);
        weaponType.Nodes.Add(ak47);

        CollectionAssert.AreEquivalent(
            new[] { ak47.LocalId },
            weaponType.Incidences
                .OfType<InstanceOf.TypeEnd>()
                .Select(static incidence => incidence.Instance.LocalId)
                .ToArray());

        var m16 = new InstanceNode("m16", weaponType);
        weaponType.Nodes.Add(m16);

        CollectionAssert.AreEquivalent(
            new[] { ak47.LocalId, m16.LocalId },
            weaponType.Incidences
                .OfType<InstanceOf.TypeEnd>()
                .Select(static incidence => incidence.Instance.LocalId)
                .ToArray());
    }

    [TestMethod]
    public async Task NodeTypeDefinition_EnforcesRequiredTypedSlot() {
        var weaponType = new NodeType("weapon-type");
        var manufacturerType = new NodeType("manufacturer-type");
        Graph.NodeTypes.Nodes.Add(weaponType);
        Graph.NodeTypes.Nodes.Add(manufacturerType);
        var definition = new NodeTypeBuilder(weaponType, type => throw new NotImplementedException())
            .RequiresSlot("manufacturer", manufacturerType)
            .Build();

        var ak47 = new InstanceNode("ak-47", weaponType);
        Graph.Root.Nodes.Add(ak47);
        ak47.Nodes.Add(weaponType);

        Assert.ThrowsException<InvalidOperationException>(() => definition.EnsureSatisfiedBy(ak47));

        var manufacturer = new InstanceNode("kalashnikov", manufacturerType);
        Graph.Root.Nodes.Add(manufacturer);
        manufacturer.Nodes.Add(manufacturerType);
        ak47.Nodes.Add(manufacturer);

        definition.EnsureSatisfiedBy(ak47);

        var storedAk47 = await Storage.Get(ak47.GlobalId);
        Assert.IsNotNull(storedAk47);
        Assert.IsTrue(await storedAk47.Nodes.AnyAsync(node => node.GlobalId == weaponType.GlobalId));
        Assert.IsTrue(await storedAk47.Nodes.AnyAsync(node => node.GlobalId == manufacturer.GlobalId));
        Assert.IsTrue(Directory.Exists(Path.Combine(StorageOptions.RootPath, "ak-47")));
        Assert.IsTrue(File.GetAttributes(Path.Combine(StorageOptions.RootPath, "ak-47", "kalashnikov")).HasFlag(FileAttributes.ReparsePoint));
    }

    [TestMethod]
    public async Task EdgesRemove_ShouldKeepHierarchyChildByMovingItThroughRemainingEdge() {
        var oldParent = new Node("old-parent");
        var child = new Node("child");
        var newParent = new Node("new-parent");
        oldParent.Nodes.Add(child);
        Graph.Root.Nodes.Add(oldParent);
        Graph.Root.Nodes.Add(newParent);

        child.Edges.Add(new Edge(child, newParent));
        oldParent.Nodes.Remove(child);

        var storedOldParent = await Storage.Get(oldParent.GlobalId);
        var storedNewParent = await Storage.Get(newParent.GlobalId);
        Assert.IsNotNull(storedOldParent);
        Assert.IsNotNull(storedNewParent);
        Assert.IsFalse(await storedOldParent.Nodes.AnyAsync(node => node.LocalId == "child"));
        Assert.IsTrue(await storedNewParent.Nodes.AnyAsync(node => node.LocalId == "child"));
        Assert.IsNull(await Storage.Get(oldParent.GlobalId, child.LocalId));
        Assert.IsNotNull(await Storage.Get(newParent.GlobalId, child.LocalId));
        Assert.IsFalse(Directory.Exists(Path.Combine(StorageOptions.RootPath, "old-parent", "child")));
        Assert.IsTrue(Directory.Exists(Path.Combine(StorageOptions.RootPath, "new-parent", "child")));
    }

    [TestMethod]
    public async Task NodesRemove_ShouldDeleteHierarchyChild() {
        var parent = new Node("parent");
        var child = new Node("child");
        parent.Nodes.Add(child);
        Graph.Root.Nodes.Add(parent);

        parent.Nodes.Remove(child);

        Assert.IsFalse(parent.Nodes.Any(node => node.LocalId == "child"));
        Assert.IsNull(await Storage.Get(parent.GlobalId, child.LocalId));
        Assert.IsFalse(parent.GetBacking().Folder.EnumerateDirectories().Any());

        Assert.IsFalse(Directory.Exists(Path.Combine(StorageOptions.RootPath, "parent", "child")));
    }

    [TestMethod]
    public async Task NodesRemove_ShouldKeepHierarchyChildWhenItHasOtherEdges() {
        var oldParent = new Node("old");
        var child = new Node("child");
        var newParent = new Node("new");
        oldParent.Nodes.Add(child);
        Graph.Root.Nodes.Add(oldParent);
        Graph.Root.Nodes.Add(newParent);
        child.Edges.Add(new Edge(child, newParent));
        Assert.IsTrue(child.GetBacking().FolderPath.Contains(oldParent.LocalId));

        oldParent.Nodes.Remove(child);

        Assert.IsFalse(oldParent.Nodes.Any(node => node.LocalId == "child"));
        Assert.IsNull(await Storage.Get(oldParent.GlobalId, child.LocalId));
        Assert.IsFalse(Directory.Exists(Path.Combine(StorageOptions.RootPath, "old", "child")));

        var reloadedNewParent = await Storage.Get(newParent.GlobalId);
        Assert.IsNotNull(reloadedNewParent);
        Assert.IsTrue(await reloadedNewParent.Nodes.AnyAsync(node => node.LocalId == "child"));
        Assert.IsNotNull(await Storage.Get(newParent.GlobalId, child.LocalId));
        Assert.IsTrue(Directory.Exists(Path.Combine(StorageOptions.RootPath, "new", "child")));
    }

    [TestMethod]
    public async Task EdgesRemove_ShouldThrowWhenHierarchyEdgeIsChildsOnlyEdge() {
        var parent = new Node("parent");
        var child = new Node("child");
        parent.Nodes.Add(child);
        Graph.Root.Nodes.Add(parent);

        var hierarchyEdge = parent.Edges.Single();

        Assert.ThrowsException<InvalidOperationException>(() => parent.Edges.Remove(hierarchyEdge));
        Assert.IsTrue(parent.Nodes.Any(node => node.LocalId == "child"));
        Assert.IsNotNull(await Storage.Get(parent.GlobalId, child.LocalId));
        Assert.IsTrue(child.GetBacking().Folder.Exists);
    }
}
