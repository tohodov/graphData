using GraphData.Compiler.Diagnostics;
using GraphData.Compiler.Ir;
using GraphData.Compiler.Model;
using GraphData.Compiler.Tensors;

namespace GraphData.Compiler.Compilation;

public sealed class GraphProgramCompiler
{
    public CompiledMessagePassingProgram Compile(
        TypedGraphSnapshot snapshot,
        MessagePassingSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(specification);

        var diagnostics = Validate(snapshot, specification);
        if (diagnostics.Count > 0)
            throw new GraphCompilationException(diagnostics);

        var nodes = snapshot.Nodes
            .OrderBy(static node => node.Key, StringComparer.Ordinal)
            .ToArray();
        var nodeIndices = nodes
            .Select(static (node, index) => KeyValuePair.Create(node.Key, index))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        var nodeTypes = nodes
            .Select(static node => (IReadOnlyList<string>)node.TypeKeys
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static typeKey => typeKey, StringComparer.Ordinal)
                .ToArray())
            .ToArray();
        var featureValues = nodes.SelectMany(static node => node.Features).ToArray();
        var features = new DenseTensor([nodes.Length, specification.InputWidth], featureValues);

        var transforms = specification.RelationTransforms
            .ToDictionary(
                static transform => transform.TypeKey,
                static transform => transform.Weights,
                StringComparer.Ordinal);
        var relationPlans = snapshot.Relations
            .GroupBy(static relation => relation.TypeKey, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var adjacency = BuildAdjacency(nodes.Length, group, nodeIndices);
                return new RelationMessagePlan(
                    group.Key,
                    adjacency.Matrix,
                    transforms[group.Key],
                    adjacency.RelationKeysByEntry);
            })
            .ToArray();

        var plan = new MessagePassingExecutionPlan(
            nodes.Length,
            specification.InputWidth,
            specification.OutputWidth,
            relationPlans,
            specification.SelfTransform,
            specification.Bias,
            specification.Activation);

        return new CompiledMessagePassingProgram(
            nodes.Select(static node => node.Key),
            nodeTypes,
            features,
            plan);
    }

    private static CompiledAdjacency BuildAdjacency(
        int nodeCount,
        IEnumerable<TypedRelationSnapshot> relations,
        IReadOnlyDictionary<string, int> nodeIndices)
    {
        var entries = relations
            .Select(relation => new AdjacencyEntry(
                relation.Key,
                nodeIndices[relation.SourceKey],
                nodeIndices[relation.TargetKey],
                relation.Weight))
            .OrderBy(static entry => entry.Target)
            .ThenBy(static entry => entry.Source)
            .ThenBy(static entry => entry.RelationKey, StringComparer.Ordinal)
            .ToArray();

        var rowOffsets = new int[nodeCount + 1];
        var columnIndices = new int[entries.Length];
        var values = new float[entries.Length];
        var entryIndex = 0;
        for (var row = 0; row < nodeCount; row++)
        {
            while (entryIndex < entries.Length && entries[entryIndex].Target == row)
            {
                columnIndices[entryIndex] = entries[entryIndex].Source;
                values[entryIndex] = entries[entryIndex].Weight;
                entryIndex++;
            }
            rowOffsets[row + 1] = entryIndex;
        }

        return new CompiledAdjacency(
            new CsrMatrix(nodeCount, nodeCount, rowOffsets, columnIndices, values),
            entries.Select(static entry => entry.RelationKey).ToArray());
    }

    private static List<GraphCompilationDiagnostic> Validate(
        TypedGraphSnapshot snapshot,
        MessagePassingSpecification specification)
    {
        var diagnostics = new List<GraphCompilationDiagnostic>();

        if (specification.InputWidth <= 0)
            Add("spec.input_width", "Input width must be greater than zero.");
        if (specification.OutputWidth <= 0)
            Add("spec.output_width", "Output width must be greater than zero.");
        if (!Enum.IsDefined(specification.Activation))
            Add("spec.activation", $"Activation value '{specification.Activation}' is not supported.");

        var nodes = snapshot.Nodes ?? [];
        var nodeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node is null)
            {
                Add("snapshot.node.required", "The node collection contains a null item.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(node.Key))
                Add("snapshot.node.key.required", "Every node must have a non-empty key.");
            else if (!nodeKeys.Add(node.Key))
                Add("snapshot.node.key.duplicate", $"Node key '{node.Key}' is duplicated.");

            if (node.TypeKeys is null || node.TypeKeys.Count == 0)
                Add("snapshot.node.type.required", $"Node '{node.Key}' must have at least one type.");
            else if (node.TypeKeys.Any(string.IsNullOrWhiteSpace))
                Add("snapshot.node.type.invalid", $"Node '{node.Key}' contains an empty type key.");

            if (node.Features is null || node.Features.Count != specification.InputWidth)
                Add(
                    "snapshot.node.feature_width",
                    $"Node '{node.Key}' must have exactly {specification.InputWidth} features.");
            else if (node.Features.Any(static value => !float.IsFinite(value)))
                Add("snapshot.node.feature.non_finite", $"Node '{node.Key}' contains a non-finite feature.");
        }

        if (snapshot.Nodes is null)
            Add("snapshot.nodes.required", "The snapshot node collection is required.");

        var relationKeys = new HashSet<string>(StringComparer.Ordinal);
        var usedRelationTypes = new HashSet<string>(StringComparer.Ordinal);
        var relations = snapshot.Relations ?? [];
        foreach (var relation in relations)
        {
            if (relation is null)
            {
                Add("snapshot.relation.required", "The relation collection contains a null item.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(relation.Key))
                Add("snapshot.relation.key.required", "Every relation must have a non-empty key.");
            else if (!relationKeys.Add(relation.Key))
                Add("snapshot.relation.key.duplicate", $"Relation key '{relation.Key}' is duplicated.");

            if (string.IsNullOrWhiteSpace(relation.TypeKey))
                Add("snapshot.relation.type.required", $"Relation '{relation.Key}' must have a type.");
            else
                usedRelationTypes.Add(relation.TypeKey);

            if (string.IsNullOrWhiteSpace(relation.SourceKey) || !nodeKeys.Contains(relation.SourceKey))
                Add(
                    "snapshot.relation.source.missing",
                    $"Source node '{relation.SourceKey}' of relation '{relation.Key}' is not present in the snapshot.");
            if (string.IsNullOrWhiteSpace(relation.TargetKey) || !nodeKeys.Contains(relation.TargetKey))
                Add(
                    "snapshot.relation.target.missing",
                    $"Target node '{relation.TargetKey}' of relation '{relation.Key}' is not present in the snapshot.");
            if (!float.IsFinite(relation.Weight))
                Add("snapshot.relation.weight.non_finite", $"Relation '{relation.Key}' has a non-finite weight.");
        }

        if (snapshot.Relations is null)
            Add("snapshot.relations.required", "The snapshot relation collection is required.");

        var transforms = new Dictionary<string, DenseTensor>(StringComparer.Ordinal);
        if (specification.RelationTransforms is null)
        {
            Add("spec.relation_transforms.required", "The relation transform collection is required.");
        }
        else
        {
            foreach (var transform in specification.RelationTransforms)
            {
                if (transform is null)
                {
                    Add("spec.relation_transform.required", "The relation transform collection contains a null item.");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(transform.TypeKey))
                {
                    Add("spec.relation_transform.type.required", "Every relation transform must have a type key.");
                    continue;
                }
                if (!transforms.TryAdd(transform.TypeKey, transform.Weights))
                {
                    Add(
                        "spec.relation_transform.type.duplicate",
                        $"Relation transform for type '{transform.TypeKey}' is duplicated.");
                    continue;
                }

                ValidateTensor(
                    transform.Weights,
                    [specification.InputWidth, specification.OutputWidth],
                    "spec.relation_transform.shape",
                    $"Relation transform '{transform.TypeKey}'",
                    diagnostics);
            }
        }

        foreach (var typeKey in usedRelationTypes.OrderBy(static key => key, StringComparer.Ordinal))
            if (!transforms.ContainsKey(typeKey))
                Add(
                    "spec.relation_transform.missing",
                    $"Relation type '{typeKey}' is used by the snapshot but has no transform.");

        if (specification.SelfTransform is not null)
            ValidateTensor(
                specification.SelfTransform,
                [specification.InputWidth, specification.OutputWidth],
                "spec.self_transform.shape",
                "Self transform",
                diagnostics);
        if (specification.Bias is not null)
            ValidateTensor(
                specification.Bias,
                [specification.OutputWidth],
                "spec.bias.shape",
                "Bias",
                diagnostics);

        return diagnostics;

        void Add(string code, string message) => diagnostics.Add(new GraphCompilationDiagnostic(code, message));
    }

    private static void ValidateTensor(
        DenseTensor? tensor,
        IReadOnlyList<int> expectedShape,
        string shapeCode,
        string subject,
        ICollection<GraphCompilationDiagnostic> diagnostics)
    {
        if (tensor is null)
        {
            diagnostics.Add(new GraphCompilationDiagnostic(
                shapeCode,
                $"{subject} is required."));
            return;
        }

        if (!tensor.Shape.SequenceEqual(expectedShape))
            diagnostics.Add(new GraphCompilationDiagnostic(
                shapeCode,
                $"{subject} must have shape [{string.Join(", ", expectedShape)}]."));
        if (tensor.ToArray().Any(static value => !float.IsFinite(value)))
            diagnostics.Add(new GraphCompilationDiagnostic(
                shapeCode.Replace(".shape", ".non_finite", StringComparison.Ordinal),
                $"{subject} contains a non-finite value."));
    }

    private sealed record AdjacencyEntry(string RelationKey, int Source, int Target, float Weight);
    private sealed record CompiledAdjacency(CsrMatrix Matrix, IReadOnlyList<string> RelationKeysByEntry);
}
