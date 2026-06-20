using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

public sealed class GraphSearchService {
    private const int DefaultLimit = 50;
    private const int MaxLimit = 500;

    private readonly IGraphStorage _storage;

    internal GraphSearchService(IGraphStorage storage) {
        _storage = storage;
    }

    public async IAsyncEnumerable<NodeSearchMatch> SearchNodesStreamAsync(NodeSearchQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default) {
        Validate(query);

        var graph = new SearchGraph(_storage, cancellationToken);
        var returnVariables = NormalizeReturnVariables(query.Return);
        var limit = NormalizeLimit(query.Limit);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var yielded = 0;

        await foreach (var solution in EnumerateSolutions(query, graph, returnVariables, cancellationToken).ConfigureAwait(false)) {
            cancellationToken.ThrowIfCancellationRequested();
            if (returnVariables.Any(variable => !solution.Bindings.ContainsKey(variable))) {
                continue;
            }

            var key = GetReturnKey(solution, returnVariables);
            if (!seen.Add(key)) {
                continue;
            }

            yield return ToMatch(solution, returnVariables);
            yielded++;

            if (yielded >= limit) {
                yield break;
            }

            await Task.Yield();
        }
    }

    private static IAsyncEnumerable<SearchSolution> EnumerateSolutions(
        NodeSearchQuery query,
        SearchGraph graph,
        string[] returnVariables,
        CancellationToken cancellationToken) {
        var effectiveWhere = BuildEffectiveWhere(query.Where, returnVariables);
        var initial = new SearchSolution(new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase), 0, []);
        return Evaluate(effectiveWhere, ToAsyncEnumerable([initial]), graph, cancellationToken);
    }

    private static NodeSearchMatch ToMatch(SearchSolution solution, string[] returnVariables) {
        return new NodeSearchMatch {
            Node = solution.Bindings[returnVariables[0]],
            Bindings = returnVariables.ToDictionary(
                static variable => variable,
                variable => solution.Bindings[variable],
                StringComparer.OrdinalIgnoreCase),
            Score = Math.Round(Math.Max(1, solution.Score), 4),
            MatchedBy = solution.MatchedBy.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private static AllNodeSearchExpression BuildEffectiveWhere(NodeSearchExpression? where, string[] returnVariables) {
        var bindableVariables = where is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : GetBindableVariables(where);
        var expressions = new List<NodeSearchExpression>();

        expressions.AddRange(returnVariables
            .Where(variable => !bindableVariables.Contains(variable))
            .Select(CreateReturnBinder));

        if (where is not null) {
            expressions.Add(where);
        }

        expressions.AddRange(returnVariables.Select(CreateReturnBinder));

        return new AllNodeSearchExpression { Expressions = expressions.ToArray() };
    }

    private static NodeExistsSearchExpression CreateReturnBinder(string variable) {
        return new NodeExistsSearchExpression {
            Node = new NodeVariableSearchSelector { Name = variable }
        };
    }

    private static HashSet<string> GetBindableVariables(NodeSearchExpression expression) {
        var variables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddBindableVariables(expression, variables);
        return variables;
    }

    private static void AddBindableVariables(NodeSearchExpression expression, ISet<string> variables) {
        switch (expression) {
            case AllNodeSearchExpression all:
                foreach (var child in all.Expressions) {
                    AddBindableVariables(child, variables);
                }
                break;

            case AnyNodeSearchExpression any:
                foreach (var child in any.Expressions) {
                    AddBindableVariables(child, variables);
                }
                break;

            case NodeExistsSearchExpression node:
                AddVariable(node.Node, variables);
                break;

            case NodeNameSearchExpression name:
                AddVariable(name.Node, variables);
                break;

            case NodeAttributeSearchExpression attribute:
                AddVariable(attribute.Node, variables);
                break;

            case NodeTextSearchExpression text:
                AddVariable(text.Node, variables);
                break;

            case NodeConnectedSearchExpression connected:
                AddVariable(connected.Left, variables);
                AddVariable(connected.Right, variables);
                break;

            case NodePathSearchExpression path:
                AddVariable(path.Left, variables);
                AddVariable(path.Right, variables);
                break;

            case NodeDescendantSearchExpression descendant:
                AddVariable(descendant.Ancestor, variables);
                AddVariable(descendant.Descendant, variables);
                break;

            case NodeDegreeSearchExpression degree:
                AddVariable(degree.Node, variables);
                break;

            case NodeSameSearchExpression same:
                AddVariable(same.Left, variables);
                AddVariable(same.Right, variables);
                break;

            case NodeNotSameSearchExpression notSame:
                AddVariable(notSame.Left, variables);
                AddVariable(notSame.Right, variables);
                break;
        }
    }

    private static void AddVariable(NodeSearchNodeSelector selector, ISet<string> variables) {
        if (selector is NodeVariableSearchSelector variable) {
            variables.Add(variable.Name);
        }
    }

    private static IAsyncEnumerable<SearchSolution> Evaluate(
        NodeSearchExpression expression,
        IAsyncEnumerable<SearchSolution> input,
        SearchGraph graph,
        CancellationToken cancellationToken) {
        return expression switch {
            AllNodeSearchExpression all => EvaluateAll(all, input, graph, cancellationToken),
            AnyNodeSearchExpression any => EvaluateAny(any, input, graph, cancellationToken),
            NotNodeSearchExpression not => EvaluateNot(not, input, graph, cancellationToken),
            ExistsNodeSearchExpression exists => EvaluateExists(exists, input, graph, cancellationToken),
            NodeExistsSearchExpression node => EvaluateNode(node, input, graph, cancellationToken),
            NodeNameSearchExpression name => EvaluateName(name, input, graph, cancellationToken),
            NodeAttributeSearchExpression attribute => EvaluateAttribute(attribute, input, graph, cancellationToken),
            NodeTextSearchExpression text => EvaluateText(text, input, graph, cancellationToken),
            NodeConnectedSearchExpression connected => EvaluatePath(
                connected.Left,
                connected.Right,
                minDepth: 1,
                maxDepth: 1,
                includeSelf: false,
                input,
                graph,
                "connected",
                cancellationToken),
            NodePathSearchExpression path => EvaluatePath(
                path.Left,
                path.Right,
                path.MinDepth,
                path.MaxDepth,
                path.IncludeSelf,
                input,
                graph,
                "path",
                cancellationToken),
            NodeDescendantSearchExpression descendant => EvaluateDescendant(descendant, input, graph, cancellationToken),
            NodeDegreeSearchExpression degree => EvaluateDegree(degree, input, graph, cancellationToken),
            NodeSameSearchExpression same => EvaluateSame(same, input, graph, cancellationToken),
            NodeNotSameSearchExpression notSame => EvaluateNotSame(notSame, input, graph, cancellationToken),
            _ => throw new NotSupportedException($"Unsupported search expression type '{expression.GetType().Name}'.")
        };
    }

    private static IAsyncEnumerable<SearchSolution> EvaluateAll(
        AllNodeSearchExpression expression,
        IAsyncEnumerable<SearchSolution> input,
        SearchGraph graph,
        CancellationToken cancellationToken) {
        var current = input;
        foreach (var child in expression.Expressions.OrderBy(static child => child is NotNodeSearchExpression ? 1 : 0)) {
            current = Evaluate(child, current, graph, cancellationToken);
        }

        return current;
    }

    private static async IAsyncEnumerable<SearchSolution> EvaluateAny(
        AnyNodeSearchExpression expression,
        IAsyncEnumerable<SearchSolution> input,
        SearchGraph graph,
        [EnumeratorCancellation] CancellationToken cancellationToken) {
        await foreach (var solution in input.WithCancellation(cancellationToken).ConfigureAwait(false)) {
            foreach (var child in expression.Expressions) {
                await foreach (var result in Evaluate(child, ToAsyncEnumerable([solution]), graph, cancellationToken).ConfigureAwait(false)) {
                    yield return result;
                }
            }
        }
    }

    private static async IAsyncEnumerable<SearchSolution> EvaluateNot(
        NotNodeSearchExpression expression,
        IAsyncEnumerable<SearchSolution> input,
        SearchGraph graph,
        [EnumeratorCancellation] CancellationToken cancellationToken) {
        await foreach (var solution in input.WithCancellation(cancellationToken).ConfigureAwait(false)) {
            if (!await AnyAsync(Evaluate(expression.Expression, ToAsyncEnumerable([solution]), graph, cancellationToken), cancellationToken).ConfigureAwait(false)) {
                yield return solution.AddMatch("not");
            }
        }
    }

    private static async IAsyncEnumerable<SearchSolution> EvaluateExists(
        ExistsNodeSearchExpression expression,
        IAsyncEnumerable<SearchSolution> input,
        SearchGraph graph,
        [EnumeratorCancellation] CancellationToken cancellationToken) {
        await foreach (var solution in input.WithCancellation(cancellationToken).ConfigureAwait(false)) {
            SearchSolution? inner = null;
            await foreach (var result in Evaluate(expression.Expression, ToAsyncEnumerable([solution]), graph, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false)) {
                if (inner is null || result.Score > inner.Score) {
                    inner = result;
                }
            }

            if (inner is null) {
                continue;
            }

            var addedScore = Math.Max(0, inner.Score - solution.Score);
            yield return solution.AddMatch("exists", addedScore);
        }
    }

    private static async IAsyncEnumerable<SearchSolution> EvaluateNode(
        NodeExistsSearchExpression expression,
        IAsyncEnumerable<SearchSolution> input,
        SearchGraph graph,
        [EnumeratorCancellation] CancellationToken cancellationToken) {
        await foreach (var solution in input.WithCancellation(cancellationToken).ConfigureAwait(false)) {
            await foreach (var candidate in CandidateNodes(solution, expression.Node, graph, cancellationToken).ConfigureAwait(false)) {
                var bound = TryBind(solution, expression.Node, candidate, 0.05, "node");
                if (bound is not null) {
                    yield return bound;
                }
            }
        }
    }

    private static async IAsyncEnumerable<SearchSolution> EvaluateName(
        NodeNameSearchExpression expression,
        IAsyncEnumerable<SearchSolution> input,
        SearchGraph graph,
        [EnumeratorCancellation] CancellationToken cancellationToken) {
        await foreach (var solution in input.WithCancellation(cancellationToken).ConfigureAwait(false)) {
            await foreach (var candidate in CandidateNodes(solution, expression.Node, graph, cancellationToken).ConfigureAwait(false)) {
                if (!MatchesText(candidate.LocalId, expression.Operator, expression.Value)) {
                    continue;
                }

                var score = GetTextComparisonScore(candidate.LocalId, expression.Operator, expression.Value);
                var bound = TryBind(solution, expression.Node, candidate, score, $"name:{expression.Operator}:{expression.Value}");
                if (bound is not null) {
                    yield return bound;
                }
            }
        }
    }

    private static async IAsyncEnumerable<SearchSolution> EvaluateAttribute(
        NodeAttributeSearchExpression expression,
        IAsyncEnumerable<SearchSolution> input,
        SearchGraph graph,
        [EnumeratorCancellation] CancellationToken cancellationToken) {
        await foreach (var solution in input.WithCancellation(cancellationToken).ConfigureAwait(false)) {
            await foreach (var candidate in CandidateNodes(solution, expression.Node, graph, cancellationToken).ConfigureAwait(false)) {
                if (!MatchesAttribute(candidate, expression.Key, expression.Operator, expression.Value)) {
                    continue;
                }

                var bound = TryBind(solution, expression.Node, candidate, 0.65, $"attribute:{expression.Key}:{expression.Operator}");
                if (bound is not null) {
                    yield return bound;
                }
            }
        }
    }

    private static async IAsyncEnumerable<SearchSolution> EvaluateText(
        NodeTextSearchExpression expression,
        IAsyncEnumerable<SearchSolution> input,
        SearchGraph graph,
        [EnumeratorCancellation] CancellationToken cancellationToken) {
        await foreach (var solution in input.WithCancellation(cancellationToken).ConfigureAwait(false)) {
            await foreach (var candidate in CandidateNodes(solution, expression.Node, graph, cancellationToken).ConfigureAwait(false)) {
                var score = GetTextScore(candidate, expression.Value);
                if (score <= 0) {
                    continue;
                }

                var bound = TryBind(solution, expression.Node, candidate, score, $"text:{expression.Value}");
                if (bound is not null) {
                    yield return bound;
                }
            }
        }
    }

    private static async IAsyncEnumerable<SearchSolution> EvaluatePath(
        NodeSearchNodeSelector left,
        NodeSearchNodeSelector right,
        int minDepth,
        int maxDepth,
        bool includeSelf,
        IAsyncEnumerable<SearchSolution> input,
        SearchGraph graph,
        string matchName,
        [EnumeratorCancellation] CancellationToken cancellationToken) {
        await foreach (var solution in input.WithCancellation(cancellationToken).ConfigureAwait(false)) {
            await foreach (var (leftNode, rightNode, distance) in CandidatePaths(solution, left, right, minDepth, maxDepth, includeSelf, graph, cancellationToken).ConfigureAwait(false)) {
                var bound = TryBind(solution, left, leftNode, PathScore(distance), $"{matchName}:depth={distance}");
                bound = bound is null ? null : TryBind(bound, right, rightNode);
                if (bound is not null) {
                    yield return bound;
                }
            }
        }
    }

    private static async IAsyncEnumerable<SearchSolution> EvaluateDescendant(
        NodeDescendantSearchExpression expression,
        IAsyncEnumerable<SearchSolution> input,
        SearchGraph graph,
        [EnumeratorCancellation] CancellationToken cancellationToken) {
        await foreach (var solution in input.WithCancellation(cancellationToken).ConfigureAwait(false)) {
            await foreach (var (ancestor, descendant, depth) in CandidateDescendants(solution, expression, graph, cancellationToken).ConfigureAwait(false)) {
                var ancestorName = NormalizeNodeName(ancestor.LocalId);
                var bound = TryBind(solution, expression.Ancestor, ancestor, 0.3, $"ancestor:{ancestorName}");
                bound = bound is null ? null : TryBind(bound, expression.Descendant, descendant, 0.75 + 0.25 / depth, $"descendant-of:{ancestorName} depth={depth}");
                if (bound is not null) {
                    yield return bound;
                }
            }
        }
    }

    private static async IAsyncEnumerable<SearchSolution> EvaluateDegree(
        NodeDegreeSearchExpression expression,
        IAsyncEnumerable<SearchSolution> input,
        SearchGraph graph,
        [EnumeratorCancellation] CancellationToken cancellationToken) {
        await foreach (var solution in input.WithCancellation(cancellationToken).ConfigureAwait(false)) {
            await foreach (var candidate in CandidateNodes(solution, expression.Node, graph, cancellationToken).ConfigureAwait(false)) {
                var degree = await graph.GetDegreeAsync(candidate).ConfigureAwait(false);
                if (!MatchesNumber(degree, expression.Operator, expression.Value)) {
                    continue;
                }

                var bound = TryBind(solution, expression.Node, candidate, 0.45, $"degree:{expression.Operator}:{expression.Value}");
                if (bound is not null) {
                    yield return bound;
                }
            }
        }
    }

    private static async IAsyncEnumerable<SearchSolution> EvaluateSame(
        NodeSameSearchExpression expression,
        IAsyncEnumerable<SearchSolution> input,
        SearchGraph graph,
        [EnumeratorCancellation] CancellationToken cancellationToken) {
        await foreach (var solution in input.WithCancellation(cancellationToken).ConfigureAwait(false)) {
            await foreach (var (left, right) in CandidateNodePairs(solution, expression.Left, expression.Right, graph, cancellationToken).ConfigureAwait(false)) {
                if (!SameNode(left, right)) {
                    continue;
                }

                var bound = TryBind(solution, expression.Left, left, 0.1, "same");
                bound = bound is null ? null : TryBind(bound, expression.Right, right);
                if (bound is not null) {
                    yield return bound;
                }
            }
        }
    }

    private static async IAsyncEnumerable<SearchSolution> EvaluateNotSame(
        NodeNotSameSearchExpression expression,
        IAsyncEnumerable<SearchSolution> input,
        SearchGraph graph,
        [EnumeratorCancellation] CancellationToken cancellationToken) {
        await foreach (var solution in input.WithCancellation(cancellationToken).ConfigureAwait(false)) {
            await foreach (var (left, right) in CandidateNodePairs(solution, expression.Left, expression.Right, graph, cancellationToken).ConfigureAwait(false)) {
                if (SameNode(left, right)) {
                    continue;
                }

                var bound = TryBind(solution, expression.Left, left, 0.1, "not-same");
                bound = bound is null ? null : TryBind(bound, expression.Right, right);
                if (bound is not null) {
                    yield return bound;
                }
            }
        }
    }

    private static async IAsyncEnumerable<Node> CandidateNodes(
        SearchSolution solution,
        NodeSearchNodeSelector selector,
        SearchGraph graph,
        [EnumeratorCancellation] CancellationToken cancellationToken) {
        switch (selector) {
            case NodeLiteralSearchSelector literal:
                var literalNode = await graph.TryGetNodeAsync(literal.Name).ConfigureAwait(false);
                if (literalNode is not null) {
                    yield return literalNode;
                }

                break;

            case NodeVariableSearchSelector variable when solution.Bindings.TryGetValue(variable.Name, out var node):
                yield return node;
                break;

            case NodeVariableSearchSelector:
                await foreach (var candidate in graph.Nodes.WithCancellation(cancellationToken).ConfigureAwait(false)) {
                    yield return candidate;
                }

                break;

            default:
                throw new NotSupportedException($"Unsupported node selector type '{selector.GetType().Name}'.");
        }
    }

    private static SearchSolution? TryBind(
        SearchSolution solution,
        NodeSearchNodeSelector selector,
        Node candidate,
        double score = 0,
        string? match = null) {
        switch (selector) {
            case NodeLiteralSearchSelector literal:
                return string.Equals(NormalizeNodeName(literal.Name), NormalizeNodeName(candidate.LocalId), StringComparison.OrdinalIgnoreCase)
                    ? solution.AddMatch(match, score)
                    : null;

            case NodeVariableSearchSelector variable:
                if (solution.Bindings.TryGetValue(variable.Name, out var existing)) {
                    return SameNode(existing, candidate)
                        ? solution.AddMatch(match, score)
                        : null;
                }

                var bindings = new Dictionary<string, Node>(solution.Bindings, StringComparer.OrdinalIgnoreCase) {
                    [variable.Name] = candidate
                };
                return new SearchSolution(bindings, solution.Score + score, AddMatch(solution.MatchedBy, match));

            default:
                throw new NotSupportedException($"Unsupported node selector type '{selector.GetType().Name}'.");
        }
    }

    private static async IAsyncEnumerable<(Node Left, Node Right)> CandidateNodePairs(
        SearchSolution solution,
        NodeSearchNodeSelector left,
        NodeSearchNodeSelector right,
        SearchGraph graph,
        [EnumeratorCancellation] CancellationToken cancellationToken) {
        await foreach (var leftNode in CandidateNodes(solution, left, graph, cancellationToken).ConfigureAwait(false)) {
            await foreach (var rightNode in CandidateNodes(solution, right, graph, cancellationToken).ConfigureAwait(false)) {
                yield return (leftNode, rightNode);
            }
        }
    }

    private static async IAsyncEnumerable<(Node Left, Node Right, int Distance)> CandidatePaths(
        SearchSolution solution,
        NodeSearchNodeSelector left,
        NodeSearchNodeSelector right,
        int minDepth,
        int maxDepth,
        bool includeSelf,
        SearchGraph graph,
        [EnumeratorCancellation] CancellationToken cancellationToken) {
        var rightNames = await GetKnownSelectorNamesAsync(solution, right, graph).ConfigureAwait(false);

        await foreach (var leftNode in CandidateNodes(solution, left, graph, cancellationToken).ConfigureAwait(false)) {
            foreach (var path in await graph.GetReachableAsync(leftNode, minDepth, maxDepth, includeSelf).ConfigureAwait(false)) {
                if (rightNames is null || rightNames.Contains(NormalizeNodeName(path.Node.LocalId))) {
                    yield return (leftNode, path.Node, path.Distance);
                }
            }
        }
    }

    private static async IAsyncEnumerable<(Node Ancestor, Node Descendant, int Depth)> CandidateDescendants(
        SearchSolution solution,
        NodeDescendantSearchExpression expression,
        SearchGraph graph,
        [EnumeratorCancellation] CancellationToken cancellationToken) {
        await foreach (var ancestor in CandidateNodes(solution, expression.Ancestor, graph, cancellationToken).ConfigureAwait(false)) {
            await foreach (var descendant in CandidateNodes(solution, expression.Descendant, graph, cancellationToken).ConfigureAwait(false)) {
                var depth = graph.GetDescendantDepth(ancestor, descendant);
                if (depth >= expression.MinDepth && depth <= expression.MaxDepth) {
                    yield return (ancestor, descendant, depth);
                }
            }
        }
    }

    private static string GetReturnKey(SearchSolution solution, string[] returnVariables) {
        return string.Join('\u001f', returnVariables.Select(variable => NormalizeNodeName(solution.Bindings[variable].LocalId)));
    }

    private static void Validate(NodeSearchQuery query) {
        if (query.Return is { Length: 0 }) {
            throw new ArgumentException("At least one return variable must be provided.", nameof(query));
        }

        foreach (var variable in NormalizeReturnVariables(query.Return)) {
            ArgumentException.ThrowIfNullOrWhiteSpace(variable);
        }

        if (query.Where is not null) {
            Validate(query.Where);
        }
    }

    private static void Validate(NodeSearchOrder order) {
        switch (order) {
            case NodeSearchScoreOrder:
                break;

            case NodeSearchNameOrder name:
                ArgumentException.ThrowIfNullOrWhiteSpace(name.Variable);
                break;

            case NodeSearchDegreeOrder degree:
                ArgumentException.ThrowIfNullOrWhiteSpace(degree.Variable);
                break;

            default:
                throw new NotSupportedException($"Unsupported search order type '{order.GetType().Name}'.");
        }

        if (!IsAscending(order.Direction) && !IsDescending(order.Direction)) {
            throw new ArgumentException($"Unsupported order direction '{order.Direction}'.");
        }
    }

    private static void Validate(NodeSearchExpression expression) {
        switch (expression) {
            case AllNodeSearchExpression all:
                foreach (var child in all.Expressions) {
                    Validate(child);
                }
                break;

            case AnyNodeSearchExpression any:
                if (any.Expressions.Length == 0) {
                    throw new ArgumentException("'any' expression must contain at least one child expression.");
                }

                foreach (var child in any.Expressions) {
                    Validate(child);
                }
                break;

            case NotNodeSearchExpression not:
                Validate(not.Expression);
                break;

            case ExistsNodeSearchExpression exists:
                Validate(exists.Expression);
                break;

            case NodeExistsSearchExpression node:
                Validate(node.Node);
                break;

            case NodeNameSearchExpression name:
                Validate(name.Node);
                ArgumentException.ThrowIfNullOrWhiteSpace(name.Value);
                break;

            case NodeAttributeSearchExpression attribute:
                Validate(attribute.Node);
                ArgumentException.ThrowIfNullOrWhiteSpace(attribute.Key);
                if (!IsOperator(attribute.Operator, SearchOperators.Exists)) {
                    ArgumentException.ThrowIfNullOrWhiteSpace(attribute.Value);
                }
                break;

            case NodeTextSearchExpression text:
                Validate(text.Node);
                ArgumentException.ThrowIfNullOrWhiteSpace(text.Value);
                break;

            case NodeConnectedSearchExpression connected:
                Validate(connected.Left);
                Validate(connected.Right);
                break;

            case NodePathSearchExpression path:
                Validate(path.Left);
                Validate(path.Right);
                ValidateDepth(path.MinDepth, path.MaxDepth);
                break;

            case NodeDescendantSearchExpression descendant:
                Validate(descendant.Ancestor);
                Validate(descendant.Descendant);
                ValidateDepth(descendant.MinDepth, descendant.MaxDepth);
                break;

            case NodeDegreeSearchExpression degree:
                Validate(degree.Node);
                if (degree.Value < 0) {
                    throw new ArgumentOutOfRangeException(nameof(degree.Value), "Degree comparison value must be non-negative.");
                }
                break;

            case NodeSameSearchExpression same:
                Validate(same.Left);
                Validate(same.Right);
                break;

            case NodeNotSameSearchExpression notSame:
                Validate(notSame.Left);
                Validate(notSame.Right);
                break;

            default:
                throw new NotSupportedException($"Unsupported search expression type '{expression.GetType().Name}'.");
        }
    }

    private static void Validate(NodeSearchNodeSelector selector) {
        switch (selector) {
            case NodeVariableSearchSelector variable:
                ArgumentException.ThrowIfNullOrWhiteSpace(variable.Name);
                break;

            case NodeLiteralSearchSelector literal:
                break;

            default:
                throw new NotSupportedException($"Unsupported node selector type '{selector.GetType().Name}'.");
        }
    }

    private static void ValidateDepth(int minDepth, int maxDepth) {
        if (minDepth < 0 || maxDepth < 0) {
            throw new ArgumentOutOfRangeException(nameof(minDepth), "Depth values must be non-negative.");
        }

        if (maxDepth < minDepth) {
            throw new ArgumentOutOfRangeException(nameof(maxDepth), "Max depth must be greater than or equal to min depth.");
        }
    }

    private static string[] NormalizeReturnVariables(string[]? returnVariables) {
        return (returnVariables is { Length: > 0 } ? returnVariables : ["n"])
            .Where(static variable => !string.IsNullOrWhiteSpace(variable))
            .Select(static variable => variable.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool MatchesAttribute(Node node, string key, string op, string? value) {
        if (!node.Attributes.TryGetValue(key, out var attributeValue)) {
            return false;
        }

        return IsOperator(op, SearchOperators.Exists) || MatchesText(attributeValue, op, value ?? string.Empty);
    }

    private static bool MatchesText(string source, string op, string value) {
        if (IsOperator(op, SearchOperators.Equal)) {
            return string.Equals(source, value, StringComparison.OrdinalIgnoreCase);
        }

        if (IsOperator(op, SearchOperators.NotEquals)) {
            return !string.Equals(source, value, StringComparison.OrdinalIgnoreCase);
        }

        if (IsOperator(op, SearchOperators.Contains)) {
            return Contains(source, value);
        }

        if (IsOperator(op, SearchOperators.StartsWith)) {
            return source.StartsWith(value, StringComparison.OrdinalIgnoreCase);
        }

        if (IsOperator(op, SearchOperators.EndsWith)) {
            return source.EndsWith(value, StringComparison.OrdinalIgnoreCase);
        }

        throw new ArgumentException($"Unsupported text operator '{op}'.");
    }

    private static bool MatchesText(NodeLocalId source, string op, string value) => MatchesText(source.ToString(), op, value);

    private static bool MatchesNumber(int source, string op, int value) {
        if (IsOperator(op, SearchOperators.Equal)) {
            return source == value;
        }

        if (IsOperator(op, SearchOperators.NotEquals)) {
            return source != value;
        }

        if (IsOperator(op, SearchOperators.GreaterThan)) {
            return source > value;
        }

        if (IsOperator(op, SearchOperators.GreaterThanOrEqual)) {
            return source >= value;
        }

        if (IsOperator(op, SearchOperators.LessThan)) {
            return source < value;
        }

        if (IsOperator(op, SearchOperators.LessThanOrEqual)) {
            return source <= value;
        }

        throw new ArgumentException($"Unsupported number operator '{op}'.");
    }

    private static double GetTextComparisonScore(string source, string op, string value) {
        if (IsOperator(op, SearchOperators.Equal) && string.Equals(source, value, StringComparison.OrdinalIgnoreCase)) {
            return 1;
        }

        if (IsOperator(op, SearchOperators.Contains) && Contains(source, value)) {
            return 0.85;
        }

        return 0.4;
    }

    private static double GetTextComparisonScore(NodeLocalId source, string op, string value) => GetTextComparisonScore(source.ToString(), op, value);

    private static double GetTextScore(Node node, string text) {
        var normalizedText = text.Trim();
        if (normalizedText.Length == 0) {
            return 0;
        }

        if (string.Equals(node.LocalId.ToString(), normalizedText, StringComparison.OrdinalIgnoreCase)) {
            return 1;
        }

        if (Contains(node.LocalId.ToString(), normalizedText)) {
            return 0.85;
        }

        if (node.Attributes.Any(attribute => Contains(attribute.Value, normalizedText))) {
            return 0.65;
        }

        var terms = normalizedText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static term => term.Length > 1)
            .ToArray();
        if (terms.Length == 0) {
            return 0;
        }

        var searchable = new StringBuilder(node.LocalId.ToString());
        foreach (var attribute in node.Attributes) {
            searchable.Append(' ').Append(attribute.Key).Append(' ').Append(attribute.Value);
        }

        var searchableText = searchable.ToString();
        return terms.All(term => Contains(searchableText, term))
            ? 0.5
            : 0;
    }

    private static bool Contains(string source, string value) {
        return CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            source,
            value,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
    }

    private static bool IsOperator(string actual, string expected) {
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAscending(string direction) {
        return string.Equals(direction, SearchOrderDirections.Ascending, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDescending(string direction) {
        return string.Equals(direction, SearchOrderDirections.Descending, StringComparison.OrdinalIgnoreCase);
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(IEnumerable<T> source) {
        foreach (var item in source) {
            yield return item;
            await Task.Yield();
        }
    }

    private static async Task<HashSet<string>?> GetKnownSelectorNamesAsync(
        SearchSolution solution,
        NodeSearchNodeSelector selector,
        SearchGraph graph) {
        switch (selector) {
            case NodeLiteralSearchSelector literal:
                var literalNode = await graph.TryGetNodeAsync(literal.Name).ConfigureAwait(false);
                return literalNode is null
                    ? []
                    : new HashSet<string>([NormalizeNodeName(literalNode.LocalId)], StringComparer.OrdinalIgnoreCase);

            case NodeVariableSearchSelector variable when solution.Bindings.TryGetValue(variable.Name, out var node):
                return new HashSet<string>([NormalizeNodeName(node.LocalId)], StringComparer.OrdinalIgnoreCase);

            case NodeVariableSearchSelector:
                return null;

            default:
                throw new NotSupportedException($"Unsupported node selector type '{selector.GetType().Name}'.");
        }
    }

    private static async Task<bool> AnyAsync<T>(IAsyncEnumerable<T> source, CancellationToken cancellationToken) {
        await foreach (var _ in source.WithCancellation(cancellationToken).ConfigureAwait(false)) {
            return true;
        }

        return false;
    }

    private static double PathScore(int distance) {
        return distance <= 0 ? 1 : 0.7 / distance;
    }

    private static bool SameNode(Node left, Node right) {
        return string.Equals(NormalizeNodeName(left.LocalId), NormalizeNodeName(right.LocalId), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeNodeName(string nodeName) {
        return nodeName.Replace('\\', '/').Trim('/');
    }

    private static string NormalizeNodeName(NodeLocalId nodeName) {
        return NormalizeNodeName(nodeName.ToString());
    }

    private static int NormalizeLimit(int limit) {
        return Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);
    }

    private static IReadOnlyCollection<string> AddMatch(IReadOnlyCollection<string> matches, string? match) {
        if (string.IsNullOrWhiteSpace(match))
            return matches;
        return matches.Append(match).ToArray();
    }

    private sealed record SearchSolution(
        IReadOnlyDictionary<string, Node> Bindings,
        double Score,
        IReadOnlyCollection<string> MatchedBy) {
        public SearchSolution AddMatch(string? match, double score = 0) {
            return string.IsNullOrWhiteSpace(match)
                ? this with { Score = Score + score }
                : this with { Score = Score + score, MatchedBy = GraphSearchService.AddMatch(MatchedBy, match) };
        }
    }

    private sealed class SearchGraph {
        private readonly IGraphStorage _storage;
        private readonly IGraphNodeStream _nodeStream;
        private readonly CancellationToken _cancellationToken;
        private readonly Dictionary<string, Node> _nodesByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Node[]> _connectionsByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, NodeDistance[]> _reachableCache = new(StringComparer.OrdinalIgnoreCase);

        public SearchGraph(IGraphStorage storage, CancellationToken cancellationToken) {
            if (storage is not IGraphNodeStream nodeStream) {
                throw new NotSupportedException("The configured graph storage provider does not expose a node stream.");
            }

            _storage = storage;
            _nodeStream = nodeStream;
            _cancellationToken = cancellationToken;
        }

        public IAsyncEnumerable<Node> Nodes => EnumerateNodesAsync();

        public async Task<Node?> TryGetNodeAsync(string name) {
            var normalizedName = NormalizeNodeName(name);
            if (_nodesByName.TryGetValue(normalizedName, out var node)) {
                return node;
            }

            var result = await _storage.Get(new NodePath(normalizedName.Split('/', StringSplitOptions.RemoveEmptyEntries))).ConfigureAwait(false);
            if (result.Status != ServiceResultStatus.Ok || result.Value is null) {
                return null;
            }

            return Remember(new Node(result.Value));
        }

        public async Task<int> GetDegreeAsync(Node node) {
            return (await GetConnectionsAsync(node).ConfigureAwait(false)).Length;
        }

        public async Task<IReadOnlyCollection<NodeDistance>> GetReachableAsync(Node start, int minDepth, int maxDepth, bool includeSelf) {
            var cacheKey = $"{NormalizeNodeName(start.LocalId)}\u001f{minDepth}\u001f{maxDepth}\u001f{includeSelf}";
            if (_reachableCache.TryGetValue(cacheKey, out var cached)) {
                return cached;
            }

            var result = new Dictionary<string, NodeDistance>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { NormalizeNodeName(start.LocalId) };
            var queue = new Queue<(Node Node, int Depth)>();
            queue.Enqueue((start, 0));

            if (includeSelf && minDepth == 0) {
                result[NormalizeNodeName(start.LocalId)] = new NodeDistance(start, 0);
            }

            while (queue.Count > 0) {
                var (node, depth) = queue.Dequeue();
                if (depth >= maxDepth) {
                    continue;
                }

                foreach (var connected in await GetConnectionsAsync(node).ConfigureAwait(false)) {
                    if (!visited.Add(NormalizeNodeName(connected.LocalId))) {
                        continue;
                    }

                    var nextDepth = depth + 1;
                    if (nextDepth >= minDepth) {
                        result[NormalizeNodeName(connected.LocalId)] = new NodeDistance(connected, nextDepth);
                    }

                    queue.Enqueue((connected, nextDepth));
                }
            }

            var reachable = result.Values
                .OrderBy(static value => value.Distance)
                .ThenBy(static value => value.Node.LocalId)
                .ToArray();
            _reachableCache[cacheKey] = reachable;
            return reachable;
        }

        private async IAsyncEnumerable<Node> EnumerateNodesAsync() {
            await foreach (var node in _nodeStream.EnumerateNodesAsync(_cancellationToken).WithCancellation(_cancellationToken).ConfigureAwait(false)) {
                yield return Remember(new Node(node));
            }
        }

        private async Task<Node[]> GetConnectionsAsync(Node node) {
            var key = NormalizeNodeName(node.LocalId);
            if (_connectionsByName.TryGetValue(key, out var cached)) {
                return cached;
            }

            var connectedResult = await _storage.GetConnectedNodesAsync(node.State).ConfigureAwait(false);
            if (connectedResult.Status != ServiceResultStatus.Ok || connectedResult.Value is null) {
                throw new InvalidOperationException(connectedResult.Error ?? $"Failed to read connections for node '{node.LocalId}'.");
            }

            var connections = connectedResult.Value
                .Select(static state => new Node(state))
                .Select(Remember)
                .OrderBy(static connection => connection.LocalId)
                .ToArray();
            _connectionsByName[key] = connections;
            return connections;
        }

        private Node Remember(Node node) {
            _nodesByName[NormalizeNodeName(node.LocalId)] = node;
            return node;
        }

        public int GetDescendantDepth(Node ancestor, Node descendant) {
            var ancestorName = NormalizeNodeName(ancestor.LocalId);
            var descendantName = NormalizeNodeName(descendant.LocalId);
            if (!IsStrictPathDescendant(ancestorName, descendantName)) {
                return -1;
            }

            return CountPathSegments(descendantName) - CountPathSegments(ancestorName);
        }

        private static bool IsStrictPathDescendant(string ancestorName, string nodeName) {
            return nodeName.Length > ancestorName.Length
                && nodeName.StartsWith(ancestorName + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static int CountPathSegments(string nodeName) {
            return NormalizeNodeName(nodeName).Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
        }
    }

    private sealed record NodeDistance(Node Node, int Distance);
}
