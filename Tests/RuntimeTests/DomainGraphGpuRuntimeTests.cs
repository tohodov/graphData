using GraphData.Compiler.Model;
using GraphData.Compiler.Runtime.Chunking;
using GraphData.Compiler.Runtime.Domain;
using GraphData.Compiler.Runtime.Execution;
using GraphData.Compiler.Tensors;
using GraphData.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Runtime;

[RelevantTestClass]
public sealed class DomainGraphGpuRuntimeTests
{
    [TestMethod]
    public void Execute_ShouldMergeCoreOutputsAcrossRawGraphChunks()
    {
        var a = new global::Node("a");
        var b = new global::Node("b");
        var c = new global::Node("c");
        a.Edges.Add(new global::Edge(a, b));
        b.Edges.Add(new global::Edge(b, c));
        using var runtime = new DomainGraphGpuRuntime(new CpuGraphProgramExecutor(), ownsExecutor: true);

        var result = runtime.Execute(
            new Subgraph { Nodes = [c, a, b] },
            node => [node.LocalId.ToString() switch { "a" => 1f, "b" => 2f, _ => 3f }],
            new MessagePassingSpecification(
                1,
                1,
                [new RelationTransform("raw-edge", new DenseTensor([1, 1], [1f]))]),
            new GraphChunkingOptions { MaxCoreNodes = 1 });

        Assert.AreEqual(3, result.Chunks.Count);
        Assert.AreEqual(3, result.NodeOutputs.Count);
        Assert.AreEqual(2f, OutputFor("a"));
        Assert.AreEqual(4f, OutputFor("b"));
        Assert.AreEqual(2f, OutputFor("c"));

        float OutputFor(string localId) => result.NodeOutputs.Single(pair => pair.Key.StartsWith(localId, StringComparison.Ordinal)).Value[0];
    }
}
