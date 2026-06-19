using System.Linq;
using System.Threading.Tasks;
using Abstractions;
using GraphData.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Storage;

partial class GraphStorageContractTests {
    [TestMethod]
    [TestCategory(nameof(Node))]
    [TestCategory(nameof(IGraphStorage.Update))]
    public async Task NodeAttributes_ShouldPersistDictionaryMutations() {
        var node = await CreateNode("node");

        node.Attributes["type"] = "updated";
        node.Attributes["extra"] = "value";
        node.Attributes.Remove("created");

        var retrieved = (await Storage.Get(node.GlobalId)).Value!;
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