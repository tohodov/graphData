using System.Collections.ObjectModel;
using GraphData.Compiler.Model;
using GraphData.Compiler.Tensors;

namespace GraphData.Compiler.Ir;

public sealed class RelationMessagePlan
{
    public string TypeKey { get; }
    public CsrMatrix Adjacency { get; }
    public DenseTensor Transform { get; }
    public IReadOnlyList<string> RelationKeysByEntry { get; }

    internal RelationMessagePlan(
        string typeKey,
        CsrMatrix adjacency,
        DenseTensor transform,
        IEnumerable<string> relationKeysByEntry)
    {
        TypeKey = typeKey;
        Adjacency = adjacency;
        Transform = transform;
        RelationKeysByEntry = Array.AsReadOnly(relationKeysByEntry.ToArray());
        if (RelationKeysByEntry.Count != adjacency.NonZeroCount)
            throw new ArgumentException(
                "Every CSR entry must have one source relation key.",
                nameof(relationKeysByEntry));
    }
}

public sealed class MessagePassingExecutionPlan
{
    public int NodeCount { get; }
    public int InputWidth { get; }
    public int OutputWidth { get; }
    public IReadOnlyList<RelationMessagePlan> Relations { get; }
    public DenseTensor? SelfTransform { get; }
    public DenseTensor? Bias { get; }
    public ActivationKind Activation { get; }

    internal MessagePassingExecutionPlan(
        int nodeCount,
        int inputWidth,
        int outputWidth,
        IEnumerable<RelationMessagePlan> relations,
        DenseTensor? selfTransform,
        DenseTensor? bias,
        ActivationKind activation)
    {
        NodeCount = nodeCount;
        InputWidth = inputWidth;
        OutputWidth = outputWidth;
        Relations = new ReadOnlyCollection<RelationMessagePlan>(relations.ToArray());
        SelfTransform = selfTransform;
        Bias = bias;
        Activation = activation;
    }
}

public sealed class CompiledMessagePassingProgram
{
    private readonly IReadOnlyDictionary<string, int> nodeIndices;

    public IReadOnlyList<string> NodeKeysByIndex { get; }
    public IReadOnlyList<IReadOnlyList<string>> NodeTypesByIndex { get; }
    public DenseTensor InitialNodeFeatures { get; }
    public MessagePassingExecutionPlan Plan { get; }

    internal CompiledMessagePassingProgram(
        IEnumerable<string> nodeKeysByIndex,
        IEnumerable<IReadOnlyList<string>> nodeTypesByIndex,
        DenseTensor initialNodeFeatures,
        MessagePassingExecutionPlan plan)
    {
        var nodeKeys = nodeKeysByIndex.ToArray();
        NodeKeysByIndex = Array.AsReadOnly(nodeKeys);
        NodeTypesByIndex = new ReadOnlyCollection<IReadOnlyList<string>>(
            nodeTypesByIndex.Select(static types => (IReadOnlyList<string>)Array.AsReadOnly(types.ToArray())).ToArray());
        InitialNodeFeatures = initialNodeFeatures;
        Plan = plan;
        nodeIndices = new ReadOnlyDictionary<string, int>(
            nodeKeys.Select(static (key, index) => KeyValuePair.Create(key, index))
                .ToDictionary(StringComparer.Ordinal));
    }

    public int GetNodeIndex(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return nodeIndices.TryGetValue(key, out var index)
            ? index
            : throw new KeyNotFoundException($"Node '{key}' is not present in the compiled snapshot.");
    }
}
