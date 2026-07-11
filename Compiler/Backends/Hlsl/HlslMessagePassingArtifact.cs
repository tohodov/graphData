using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace GraphData.Compiler.Backends.Hlsl;

public enum HlslBufferResourceKind
{
    ShaderResource,
    UnorderedAccess
}

public enum HlslBufferElementKind
{
    Float32,
    UInt32,
    UInt32x4
}

public sealed record HlslBufferMetadata(
    string Name,
    HlslBufferResourceKind ResourceKind,
    int Register,
    HlslBufferElementKind ElementKind,
    int LogicalElementCount,
    int AllocationElementCount,
    int StrideInBytes)
{
    public int AllocationSizeInBytes => checked(AllocationElementCount * StrideInBytes);

    public string RegisterName => ResourceKind switch
    {
        HlslBufferResourceKind.ShaderResource => $"t{Register}",
        HlslBufferResourceKind.UnorderedAccess => $"u{Register}",
        _ => throw new InvalidOperationException($"Unsupported HLSL resource kind '{ResourceKind}'.")
    };
}

public sealed record HlslConstantBufferMetadata(
    string Name,
    int Register,
    int SizeInBytes);

public sealed record HlslDispatchMetadata(
    int ThreadGroupSizeX,
    int ThreadGroupSizeY,
    int ThreadGroupSizeZ,
    int GroupCountX,
    int GroupCountY,
    int GroupCountZ)
{
    public bool HasWork => GroupCountX > 0 && GroupCountY > 0 && GroupCountZ > 0;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct HlslMessagePassingConstants
{
    public const int SizeInBytes = 8 * sizeof(uint);

    public readonly uint NodeCount;
    public readonly uint InputWidth;
    public readonly uint OutputWidth;
    public readonly uint RelationCount;
    public readonly uint HasSelfTransform;
    public readonly uint HasBias;
    public readonly uint Activation;
    public readonly uint Reserved;

    internal HlslMessagePassingConstants(
        uint nodeCount,
        uint inputWidth,
        uint outputWidth,
        uint relationCount,
        bool hasSelfTransform,
        bool hasBias,
        uint activation)
    {
        NodeCount = nodeCount;
        InputWidth = inputWidth;
        OutputWidth = outputWidth;
        RelationCount = relationCount;
        HasSelfTransform = hasSelfTransform ? 1u : 0u;
        HasBias = hasBias ? 1u : 0u;
        Activation = activation;
        Reserved = 0;
    }
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct HlslRelationDescriptor
{
    public const int SizeInBytes = 4 * sizeof(uint);

    public readonly uint RowOffsetsBase;
    public readonly uint EdgeBase;
    public readonly uint TransformBase;
    public readonly uint NonZeroCount;

    internal HlslRelationDescriptor(
        uint rowOffsetsBase,
        uint edgeBase,
        uint transformBase,
        uint nonZeroCount)
    {
        RowOffsetsBase = rowOffsetsBase;
        EdgeBase = edgeBase;
        TransformBase = transformBase;
        NonZeroCount = nonZeroCount;
    }
}

public sealed class HlslPackedMessagePassingBuffers
{
    public IReadOnlyList<float> NodeFeatures { get; }
    public IReadOnlyList<uint> RowOffsets { get; }
    public IReadOnlyList<uint> ColumnIndices { get; }
    public IReadOnlyList<float> EdgeWeights { get; }
    public IReadOnlyList<float> RelationTransforms { get; }
    public IReadOnlyList<HlslRelationDescriptor> RelationDescriptors { get; }
    public IReadOnlyList<float> SelfTransform { get; }
    public IReadOnlyList<float> Bias { get; }
    public IReadOnlyList<string> RelationTypeKeys { get; }

    internal HlslPackedMessagePassingBuffers(
        IEnumerable<float> nodeFeatures,
        IEnumerable<uint> rowOffsets,
        IEnumerable<uint> columnIndices,
        IEnumerable<float> edgeWeights,
        IEnumerable<float> relationTransforms,
        IEnumerable<HlslRelationDescriptor> relationDescriptors,
        IEnumerable<float> selfTransform,
        IEnumerable<float> bias,
        IEnumerable<string> relationTypeKeys)
    {
        NodeFeatures = ReadOnly(nodeFeatures);
        RowOffsets = ReadOnly(rowOffsets);
        ColumnIndices = ReadOnly(columnIndices);
        EdgeWeights = ReadOnly(edgeWeights);
        RelationTransforms = ReadOnly(relationTransforms);
        RelationDescriptors = ReadOnly(relationDescriptors);
        SelfTransform = ReadOnly(selfTransform);
        Bias = ReadOnly(bias);
        RelationTypeKeys = ReadOnly(relationTypeKeys);
    }

    private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> values) =>
        new ReadOnlyCollection<T>(values.ToArray());
}

public sealed class HlslMessagePassingArtifact
{
    public string Source { get; }
    public string EntryPoint { get; }
    public string TargetProfile { get; }
    public HlslMessagePassingConstants Constants { get; }
    public HlslConstantBufferMetadata ConstantBuffer { get; }
    public HlslDispatchMetadata Dispatch { get; }
    public HlslPackedMessagePassingBuffers Buffers { get; }
    public IReadOnlyList<HlslBufferMetadata> BufferMetadata { get; }

    internal HlslMessagePassingArtifact(
        string source,
        string entryPoint,
        string targetProfile,
        HlslMessagePassingConstants constants,
        HlslConstantBufferMetadata constantBuffer,
        HlslDispatchMetadata dispatch,
        HlslPackedMessagePassingBuffers buffers,
        IEnumerable<HlslBufferMetadata> bufferMetadata)
    {
        Source = source;
        EntryPoint = entryPoint;
        TargetProfile = targetProfile;
        Constants = constants;
        ConstantBuffer = constantBuffer;
        Dispatch = dispatch;
        Buffers = buffers;
        BufferMetadata = new ReadOnlyCollection<HlslBufferMetadata>(bufferMetadata.ToArray());
    }
}
