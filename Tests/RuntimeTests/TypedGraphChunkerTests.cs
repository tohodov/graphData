using GraphData.Compiler.Model;
using GraphData.Compiler.Runtime.Chunking;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Runtime;

[RelevantTestClass]
public sealed class TypedGraphChunkerTests
{
    [TestMethod]
    public void Create_ShouldAddIncomingSourcesAsHaloAndAssignEveryTargetOnce()
    {
        var snapshot = new TypedGraphSnapshot(
            [Node("a", 1), Node("b", 2), Node("c", 3)],
            [
                new TypedRelationSnapshot("a-b", "edge", "a", "b"),
                new TypedRelationSnapshot("b-c", "edge", "b", "c")
            ]);

        var chunks = new TypedGraphChunker().Create(snapshot, new GraphChunkingOptions { MaxCoreNodes = 1 });

        Assert.AreEqual(3, chunks.Count);
        CollectionAssert.AreEqual(new[] { "a" }, chunks[0].CoreNodeKeys.ToArray());
        CollectionAssert.AreEqual(new[] { "b" }, chunks[1].CoreNodeKeys.ToArray());
        CollectionAssert.AreEqual(new[] { "c" }, chunks[2].CoreNodeKeys.ToArray());
        CollectionAssert.AreEqual(new[] { "a", "b" }, chunks[1].Snapshot.Nodes.Select(static node => node.Key).ToArray());
        CollectionAssert.AreEqual(new[] { "b", "c" }, chunks[2].Snapshot.Nodes.Select(static node => node.Key).ToArray());
        CollectionAssert.AreEqual(new[] { "a-b" }, chunks[1].Snapshot.Relations.Select(static relation => relation.Key).ToArray());
        CollectionAssert.AreEqual(new[] { "b-c" }, chunks[2].Snapshot.Relations.Select(static relation => relation.Key).ToArray());
    }

    private static TypedNodeSnapshot Node(string key, float value) =>
        new(key, ["node"], [value]);
}
