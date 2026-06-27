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
}
