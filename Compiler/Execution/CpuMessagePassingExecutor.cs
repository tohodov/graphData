using GraphData.Compiler.Ir;
using GraphData.Compiler.Model;
using GraphData.Compiler.Tensors;

namespace GraphData.Compiler.Execution;

/// <summary>
/// Deterministic reference implementation used to verify generated backends.
/// It intentionally favors a transparent evaluation order over performance.
/// </summary>
public sealed class CpuMessagePassingExecutor
{
    public DenseTensor Execute(CompiledMessagePassingProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        return Execute(program.Plan, program.InitialNodeFeatures);
    }

    public DenseTensor Execute(MessagePassingExecutionPlan plan, DenseTensor nodeFeatures)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(nodeFeatures);
        if (!nodeFeatures.Shape.SequenceEqual([plan.NodeCount, plan.InputWidth]))
            throw new ArgumentException(
                $"Node features must have shape [{plan.NodeCount}, {plan.InputWidth}].",
                nameof(nodeFeatures));

        var output = new float[checked(plan.NodeCount * plan.OutputWidth)];
        for (var target = 0; target < plan.NodeCount; target++)
        {
            for (var outputFeature = 0; outputFeature < plan.OutputWidth; outputFeature++)
            {
                var value = plan.Bias?[outputFeature] ?? 0f;

                if (plan.SelfTransform is not null)
                    for (var inputFeature = 0; inputFeature < plan.InputWidth; inputFeature++)
                        value += nodeFeatures[target, inputFeature]
                            * plan.SelfTransform[inputFeature, outputFeature];

                foreach (var relation in plan.Relations)
                {
                    var start = relation.Adjacency.RowOffsets[target];
                    var end = relation.Adjacency.RowOffsets[target + 1];
                    for (var edgeIndex = start; edgeIndex < end; edgeIndex++)
                    {
                        var source = relation.Adjacency.ColumnIndices[edgeIndex];
                        var edgeWeight = relation.Adjacency.Values[edgeIndex];
                        for (var inputFeature = 0; inputFeature < plan.InputWidth; inputFeature++)
                            value += edgeWeight
                                * nodeFeatures[source, inputFeature]
                                * relation.Transform[inputFeature, outputFeature];
                    }
                }

                output[(target * plan.OutputWidth) + outputFeature] = plan.Activation switch
                {
                    ActivationKind.Identity => value,
                    ActivationKind.Relu => MathF.Max(0f, value),
                    _ => throw new InvalidOperationException($"Unsupported activation '{plan.Activation}'.")
                };
            }
        }

        return new DenseTensor([plan.NodeCount, plan.OutputWidth], output);
    }
}
