using GraphData.Compiler.Adapters;
using GraphData.Compiler.Compilation;
using GraphData.Compiler.Execution;
using GraphData.Compiler.Model;
using GraphData.Compiler.Tensors;
using GraphData.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Compiler;

[RelevantTestClass]
public sealed class RawSubgraphSnapshotAdapterTests
{
    [TestMethod]
    public void Create_ShouldLowerOneUndirectedEdgeToTwoDirectedRelations()
    {
        var first = new global::Node("first");
        var second = new global::Node("second");
        first.Edges.Add(new global::Edge(first, second));

        var snapshot = RawSubgraphSnapshotAdapter.Create(
            new Subgraph { Nodes = [second, first] },
            node => [node.LocalId == "first" ? 1f : 2f]);

        Assert.AreEqual(2, snapshot.Nodes.Count);
        Assert.AreEqual(2, snapshot.Relations.Count);
        var endpoints = snapshot.Relations
            .Select(static relation => $"{relation.SourceKey}->{relation.TargetKey}")
            .ToArray();
        var firstKey = snapshot.Nodes.Single(node => node.Features[0] == 1).Key;
        var secondKey = snapshot.Nodes.Single(node => node.Features[0] == 2).Key;
        CollectionAssert.AreEquivalent(
            new[] { $"{firstKey}->{secondKey}", $"{secondKey}->{firstKey}" },
            endpoints);
    }

    [TestMethod]
    public void Create_ShouldIgnoreEdgesLeavingTheSnapshot()
    {
        var included = new global::Node("included");
        var external = new global::Node("external");
        included.Edges.Add(new global::Edge(included, external));

        var snapshot = RawSubgraphSnapshotAdapter.Create(
            new Subgraph { Nodes = [included] },
            static _ => [1f]);

        Assert.AreEqual(1, snapshot.Nodes.Count);
        Assert.AreEqual(0, snapshot.Relations.Count);
    }

    [TestMethod]
    public void Create_ShouldRejectDuplicateNodeWrappers()
    {
        var node = new global::Node("duplicate");

        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
            RawSubgraphSnapshotAdapter.Create(
                new Subgraph { Nodes = [node, node] },
                static _ => [1f]));

        StringAssert.Contains(exception.Message, "more than one node wrapper");
    }

    [TestMethod]
    public void Create_ShouldReportDuplicateNodeWrappersInCanonicalOrder()
    {
        var first = new global::Node("first");
        var second = new global::Node("second");

        var firstOrder = Assert.ThrowsException<InvalidOperationException>(() =>
            RawSubgraphSnapshotAdapter.Create(
                new Subgraph { Nodes = [second, second, first, first] },
                static _ => [1f]));
        var secondOrder = Assert.ThrowsException<InvalidOperationException>(() =>
            RawSubgraphSnapshotAdapter.Create(
                new Subgraph { Nodes = [first, first, second, second] },
                static _ => [1f]));

        Assert.AreEqual(firstOrder.Message, secondOrder.Message);
    }

    [TestMethod]
    public void Create_ShouldCompileAndExecuteRawSubgraphEndToEnd()
    {
        var source = new global::Node("source");
        var target = new global::Node("target");
        source.Edges.Add(new global::Edge(source, target));
        var snapshot = RawSubgraphSnapshotAdapter.Create(
            new Subgraph { Nodes = [target, source] },
            node => [node.LocalId == "source" ? 2f : 0f]);
        var program = new GraphProgramCompiler().Compile(
            snapshot,
            new MessagePassingSpecification(
                1,
                1,
                [new RelationTransform("raw-edge", new DenseTensor([1, 1], [1f]))]));

        var output = new CpuMessagePassingExecutor().Execute(program);
        var targetKey = snapshot.Nodes.Single(node => node.Features[0] == 0).Key;

        Assert.AreEqual(2f, output[program.GetNodeIndex(targetKey), 0]);
    }
}
