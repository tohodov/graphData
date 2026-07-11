using GraphData.Compiler.Tensors;

namespace GraphData.Compiler.Model;

public enum ActivationKind
{
    Identity,
    Relu
}

public sealed record RelationTransform(string TypeKey, DenseTensor Weights);

/// <summary>
/// Defines one inference layer:
/// Y[v] = activation(bias + X[v] * Wself + sum(Arelation * X * Wrelation)).
/// </summary>
public sealed record MessagePassingSpecification(
    int InputWidth,
    int OutputWidth,
    IReadOnlyList<RelationTransform> RelationTransforms,
    DenseTensor? SelfTransform = null,
    DenseTensor? Bias = null,
    ActivationKind Activation = ActivationKind.Identity);
