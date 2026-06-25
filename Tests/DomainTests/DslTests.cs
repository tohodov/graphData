using Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[RelevantTestClass]
public sealed class DslTests : GraphServiceTests {
    [TestMethod]
    public async Task LINQ_Traverse() {
        var weaponType = await Service.CreateNode("weapon", Service.TypesRoot.GlobalId);
        var node = ((Node)await Service.CreateNode("ak47", Service.TypesRoot.GlobalId))!;
        await Service.CreateNode("m16", Service.TypesRoot.GlobalId);
        await Service.CreateNode("mp5", Service.TypesRoot.GlobalId);

        var type = node.Incidences
            .OfType<InstanceOf.InstanceEnd>()
            .Select(x => x.Type)
            .First();
        Assert.AreEqual<NodeLocalId>("weapon", type.LocalId);

        var metaType = type.Incidences
            .OfType<InstanceOf.InstanceEnd>()
            .Select(x => x.Type)
            .First();
        Assert.IsNull(metaType);

        var instances = type.Incidences
            .OfType<InstanceOf.TypeEnd>()
            .Select(x => x.Instance)
            .ToArray();
        CollectionAssert.AreEquivalent(new NodeLocalId[] { "ak47", "m16", "mp5" }, instances.Select(x => x.LocalId).ToArray());
    }
}
