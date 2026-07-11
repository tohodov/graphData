using GraphData.Compiler.Backends.Hlsl;
using GraphData.Compiler.Compilation;
using GraphData.Compiler.Model;
using GraphData.Compiler.Tensors;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Compiler;

[RelevantTestClass]
public sealed class HlslMessagePassingEmitterTests
{
    [TestMethod]
    public void Emit_ShouldPackCanonicalBuffersAndDispatchMetadata()
    {
        var program = new GraphProgramCompiler().Compile(
            new TypedGraphSnapshot(
                [Node("c", 30), Node("a", 10), Node("b", 20)],
                [
                    new TypedRelationSnapshot("second", "link", "c", "b", 2),
                    new TypedRelationSnapshot("first", "link", "a", "b", 1)
                ]),
            new MessagePassingSpecification(
                1,
                2,
                [new RelationTransform("link", Tensor([1, 2], 3, 4))],
                Tensor([1, 2], 5, 6),
                Tensor([2], 7, 8),
                ActivationKind.Relu));

        var artifact = new HlslMessagePassingEmitter().Emit(program);

        Assert.AreEqual("MessagePassingMain", artifact.EntryPoint);
        Assert.AreEqual("cs_6_0", artifact.TargetProfile);
        Assert.AreEqual(3u, artifact.Constants.NodeCount);
        Assert.AreEqual(1u, artifact.Constants.InputWidth);
        Assert.AreEqual(2u, artifact.Constants.OutputWidth);
        Assert.AreEqual(1u, artifact.Constants.RelationCount);
        Assert.AreEqual(1u, artifact.Constants.HasSelfTransform);
        Assert.AreEqual(1u, artifact.Constants.HasBias);
        Assert.AreEqual(1u, artifact.Constants.Activation);
        Assert.AreEqual(1, artifact.Dispatch.GroupCountX);
        Assert.AreEqual(1, artifact.Dispatch.GroupCountY);
        Assert.IsTrue(artifact.Dispatch.HasWork);

        CollectionAssert.AreEqual(new uint[] { 0, 0, 2, 2 }, artifact.Buffers.RowOffsets.ToArray());
        CollectionAssert.AreEqual(new uint[] { 0, 2 }, artifact.Buffers.ColumnIndices.ToArray());
        CollectionAssert.AreEqual(new[] { 1f, 2f }, artifact.Buffers.EdgeWeights.ToArray());
        CollectionAssert.AreEqual(new[] { 3f, 4f }, artifact.Buffers.RelationTransforms.ToArray());
        CollectionAssert.AreEqual(new[] { 5f, 6f }, artifact.Buffers.SelfTransform.ToArray());
        CollectionAssert.AreEqual(new[] { 7f, 8f }, artifact.Buffers.Bias.ToArray());
        Assert.AreEqual(0u, artifact.Buffers.RelationDescriptors.Single().RowOffsetsBase);
        Assert.AreEqual(0u, artifact.Buffers.RelationDescriptors.Single().EdgeBase);
        Assert.AreEqual(0u, artifact.Buffers.RelationDescriptors.Single().TransformBase);
        Assert.AreEqual(2u, artifact.Buffers.RelationDescriptors.Single().NonZeroCount);
        Assert.AreEqual("t0", artifact.BufferMetadata.Single(buffer => buffer.Name == "NodeFeatures").RegisterName);
        Assert.AreEqual("u0", artifact.BufferMetadata.Single(buffer => buffer.Name == "Output").RegisterName);
        StringAssert.Contains(artifact.Source, "[numthreads(8, 8, 1)]");
        StringAssert.Contains(artifact.Source, "StructuredBuffer<uint> RowOffsets : register(t1);");
        StringAssert.Contains(artifact.Source, "RWStructuredBuffer<float> Output : register(u0);");
    }

    [TestMethod]
    public void Emit_ShouldPackRelationTypesInCanonicalOrder()
    {
        var program = new GraphProgramCompiler().Compile(
            new TypedGraphSnapshot(
                [Node("a", 1), Node("b", 2)],
                [
                    new TypedRelationSnapshot("z-edge", "z-type", "a", "b"),
                    new TypedRelationSnapshot("a-edge", "a-type", "b", "a")
                ]),
            new MessagePassingSpecification(
                1,
                1,
                [
                    new RelationTransform("z-type", Tensor([1, 1], 20)),
                    new RelationTransform("a-type", Tensor([1, 1], 10))
                ]));

        var buffers = new HlslMessagePassingEmitter().Emit(program).Buffers;

        CollectionAssert.AreEqual(new[] { "a-type", "z-type" }, buffers.RelationTypeKeys.ToArray());
        CollectionAssert.AreEqual(new uint[] { 0, 1, 1, 0, 0, 1 }, buffers.RowOffsets.ToArray());
        CollectionAssert.AreEqual(new uint[] { 1, 0 }, buffers.ColumnIndices.ToArray());
        CollectionAssert.AreEqual(new[] { 10f, 20f }, buffers.RelationTransforms.ToArray());
        Assert.AreEqual(0u, buffers.RelationDescriptors[0].RowOffsetsBase);
        Assert.AreEqual(3u, buffers.RelationDescriptors[1].RowOffsetsBase);
        Assert.AreEqual(0u, buffers.RelationDescriptors[0].EdgeBase);
        Assert.AreEqual(1u, buffers.RelationDescriptors[1].EdgeBase);
        Assert.AreEqual(0u, buffers.RelationDescriptors[0].TransformBase);
        Assert.AreEqual(1u, buffers.RelationDescriptors[1].TransformBase);
    }

    [TestMethod]
    public void Emit_ShouldDescribeDummyAllocationsForEmptyBuffers()
    {
        var program = new GraphProgramCompiler().Compile(
            new TypedGraphSnapshot([], []),
            new MessagePassingSpecification(1, 2, []));

        var artifact = new HlslMessagePassingEmitter().Emit(program);

        Assert.IsFalse(artifact.Dispatch.HasWork);
        Assert.AreEqual(0, artifact.Dispatch.GroupCountY);
        Assert.AreEqual(1, artifact.Buffers.NodeFeatures.Count);
        Assert.AreEqual(1, artifact.Buffers.RowOffsets.Count);
        Assert.AreEqual(1, artifact.Buffers.RelationDescriptors.Count);
        var nodeFeatures = artifact.BufferMetadata.Single(buffer => buffer.Name == "NodeFeatures");
        Assert.AreEqual(0, nodeFeatures.LogicalElementCount);
        Assert.AreEqual(1, nodeFeatures.AllocationElementCount);
        var output = artifact.BufferMetadata.Single(buffer => buffer.Name == "Output");
        Assert.AreEqual(0, output.LogicalElementCount);
        Assert.AreEqual(1, output.AllocationElementCount);
    }

    [TestMethod]
    public void Emit_ShouldEnforceDispatchDimensionLimit()
    {
        var maximumOutputWidth = HlslMessagePassingEmitter.MaxDispatchGroupCount
            * HlslMessagePassingEmitter.ThreadGroupSizeX;
        var supported = new GraphProgramCompiler().Compile(
            new TypedGraphSnapshot([Node("node", 1)], []),
            new MessagePassingSpecification(1, maximumOutputWidth, []));

        var artifact = new HlslMessagePassingEmitter().Emit(supported);

        Assert.AreEqual(
            HlslMessagePassingEmitter.MaxDispatchGroupCount,
            artifact.Dispatch.GroupCountX);

        var unsupported = new GraphProgramCompiler().Compile(
            new TypedGraphSnapshot([Node("node", 1)], []),
            new MessagePassingSpecification(1, maximumOutputWidth + 1, []));
        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
            new HlslMessagePassingEmitter().Emit(unsupported));
        StringAssert.Contains(exception.Message, "supports at most 65535");
    }

    private static TypedNodeSnapshot Node(string key, float feature) =>
        new(key, ["entity"], [feature]);

    private static DenseTensor Tensor(IReadOnlyList<int> shape, params float[] values) =>
        new(shape, values);
}
