using GraphData.Compiler.Compilation;
using GraphData.Compiler.Model;
using GraphData.Compiler.Runtime.Chunking;
using GraphData.Compiler.Runtime.Domain;
using GraphData.Compiler.Runtime.Execution;
using GraphData.Compiler.Tensors;
using GraphData.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Runtime;

[RelevantTestClass]
public sealed class Direct3D12GraphProgramExecutorTests
{
    [TestMethod]
    [TestCategory("GPU")]
    public void Execute_ShouldMatchMessagePassingReferenceOnHardware()
    {
        var program = new GraphProgramCompiler().Compile(
            new TypedGraphSnapshot(
                [
                    new TypedNodeSnapshot("a", ["node"], [2f, -1f]),
                    new TypedNodeSnapshot("b", ["node"], [-3f, 4f])
                ],
                [new TypedRelationSnapshot("a-b", "edge", "a", "b", 0.5f)]),
            new MessagePassingSpecification(
                2,
                2,
                [new RelationTransform("edge", new DenseTensor([2, 2], [1f, 2f, 3f, 4f]))],
                new DenseTensor([2, 2], [1f, 0f, 0f, 1f]),
                new DenseTensor([2], [-1f, 1f]),
                ActivationKind.Relu));

        Direct3D12GraphProgramExecutor executor;
        try
        {
            executor = new Direct3D12GraphProgramExecutor();
        }
        catch (InvalidOperationException exception)
        {
            Assert.Inconclusive($"No hardware Direct3D 12 executor is available: {exception.Message}");
            return;
        }

        using (executor)
        {
            var output = executor.Execute(program).ToArray();

            CollectionAssert.AreEqual(new[] { 1f, 0f, 0f, 5f }, output);
        }
    }

    [TestMethod]
    [TestCategory("GPU")]
    public void DomainRuntime_ShouldCompileAndExecuteRawChunksOnHardware()
    {
        var a = new global::Node("a");
        var b = new global::Node("b");
        var c = new global::Node("c");
        a.Edges.Add(new global::Edge(a, b));
        b.Edges.Add(new global::Edge(b, c));

        Direct3D12GraphProgramExecutor executor;
        try
        {
            executor = new Direct3D12GraphProgramExecutor();
        }
        catch (InvalidOperationException exception)
        {
            Assert.Inconclusive($"No hardware Direct3D 12 executor is available: {exception.Message}");
            return;
        }

        using var runtime = new DomainGraphGpuRuntime(executor, ownsExecutor: true);
        var result = runtime.Execute(
            new Subgraph { Nodes = [c, b, a] },
            node => [node.LocalId.ToString() switch { "a" => 1f, "b" => 2f, _ => 3f }],
            new MessagePassingSpecification(
                1,
                1,
                [new RelationTransform("raw-edge", new DenseTensor([1, 1], [1f]))]),
            new GraphChunkingOptions { MaxCoreNodes = 1 });

        Assert.AreEqual(3, result.Chunks.Count);
        Assert.AreEqual(2f, OutputFor("a"));
        Assert.AreEqual(4f, OutputFor("b"));
        Assert.AreEqual(2f, OutputFor("c"));

        float OutputFor(string localId) => result.NodeOutputs
            .Single(pair => pair.Key.StartsWith(localId, StringComparison.Ordinal))
            .Value[0];
    }
}
