using GraphData.Api.Models;
using GraphData.Core.Models;

namespace GraphData.Api.Services;

public static class GraphRequestMapper
{
    public static NodeSearchQuery ToNodeSearchQuery(NodeSearchQueryRequest request)
    {
        return new NodeSearchQuery
        {
            Return = request.Return,
            Where = request.Where is null ? null : ToNodeSearchExpression(request.Where),
            Limit = request.Limit
        };
    }

    private static NodeSearchExpression ToNodeSearchExpression(NodeSearchExpressionRequest expression)
    {
        return expression switch
        {
            AllNodeSearchExpressionRequest value => new AllNodeSearchExpression
            {
                Expressions = value.Expressions.Select(ToNodeSearchExpression).ToArray()
            },
            AnyNodeSearchExpressionRequest value => new AnyNodeSearchExpression
            {
                Expressions = value.Expressions.Select(ToNodeSearchExpression).ToArray()
            },
            NotNodeSearchExpressionRequest value => new NotNodeSearchExpression
            {
                Expression = ToNodeSearchExpression(value.Expression)
            },
            ExistsNodeSearchExpressionRequest value => new ExistsNodeSearchExpression
            {
                Variables = value.Variables,
                Expression = ToNodeSearchExpression(value.Expression)
            },
            NodeExistsSearchExpressionRequest value => new NodeExistsSearchExpression
            {
                Node = ToNodeSearchSelector(value.Node)
            },
            NodeNameSearchExpressionRequest value => new NodeNameSearchExpression
            {
                Node = ToNodeSearchSelector(value.Node),
                Operator = value.Operator,
                Value = value.Value
            },
            NodeAttributeSearchExpressionRequest value => new NodeAttributeSearchExpression
            {
                Node = ToNodeSearchSelector(value.Node),
                Key = value.Key,
                Operator = value.Operator,
                Value = value.Value
            },
            NodeTextSearchExpressionRequest value => new NodeTextSearchExpression
            {
                Node = ToNodeSearchSelector(value.Node),
                Value = value.Value
            },
            NodeConnectedSearchExpressionRequest value => new NodeConnectedSearchExpression
            {
                Left = ToNodeSearchSelector(value.Left),
                Right = ToNodeSearchSelector(value.Right)
            },
            NodePathSearchExpressionRequest value => new NodePathSearchExpression
            {
                Left = ToNodeSearchSelector(value.Left),
                Right = ToNodeSearchSelector(value.Right),
                MinDepth = value.MinDepth,
                MaxDepth = value.MaxDepth,
                IncludeSelf = value.IncludeSelf
            },
            NodeDescendantSearchExpressionRequest value => new NodeDescendantSearchExpression
            {
                Ancestor = ToNodeSearchSelector(value.Ancestor),
                Descendant = ToNodeSearchSelector(value.Descendant),
                MinDepth = value.MinDepth,
                MaxDepth = value.MaxDepth
            },
            NodeDegreeSearchExpressionRequest value => new NodeDegreeSearchExpression
            {
                Node = ToNodeSearchSelector(value.Node),
                Operator = value.Operator,
                Value = value.Value
            },
            NodeSameSearchExpressionRequest value => new NodeSameSearchExpression
            {
                Left = ToNodeSearchSelector(value.Left),
                Right = ToNodeSearchSelector(value.Right)
            },
            NodeNotSameSearchExpressionRequest value => new NodeNotSameSearchExpression
            {
                Left = ToNodeSearchSelector(value.Left),
                Right = ToNodeSearchSelector(value.Right)
            },
            _ => throw new NotSupportedException($"Unsupported node search expression type '{expression.GetType().FullName}'.")
        };
    }

    private static NodeSearchNodeSelector ToNodeSearchSelector(NodeSearchNodeSelectorRequest selector)
    {
        return selector switch
        {
            NodeVariableSearchSelectorRequest value => new NodeVariableSearchSelector { Name = value.Name },
            NodeLiteralSearchSelectorRequest value => new NodeLiteralSearchSelector { Name = value.Name },
            _ => throw new NotSupportedException($"Unsupported node search selector type '{selector.GetType().FullName}'.")
        };
    }
}
