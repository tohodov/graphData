using System.Text.Json.Serialization;

namespace GraphData.Api.Models;

public sealed record NodeSearchQueryRequest
{
    [JsonPropertyName("return")]
    public string[] Return { get; init; } = ["n"];

    public NodeSearchExpressionRequest? Where { get; init; }

    public int Limit { get; init; } = 50;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AllNodeSearchExpressionRequest), "all")]
[JsonDerivedType(typeof(AnyNodeSearchExpressionRequest), "any")]
[JsonDerivedType(typeof(NotNodeSearchExpressionRequest), "not")]
[JsonDerivedType(typeof(ExistsNodeSearchExpressionRequest), "exists")]
[JsonDerivedType(typeof(NodeExistsSearchExpressionRequest), "node")]
[JsonDerivedType(typeof(NodeNameSearchExpressionRequest), "name")]
[JsonDerivedType(typeof(NodeAttributeSearchExpressionRequest), "attribute")]
[JsonDerivedType(typeof(NodeTextSearchExpressionRequest), "text")]
[JsonDerivedType(typeof(NodeConnectedSearchExpressionRequest), "connected")]
[JsonDerivedType(typeof(NodePathSearchExpressionRequest), "path")]
[JsonDerivedType(typeof(NodeDescendantSearchExpressionRequest), "descendant")]
[JsonDerivedType(typeof(NodeDegreeSearchExpressionRequest), "degree")]
[JsonDerivedType(typeof(NodeSameSearchExpressionRequest), "same")]
[JsonDerivedType(typeof(NodeNotSameSearchExpressionRequest), "notSame")]
public abstract record NodeSearchExpressionRequest;

public sealed record AllNodeSearchExpressionRequest : NodeSearchExpressionRequest
{
    public NodeSearchExpressionRequest[] Expressions { get; init; } = [];
}

public sealed record AnyNodeSearchExpressionRequest : NodeSearchExpressionRequest
{
    public NodeSearchExpressionRequest[] Expressions { get; init; } = [];
}

public sealed record NotNodeSearchExpressionRequest : NodeSearchExpressionRequest
{
    public required NodeSearchExpressionRequest Expression { get; init; }
}

public sealed record ExistsNodeSearchExpressionRequest : NodeSearchExpressionRequest
{
    public string[] Variables { get; init; } = [];

    public required NodeSearchExpressionRequest Expression { get; init; }
}

public sealed record NodeExistsSearchExpressionRequest : NodeSearchExpressionRequest
{
    public required NodeSearchNodeSelectorRequest Node { get; init; }
}

public sealed record NodeNameSearchExpressionRequest : NodeSearchExpressionRequest
{
    public required NodeSearchNodeSelectorRequest Node { get; init; }

    [JsonPropertyName("operator")]
    public string Operator { get; init; } = "equals";

    public required string Value { get; init; }
}

public sealed record NodeAttributeSearchExpressionRequest : NodeSearchExpressionRequest
{
    public required NodeSearchNodeSelectorRequest Node { get; init; }

    public required string Key { get; init; }

    [JsonPropertyName("operator")]
    public string Operator { get; init; } = "equals";

    public string? Value { get; init; }
}

public sealed record NodeTextSearchExpressionRequest : NodeSearchExpressionRequest
{
    public required NodeSearchNodeSelectorRequest Node { get; init; }

    public required string Value { get; init; }
}

public sealed record NodeConnectedSearchExpressionRequest : NodeSearchExpressionRequest
{
    public required NodeSearchNodeSelectorRequest Left { get; init; }

    public required NodeSearchNodeSelectorRequest Right { get; init; }
}

public sealed record NodePathSearchExpressionRequest : NodeSearchExpressionRequest
{
    public required NodeSearchNodeSelectorRequest Left { get; init; }

    public required NodeSearchNodeSelectorRequest Right { get; init; }

    public int MinDepth { get; init; } = 1;

    public int MaxDepth { get; init; } = 1;

    public bool IncludeSelf { get; init; }
}

public sealed record NodeDescendantSearchExpressionRequest : NodeSearchExpressionRequest
{
    public required NodeSearchNodeSelectorRequest Ancestor { get; init; }

    public required NodeSearchNodeSelectorRequest Descendant { get; init; }

    public int MinDepth { get; init; } = 1;

    public int MaxDepth { get; init; } = 8;
}

public sealed record NodeDegreeSearchExpressionRequest : NodeSearchExpressionRequest
{
    public required NodeSearchNodeSelectorRequest Node { get; init; }

    [JsonPropertyName("operator")]
    public string Operator { get; init; } = "equals";

    public int Value { get; init; }
}

public sealed record NodeSameSearchExpressionRequest : NodeSearchExpressionRequest
{
    public required NodeSearchNodeSelectorRequest Left { get; init; }

    public required NodeSearchNodeSelectorRequest Right { get; init; }
}

public sealed record NodeNotSameSearchExpressionRequest : NodeSearchExpressionRequest
{
    public required NodeSearchNodeSelectorRequest Left { get; init; }

    public required NodeSearchNodeSelectorRequest Right { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(NodeVariableSearchSelectorRequest), "var")]
[JsonDerivedType(typeof(NodeLiteralSearchSelectorRequest), "literal")]
public abstract record NodeSearchNodeSelectorRequest;

public sealed record NodeVariableSearchSelectorRequest : NodeSearchNodeSelectorRequest
{
    public required string Name { get; init; }
}

public sealed record NodeLiteralSearchSelectorRequest : NodeSearchNodeSelectorRequest
{
    public required string Name { get; init; }
}
