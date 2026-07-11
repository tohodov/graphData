using GraphData.Compiler.Ir;
using GraphData.Compiler.Model;

namespace GraphData.Compiler.Backends.Hlsl;

/// <summary>
/// Emits a single, race-free compute shader for the reference message-passing
/// semantics. Each shader invocation owns one (target node, output feature)
/// pair and walks the canonical incoming-edge CSR rows in plan order.
/// </summary>
public sealed class HlslMessagePassingEmitter
{
    public const string DefaultEntryPoint = "MessagePassingMain";
    public const string DefaultTargetProfile = "cs_6_0";
    public const int ThreadGroupSizeX = 8;
    public const int ThreadGroupSizeY = 8;
    public const int ThreadGroupSizeZ = 1;
    public const int MaxDispatchGroupCount = 65_535;

    public HlslMessagePassingArtifact Emit(CompiledMessagePassingProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        Validate(program);

        var packed = Pack(program);
        var plan = program.Plan;
        var constants = new HlslMessagePassingConstants(
            checked((uint)plan.NodeCount),
            checked((uint)plan.InputWidth),
            checked((uint)plan.OutputWidth),
            checked((uint)plan.Relations.Count),
            plan.SelfTransform is not null,
            plan.Bias is not null,
            EncodeActivation(plan.Activation));
        var dispatch = new HlslDispatchMetadata(
            ThreadGroupSizeX,
            ThreadGroupSizeY,
            ThreadGroupSizeZ,
            GetDispatchGroupCount(plan.OutputWidth, ThreadGroupSizeX, "output width"),
            GetDispatchGroupCount(plan.NodeCount, ThreadGroupSizeY, "node count"),
            1);
        var metadata = CreateBufferMetadata(packed);

        return new HlslMessagePassingArtifact(
            CreateShaderSource(),
            DefaultEntryPoint,
            DefaultTargetProfile,
            constants,
            new HlslConstantBufferMetadata(
                "MessagePassingConstants",
                0,
                HlslMessagePassingConstants.SizeInBytes),
            dispatch,
            packed.Buffers,
            metadata);
    }

    private static PackedBuffersWithLogicalCounts Pack(CompiledMessagePassingProgram program)
    {
        var plan = program.Plan;
        var rowOffsets = new List<uint>();
        var columnIndices = new List<uint>();
        var edgeWeights = new List<float>();
        var relationTransforms = new List<float>();
        var descriptors = new List<HlslRelationDescriptor>();
        var relationTypeKeys = new List<string>();

        foreach (var relation in plan.Relations)
        {
            var rowOffsetsBase = checked((uint)rowOffsets.Count);
            var edgeBase = checked((uint)columnIndices.Count);
            var transformBase = checked((uint)relationTransforms.Count);

            foreach (var offset in relation.Adjacency.RowOffsets)
                rowOffsets.Add(checked((uint)offset));
            foreach (var column in relation.Adjacency.ColumnIndices)
                columnIndices.Add(checked((uint)column));
            edgeWeights.AddRange(relation.Adjacency.Values);
            relationTransforms.AddRange(relation.Transform.ToArray());
            descriptors.Add(new HlslRelationDescriptor(
                rowOffsetsBase,
                edgeBase,
                transformBase,
                checked((uint)relation.Adjacency.NonZeroCount)));
            relationTypeKeys.Add(relation.TypeKey);
        }

        var nodeFeatures = program.InitialNodeFeatures.ToArray();
        var selfTransform = plan.SelfTransform?.ToArray() ?? [];
        var bias = plan.Bias?.ToArray() ?? [];
        var logicalCounts = new LogicalBufferCounts(
            nodeFeatures.Length,
            rowOffsets.Count,
            columnIndices.Count,
            edgeWeights.Count,
            relationTransforms.Count,
            descriptors.Count,
            selfTransform.Length,
            bias.Length,
            checked(plan.NodeCount * plan.OutputWidth));

        return new PackedBuffersWithLogicalCounts(
            new HlslPackedMessagePassingBuffers(
                EnsureBindable(nodeFeatures),
                EnsureBindable(rowOffsets),
                EnsureBindable(columnIndices),
                EnsureBindable(edgeWeights),
                EnsureBindable(relationTransforms),
                EnsureBindable(descriptors),
                EnsureBindable(selfTransform),
                EnsureBindable(bias),
                relationTypeKeys),
            logicalCounts);
    }

    private static IReadOnlyList<HlslBufferMetadata> CreateBufferMetadata(
        PackedBuffersWithLogicalCounts packed)
    {
        var buffers = packed.Buffers;
        var counts = packed.LogicalCounts;
        return [
            ReadOnly("NodeFeatures", 0, HlslBufferElementKind.Float32,
                counts.NodeFeatures, buffers.NodeFeatures.Count, sizeof(float)),
            ReadOnly("RowOffsets", 1, HlslBufferElementKind.UInt32,
                counts.RowOffsets, buffers.RowOffsets.Count, sizeof(uint)),
            ReadOnly("ColumnIndices", 2, HlslBufferElementKind.UInt32,
                counts.ColumnIndices, buffers.ColumnIndices.Count, sizeof(uint)),
            ReadOnly("EdgeWeights", 3, HlslBufferElementKind.Float32,
                counts.EdgeWeights, buffers.EdgeWeights.Count, sizeof(float)),
            ReadOnly("RelationTransforms", 4, HlslBufferElementKind.Float32,
                counts.RelationTransforms, buffers.RelationTransforms.Count, sizeof(float)),
            ReadOnly("RelationDescriptors", 5, HlslBufferElementKind.UInt32x4,
                counts.RelationDescriptors, buffers.RelationDescriptors.Count, HlslRelationDescriptor.SizeInBytes),
            ReadOnly("SelfTransform", 6, HlslBufferElementKind.Float32,
                counts.SelfTransform, buffers.SelfTransform.Count, sizeof(float)),
            ReadOnly("Bias", 7, HlslBufferElementKind.Float32,
                counts.Bias, buffers.Bias.Count, sizeof(float)),
            new HlslBufferMetadata(
                "Output",
                HlslBufferResourceKind.UnorderedAccess,
                0,
                HlslBufferElementKind.Float32,
                counts.Output,
                Math.Max(1, counts.Output),
                sizeof(float))
        ];

        static HlslBufferMetadata ReadOnly(
            string name,
            int register,
            HlslBufferElementKind elementKind,
            int logicalCount,
            int allocationCount,
            int stride) => new(
                name,
                HlslBufferResourceKind.ShaderResource,
                register,
                elementKind,
                logicalCount,
                allocationCount,
                stride);
    }

    private static void Validate(CompiledMessagePassingProgram program)
    {
        var plan = program.Plan;
        if (!program.InitialNodeFeatures.Shape.SequenceEqual([plan.NodeCount, plan.InputWidth]))
            throw new InvalidOperationException(
                $"Initial node features must have shape [{plan.NodeCount}, {plan.InputWidth}].");

        string? previousTypeKey = null;
        foreach (var relation in plan.Relations)
        {
            if (previousTypeKey is not null
                && StringComparer.Ordinal.Compare(previousTypeKey, relation.TypeKey) >= 0)
                throw new InvalidOperationException(
                    "Relation message plans must have unique type keys in ordinal order.");
            previousTypeKey = relation.TypeKey;

            if (relation.Adjacency.RowCount != plan.NodeCount
                || relation.Adjacency.ColumnCount != plan.NodeCount)
                throw new InvalidOperationException(
                    $"Relation '{relation.TypeKey}' adjacency must have shape [{plan.NodeCount}, {plan.NodeCount}].");
            if (!relation.Transform.Shape.SequenceEqual([plan.InputWidth, plan.OutputWidth]))
                throw new InvalidOperationException(
                    $"Relation '{relation.TypeKey}' transform must have shape [{plan.InputWidth}, {plan.OutputWidth}].");
        }

        if (plan.SelfTransform is not null
            && !plan.SelfTransform.Shape.SequenceEqual([plan.InputWidth, plan.OutputWidth]))
            throw new InvalidOperationException(
                $"Self transform must have shape [{plan.InputWidth}, {plan.OutputWidth}].");
        if (plan.Bias is not null && !plan.Bias.Shape.SequenceEqual([plan.OutputWidth]))
            throw new InvalidOperationException($"Bias must have shape [{plan.OutputWidth}].");

        _ = EncodeActivation(plan.Activation);
    }

    private static uint EncodeActivation(ActivationKind activation) => activation switch
    {
        ActivationKind.Identity => 0u,
        ActivationKind.Relu => 1u,
        _ => throw new InvalidOperationException($"Unsupported activation '{activation}'.")
    };

    private static int DivideRoundUp(int value, int divisor) =>
        value == 0 ? 0 : checked(((value - 1) / divisor) + 1);

    private static int GetDispatchGroupCount(int value, int threadGroupSize, string dimensionName)
    {
        var groupCount = DivideRoundUp(value, threadGroupSize);
        if (groupCount > MaxDispatchGroupCount)
            throw new InvalidOperationException(
                $"The {dimensionName} '{value}' requires {groupCount} dispatch groups, "
                + $"but this HLSL backend supports at most {MaxDispatchGroupCount} per dimension.");
        return groupCount;
    }

    private static T[] EnsureBindable<T>(IEnumerable<T> values)
    {
        var packed = values.ToArray();
        return packed.Length == 0 ? [default!] : packed;
    }

    private static string CreateShaderSource() => string.Join('\n', [
        "struct RelationDescriptor",
        "{",
        "    uint RowOffsetsBase;",
        "    uint EdgeBase;",
        "    uint TransformBase;",
        "    uint NonZeroCount;",
        "};",
        "",
        "cbuffer MessagePassingConstants : register(b0)",
        "{",
        "    uint NodeCount;",
        "    uint InputWidth;",
        "    uint OutputWidth;",
        "    uint RelationCount;",
        "    uint HasSelfTransform;",
        "    uint HasBias;",
        "    uint Activation;",
        "    uint Reserved;",
        "};",
        "",
        "StructuredBuffer<float> NodeFeatures : register(t0);",
        "StructuredBuffer<uint> RowOffsets : register(t1);",
        "StructuredBuffer<uint> ColumnIndices : register(t2);",
        "StructuredBuffer<float> EdgeWeights : register(t3);",
        "StructuredBuffer<float> RelationTransforms : register(t4);",
        "StructuredBuffer<RelationDescriptor> RelationDescriptors : register(t5);",
        "StructuredBuffer<float> SelfTransform : register(t6);",
        "StructuredBuffer<float> Bias : register(t7);",
        "RWStructuredBuffer<float> Output : register(u0);",
        "",
        $"[numthreads({ThreadGroupSizeX}, {ThreadGroupSizeY}, {ThreadGroupSizeZ})]",
        $"void {DefaultEntryPoint}(uint3 dispatchThreadId : SV_DispatchThreadID)",
        "{",
        "    const uint outputFeature = dispatchThreadId.x;",
        "    const uint target = dispatchThreadId.y;",
        "    if (target >= NodeCount || outputFeature >= OutputWidth)",
        "        return;",
        "",
        "    precise float value = HasBias != 0u ? Bias[outputFeature] : 0.0f;",
        "",
        "    if (HasSelfTransform != 0u)",
        "    {",
        "        const uint featureBase = target * InputWidth;",
        "        for (uint inputFeature = 0u; inputFeature < InputWidth; inputFeature++)",
        "        {",
        "            const uint transformIndex = inputFeature * OutputWidth + outputFeature;",
        "            value = value + NodeFeatures[featureBase + inputFeature] * SelfTransform[transformIndex];",
        "        }",
        "    }",
        "",
        "    for (uint relationIndex = 0u; relationIndex < RelationCount; relationIndex++)",
        "    {",
        "        const RelationDescriptor relation = RelationDescriptors[relationIndex];",
        "        const uint rowStart = RowOffsets[relation.RowOffsetsBase + target];",
        "        const uint rowEnd = RowOffsets[relation.RowOffsetsBase + target + 1u];",
        "        for (uint localEdge = rowStart; localEdge < rowEnd; localEdge++)",
        "        {",
        "            const uint edgeIndex = relation.EdgeBase + localEdge;",
        "            const uint source = ColumnIndices[edgeIndex];",
        "            const float edgeWeight = EdgeWeights[edgeIndex];",
        "            const uint featureBase = source * InputWidth;",
        "            for (uint inputFeature = 0u; inputFeature < InputWidth; inputFeature++)",
        "            {",
        "                const uint transformIndex = relation.TransformBase",
        "                    + inputFeature * OutputWidth",
        "                    + outputFeature;",
        "                value = value + (edgeWeight * NodeFeatures[featureBase + inputFeature])",
        "                    * RelationTransforms[transformIndex];",
        "            }",
        "        }",
        "    }",
        "",
        "    if (Activation == 1u)",
        "        value = max(0.0f, value);",
        "",
        "    Output[target * OutputWidth + outputFeature] = value;",
        "}",
        ""
    ]);

    private sealed record LogicalBufferCounts(
        int NodeFeatures,
        int RowOffsets,
        int ColumnIndices,
        int EdgeWeights,
        int RelationTransforms,
        int RelationDescriptors,
        int SelfTransform,
        int Bias,
        int Output);

    private sealed record PackedBuffersWithLogicalCounts(
        HlslPackedMessagePassingBuffers Buffers,
        LogicalBufferCounts LogicalCounts);
}
