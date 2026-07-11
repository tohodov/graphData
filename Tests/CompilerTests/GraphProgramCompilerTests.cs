using GraphData.Compiler.Compilation;
using GraphData.Compiler.Diagnostics;
using GraphData.Compiler.Execution;
using GraphData.Compiler.Model;
using GraphData.Compiler.Tensors;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Compiler;

[RelevantTestClass]
public sealed class GraphProgramCompilerTests
{
    private readonly GraphProgramCompiler compiler = new();

    [TestMethod]
    public void Compile_ShouldProduceCanonicalCsrIndependentOfInputOrder()
    {
        var first = compiler.Compile(
            Snapshot(
                [Node("c", 30), Node("a", 10), Node("b", 20)],
                [Relation("second", "c", "b", 2), Relation("first", "a", "b", 1)]),
            Specification());
        var second = compiler.Compile(
            Snapshot(
                [Node("b", 20), Node("a", 10), Node("c", 30)],
                [Relation("first", "a", "b", 1), Relation("second", "c", "b", 2)]),
            Specification());

        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, first.NodeKeysByIndex.ToArray());
        CollectionAssert.AreEqual(new[] { 0, 0, 2, 2 }, first.Plan.Relations.Single().Adjacency.RowOffsets.ToArray());
        CollectionAssert.AreEqual(new[] { 0, 2 }, first.Plan.Relations.Single().Adjacency.ColumnIndices.ToArray());
        CollectionAssert.AreEqual(new[] { 1f, 2f }, first.Plan.Relations.Single().Adjacency.Values.ToArray());
        CollectionAssert.AreEqual(
            first.InitialNodeFeatures.ToArray(),
            second.InitialNodeFeatures.ToArray());
        CollectionAssert.AreEqual(
            first.Plan.Relations.Single().Adjacency.CopyRowOffsets(),
            second.Plan.Relations.Single().Adjacency.CopyRowOffsets());
        CollectionAssert.AreEqual(
            first.Plan.Relations.Single().Adjacency.CopyColumnIndices(),
            second.Plan.Relations.Single().Adjacency.CopyColumnIndices());
        CollectionAssert.AreEqual(
            first.Plan.Relations.Single().Adjacency.CopyValues(),
            second.Plan.Relations.Single().Adjacency.CopyValues());
    }

    [TestMethod]
    public void Compile_ShouldPreserveParallelRelations()
    {
        var program = compiler.Compile(
            Snapshot(
                [Node("a", 2), Node("b", 0)],
                [Relation("r1", "a", "b", 1), Relation("r2", "a", "b", 3)]),
            Specification());

        var adjacency = program.Plan.Relations.Single().Adjacency;
        CollectionAssert.AreEqual(new[] { 0, 0, 2 }, adjacency.RowOffsets.ToArray());
        CollectionAssert.AreEqual(new[] { 0, 0 }, adjacency.ColumnIndices.ToArray());
        CollectionAssert.AreEqual(new[] { 1f, 3f }, adjacency.Values.ToArray());
        CollectionAssert.AreEqual(
            new[] { "r1", "r2" },
            program.Plan.Relations.Single().RelationKeysByEntry.ToArray());

        var output = new CpuMessagePassingExecutor().Execute(program);
        Assert.AreEqual(8f, output[1, 0]);
    }

    [TestMethod]
    public void Execute_ShouldApplyBiasSelfRelationsAndRelu()
    {
        var snapshot = new TypedGraphSnapshot(
            [
                new TypedNodeSnapshot("a", ["entity"], [2f, -1f]),
                new TypedNodeSnapshot("b", ["entity"], [-3f, 4f])
            ],
            [new TypedRelationSnapshot("a-to-b", "link", "a", "b", 0.5f)]);
        var specification = new MessagePassingSpecification(
            2,
            2,
            [new RelationTransform("link", Matrix(2, 2, 1, 2, 3, 4))],
            Matrix(2, 2, 1, 0, 0, 1),
            new DenseTensor([2], [-1, 1]),
            ActivationKind.Relu);

        var output = new CpuMessagePassingExecutor().Execute(compiler.Compile(snapshot, specification));

        CollectionAssert.AreEqual(new[] { 1f, 0f, 0f, 5f }, output.ToArray());
    }

    [TestMethod]
    public void Execute_ShouldUseSeparateTransformsForRelationTypes()
    {
        var program = compiler.Compile(
            new TypedGraphSnapshot(
                [Node("target", 0), Node("left", 2), Node("right", 3)],
                [
                    new TypedRelationSnapshot("z-edge", "z-type", "left", "target"),
                    new TypedRelationSnapshot("a-edge", "a-type", "right", "target")
                ]),
            new MessagePassingSpecification(
                1,
                1,
                [
                    new RelationTransform("z-type", Matrix(1, 1, 10)),
                    new RelationTransform("a-type", Matrix(1, 1, 100))
                ]));

        CollectionAssert.AreEqual(
            new[] { "a-type", "z-type" },
            program.Plan.Relations.Select(static relation => relation.TypeKey).ToArray());
        var output = new CpuMessagePassingExecutor().Execute(program);
        Assert.AreEqual(320f, output[program.GetNodeIndex("target"), 0]);
    }

    [TestMethod]
    public void Compile_ShouldReportMissingRelationTransform()
    {
        var exception = Assert.ThrowsException<GraphCompilationException>(() => compiler.Compile(
            Snapshot([Node("a", 1), Node("b", 2)], [Relation("r", "a", "b")]),
            new MessagePassingSpecification(1, 1, [])));

        Assert.IsTrue(exception.Diagnostics.Any(static diagnostic =>
            diagnostic.Code == "spec.relation_transform.missing"));
    }

    [TestMethod]
    public void Compile_ShouldAggregateInvalidSnapshotDiagnostics()
    {
        var exception = Assert.ThrowsException<GraphCompilationException>(() => compiler.Compile(
            new TypedGraphSnapshot(
                [
                    new TypedNodeSnapshot("a", ["entity"], [float.NaN]),
                    Node("a", 1)
                ],
                [
                    new TypedRelationSnapshot("r", "link", "a", "missing"),
                    new TypedRelationSnapshot("r", "link", "a", "a", float.PositiveInfinity)
                ]),
            new MessagePassingSpecification(
                1,
                1,
                [
                    new RelationTransform("link", Matrix(1, 2, 1, 2)),
                    new RelationTransform("link", Matrix(1, 1, 1))
                ],
                Matrix(2, 1, 1, 2),
                new DenseTensor([2], [1, 2]))));

        var codes = exception.Diagnostics.Select(static diagnostic => diagnostic.Code).ToArray();
        CollectionAssert.IsSubsetOf(
            new[] {
                "snapshot.node.feature.non_finite",
                "snapshot.node.key.duplicate",
                "snapshot.relation.key.duplicate",
                "snapshot.relation.target.missing",
                "snapshot.relation.weight.non_finite",
                "spec.relation_transform.shape",
                "spec.relation_transform.type.duplicate",
                "spec.self_transform.shape",
                "spec.bias.shape"
            },
            codes);
    }

    [TestMethod]
    public void Compile_ShouldCopyMutableSnapshotData()
    {
        var sourceFeatures = new[] { 2f };
        var sourceTypes = new[] { "entity" };
        var sourceNodes = new List<TypedNodeSnapshot> {
            new("a", sourceTypes, sourceFeatures),
            Node("b", 0)
        };
        var sourceRelations = new List<TypedRelationSnapshot> {
            Relation("r", "a", "b")
        };
        var program = compiler.Compile(
            new TypedGraphSnapshot(sourceNodes, sourceRelations),
            Specification());

        sourceFeatures[0] = 100;
        sourceTypes[0] = "changed";
        sourceNodes.Clear();
        sourceRelations.Clear();

        CollectionAssert.AreEqual(new[] { 2f, 0f }, program.InitialNodeFeatures.ToArray());
        CollectionAssert.AreEqual(new[] { "entity" }, program.NodeTypesByIndex[0].ToArray());
        Assert.AreEqual(2f, new CpuMessagePassingExecutor().Execute(program)[1, 0]);
    }

    [TestMethod]
    public void Execute_ShouldSupportEmptyGraph()
    {
        var program = compiler.Compile(
            new TypedGraphSnapshot([], []),
            new MessagePassingSpecification(3, 2, []));

        var output = new CpuMessagePassingExecutor().Execute(program);

        CollectionAssert.AreEqual(new[] { 0, 2 }, output.Shape.ToArray());
        Assert.AreEqual(0, output.ElementCount);
    }

    [TestMethod]
    public void Compile_ShouldCanonicalizeNodeTypeSets()
    {
        var program = compiler.Compile(
            new TypedGraphSnapshot(
                [new TypedNodeSnapshot("node", ["z-type", "a-type", "z-type"], [1f])],
                []),
            new MessagePassingSpecification(1, 1, []));

        CollectionAssert.AreEqual(
            new[] { "a-type", "z-type" },
            program.NodeTypesByIndex.Single().ToArray());
    }

    private static TypedGraphSnapshot Snapshot(
        IReadOnlyList<TypedNodeSnapshot> nodes,
        IReadOnlyList<TypedRelationSnapshot> relations) => new(nodes, relations);

    private static TypedNodeSnapshot Node(string key, float feature) =>
        new(key, ["entity"], [feature]);

    private static TypedRelationSnapshot Relation(
        string key,
        string source,
        string target,
        float weight = 1) => new(key, "link", source, target, weight);

    private static MessagePassingSpecification Specification() =>
        new(1, 1, [new RelationTransform("link", Matrix(1, 1, 1))]);

    private static DenseTensor Matrix(int rows, int columns, params float[] values) =>
        new([rows, columns], values);
}
