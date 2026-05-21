using System.Text.Json.Serialization;

namespace GraphData.Core.Models;

public sealed record NodeSearchQuery
{
    [JsonPropertyName("return")]
    public string[] Return { get; init; } = ["n"];

    public NodeSearchExpression? Where { get; init; }

    public int Limit { get; init; } = 50;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AllNodeSearchExpression), "all")]
[JsonDerivedType(typeof(AnyNodeSearchExpression), "any")]
[JsonDerivedType(typeof(NotNodeSearchExpression), "not")]
[JsonDerivedType(typeof(ExistsNodeSearchExpression), "exists")]
[JsonDerivedType(typeof(NodeExistsSearchExpression), "node")]
[JsonDerivedType(typeof(NodeNameSearchExpression), "name")]
[JsonDerivedType(typeof(NodeAttributeSearchExpression), "attribute")]
[JsonDerivedType(typeof(NodeTextSearchExpression), "text")]
[JsonDerivedType(typeof(NodeConnectedSearchExpression), "connected")]
[JsonDerivedType(typeof(NodePathSearchExpression), "path")]
[JsonDerivedType(typeof(NodeDescendantSearchExpression), "descendant")]
[JsonDerivedType(typeof(NodeDegreeSearchExpression), "degree")]
[JsonDerivedType(typeof(NodeSameSearchExpression), "same")]
[JsonDerivedType(typeof(NodeNotSameSearchExpression), "notSame")]
public abstract record NodeSearchExpression;

public sealed record AllNodeSearchExpression : NodeSearchExpression
{
    public NodeSearchExpression[] Expressions { get; init; } = [];
}

public sealed record AnyNodeSearchExpression : NodeSearchExpression
{
    public NodeSearchExpression[] Expressions { get; init; } = [];
}

public sealed record NotNodeSearchExpression : NodeSearchExpression
{
    public required NodeSearchExpression Expression { get; init; }
}

public sealed record ExistsNodeSearchExpression : NodeSearchExpression
{
    public string[] Variables { get; init; } = [];

    public required NodeSearchExpression Expression { get; init; }
}

public sealed record NodeExistsSearchExpression : NodeSearchExpression
{
    public required NodeSearchNodeSelector Node { get; init; }
}

public sealed record NodeNameSearchExpression : NodeSearchExpression
{
    public required NodeSearchNodeSelector Node { get; init; }

    [JsonPropertyName("operator")]
    public string Operator { get; init; } = SearchOperators.Equal;

    public required string Value { get; init; }
}

public sealed record NodeAttributeSearchExpression : NodeSearchExpression
{
    public required NodeSearchNodeSelector Node { get; init; }

    public required string Key { get; init; }

    [JsonPropertyName("operator")]
    public string Operator { get; init; } = SearchOperators.Equal;

    public string? Value { get; init; }
}

public sealed record NodeTextSearchExpression : NodeSearchExpression
{
    public required NodeSearchNodeSelector Node { get; init; }

    public required string Value { get; init; }
}

public sealed record NodeConnectedSearchExpression : NodeSearchExpression
{
    public required NodeSearchNodeSelector Left { get; init; }

    public required NodeSearchNodeSelector Right { get; init; }
}

public sealed record NodePathSearchExpression : NodeSearchExpression
{
    public required NodeSearchNodeSelector Left { get; init; }

    public required NodeSearchNodeSelector Right { get; init; }

    public int MinDepth { get; init; } = 1;

    public int MaxDepth { get; init; } = 1;

    public bool IncludeSelf { get; init; }
}

public sealed record NodeDescendantSearchExpression : NodeSearchExpression
{
    public required NodeSearchNodeSelector Ancestor { get; init; }

    public required NodeSearchNodeSelector Descendant { get; init; }

    public int MinDepth { get; init; } = 1;

    public int MaxDepth { get; init; } = 8;
}

public sealed record NodeDegreeSearchExpression : NodeSearchExpression
{
    public required NodeSearchNodeSelector Node { get; init; }

    [JsonPropertyName("operator")]
    public string Operator { get; init; } = SearchOperators.Equal;

    public int Value { get; init; }
}

public sealed record NodeSameSearchExpression : NodeSearchExpression
{
    public required NodeSearchNodeSelector Left { get; init; }

    public required NodeSearchNodeSelector Right { get; init; }
}

public sealed record NodeNotSameSearchExpression : NodeSearchExpression
{
    public required NodeSearchNodeSelector Left { get; init; }

    public required NodeSearchNodeSelector Right { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(NodeVariableSearchSelector), "var")]
[JsonDerivedType(typeof(NodeLiteralSearchSelector), "literal")]
public abstract record NodeSearchNodeSelector;

public sealed record NodeVariableSearchSelector : NodeSearchNodeSelector
{
    public required string Name { get; init; }
}

public sealed record NodeLiteralSearchSelector : NodeSearchNodeSelector
{
    public required string Name { get; init; }
}

public static class SearchOperators
{
    public const string Equal = "equals";
    public const string NotEquals = "notEquals";
    public const string Contains = "contains";
    public const string StartsWith = "startsWith";
    public const string EndsWith = "endsWith";
    public const string Exists = "exists";
    public const string GreaterThan = "greaterThan";
    public const string GreaterThanOrEqual = "greaterThanOrEqual";
    public const string LessThan = "lessThan";
    public const string LessThanOrEqual = "lessThanOrEqual";
}

public sealed record NodeSearchMatch
{
    public required Node Node { get; init; }

    public required IReadOnlyDictionary<string, Node> Bindings { get; init; }

    public double Score { get; init; }

    public IReadOnlyCollection<string> MatchedBy { get; init; } = Array.Empty<string>();
}
