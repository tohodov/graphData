using System.Globalization;
using System.Text;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

public sealed class GraphSearchService(IGraphStorage storage)
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 500;

    private readonly IGraphStorage _storage = storage;

    public async Task<IReadOnlyCollection<NodeSearchMatch>> SearchNodesAsync(NodeSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        Validate(query);

        var graph = await SearchGraph.CreateAsync(_storage).ConfigureAwait(false);
        var returnVariables = NormalizeReturnVariables(query.Return);
        var effectiveWhere = BuildEffectiveWhere(query.Where, returnVariables);
        var initial = new SearchSolution(new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase), 0, []);
        var solutions = Evaluate(effectiveWhere, [initial], graph);
        var distinctSolutions = DistinctByReturnVariables(solutions, returnVariables);
        var limit = NormalizeLimit(query.Limit);

        return distinctSolutions
            .OrderByDescending(static solution => solution.Score)
            .ThenBy(solution => string.Join('\u001f', returnVariables.Select(variable => solution.Bindings[variable].Name)), StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(solution => new NodeSearchMatch
            {
                Node = solution.Bindings[returnVariables[0]],
                Bindings = returnVariables.ToDictionary(
                    static variable => variable,
                    variable => solution.Bindings[variable],
                    StringComparer.OrdinalIgnoreCase),
                Score = Math.Round(Math.Max(1, solution.Score), 4),
                MatchedBy = solution.MatchedBy.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            })
            .ToArray();
    }

    private static AllNodeSearchExpression BuildEffectiveWhere(NodeSearchExpression? where, string[] returnVariables)
    {
        var bindableVariables = where is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : GetBindableVariables(where);
        var expressions = new List<NodeSearchExpression>();

        expressions.AddRange(returnVariables
            .Where(variable => !bindableVariables.Contains(variable))
            .Select(CreateReturnBinder));

        if (where is not null)
        {
            expressions.Add(where);
        }

        expressions.AddRange(returnVariables.Select(CreateReturnBinder));

        return new AllNodeSearchExpression { Expressions = expressions.ToArray() };
    }

    private static NodeExistsSearchExpression CreateReturnBinder(string variable)
    {
        return new NodeExistsSearchExpression
        {
            Node = new NodeVariableSearchSelector { Name = variable }
        };
    }

    private static HashSet<string> GetBindableVariables(NodeSearchExpression expression)
    {
        var variables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddBindableVariables(expression, variables);
        return variables;
    }

    private static void AddBindableVariables(NodeSearchExpression expression, ISet<string> variables)
    {
        switch (expression)
        {
            case AllNodeSearchExpression all:
                foreach (var child in all.Expressions)
                {
                    AddBindableVariables(child, variables);
                }
                break;

            case AnyNodeSearchExpression any:
                foreach (var child in any.Expressions)
                {
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

    private static void AddVariable(NodeSearchNodeSelector selector, ISet<string> variables)
    {
        if (selector is NodeVariableSearchSelector variable)
        {
            variables.Add(variable.Name);
        }
    }

    private static IEnumerable<SearchSolution> Evaluate(
        NodeSearchExpression expression,
        IEnumerable<SearchSolution> input,
        SearchGraph graph)
    {
        return expression switch
        {
            AllNodeSearchExpression all => EvaluateAll(all, input, graph),
            AnyNodeSearchExpression any => EvaluateAny(any, input, graph),
            NotNodeSearchExpression not => EvaluateNot(not, input, graph),
            ExistsNodeSearchExpression exists => EvaluateExists(exists, input, graph),
            NodeExistsSearchExpression node => EvaluateNode(node, input, graph),
            NodeNameSearchExpression name => EvaluateName(name, input, graph),
            NodeAttributeSearchExpression attribute => EvaluateAttribute(attribute, input, graph),
            NodeTextSearchExpression text => EvaluateText(text, input, graph),
            NodeConnectedSearchExpression connected => EvaluatePath(
                connected.Left,
                connected.Right,
                minDepth: 1,
                maxDepth: 1,
                includeSelf: false,
                input,
                graph,
                "connected"),
            NodePathSearchExpression path => EvaluatePath(
                path.Left,
                path.Right,
                path.MinDepth,
                path.MaxDepth,
                path.IncludeSelf,
                input,
                graph,
                "path"),
            NodeDescendantSearchExpression descendant => EvaluateDescendant(descendant, input, graph),
            NodeDegreeSearchExpression degree => EvaluateDegree(degree, input, graph),
            NodeSameSearchExpression same => EvaluateSame(same, input, graph),
            NodeNotSameSearchExpression notSame => EvaluateNotSame(notSame, input, graph),
            _ => throw new NotSupportedException($"Unsupported search expression type '{expression.GetType().Name}'.")
        };
    }

    private static IEnumerable<SearchSolution> EvaluateAll(
        AllNodeSearchExpression expression,
        IEnumerable<SearchSolution> input,
        SearchGraph graph)
    {
        var current = input;
        foreach (var child in expression.Expressions.OrderBy(static child => child is NotNodeSearchExpression ? 1 : 0))
        {
            current = Evaluate(child, current, graph);
        }

        return current;
    }

    private static IEnumerable<SearchSolution> EvaluateAny(
        AnyNodeSearchExpression expression,
        IEnumerable<SearchSolution> input,
        SearchGraph graph)
    {
        foreach (var solution in input)
        {
            foreach (var child in expression.Expressions)
            {
                foreach (var result in Evaluate(child, [solution], graph))
                {
                    yield return result;
                }
            }
        }
    }

    private static IEnumerable<SearchSolution> EvaluateNot(
        NotNodeSearchExpression expression,
        IEnumerable<SearchSolution> input,
        SearchGraph graph)
    {
        foreach (var solution in input)
        {
            if (!Evaluate(expression.Expression, [solution], graph).Any())
            {
                yield return solution.AddMatch("not");
            }
        }
    }

    private static IEnumerable<SearchSolution> EvaluateExists(
        ExistsNodeSearchExpression expression,
        IEnumerable<SearchSolution> input,
        SearchGraph graph)
    {
        foreach (var solution in input)
        {
            var inner = Evaluate(expression.Expression, [solution], graph)
                .OrderByDescending(static result => result.Score)
                .FirstOrDefault();
            if (inner is null)
            {
                continue;
            }

            var addedScore = Math.Max(0, inner.Score - solution.Score);
            yield return solution.AddMatch("exists", addedScore);
        }
    }

    private static IEnumerable<SearchSolution> EvaluateNode(
        NodeExistsSearchExpression expression,
        IEnumerable<SearchSolution> input,
        SearchGraph graph)
    {
        return input.SelectMany(solution => BindSelector(
            solution,
            expression.Node,
            graph.Nodes,
            score: 0.05,
            match: "node"));
    }

    private static IEnumerable<SearchSolution> EvaluateName(
        NodeNameSearchExpression expression,
        IEnumerable<SearchSolution> input,
        SearchGraph graph)
    {
        foreach (var solution in input)
        {
            foreach (var candidate in CandidateNodes(solution, expression.Node, graph))
            {
                if (!MatchesText(candidate.Name, expression.Operator, expression.Value))
                {
                    continue;
                }

                var score = GetTextComparisonScore(candidate.Name, expression.Operator, expression.Value);
                var bound = TryBind(solution, expression.Node, candidate, score, $"name:{expression.Operator}:{expression.Value}");
                if (bound is not null)
                {
                    yield return bound;
                }
            }
        }
    }

    private static IEnumerable<SearchSolution> EvaluateAttribute(
        NodeAttributeSearchExpression expression,
        IEnumerable<SearchSolution> input,
        SearchGraph graph)
    {
        foreach (var solution in input)
        {
            foreach (var candidate in CandidateNodes(solution, expression.Node, graph))
            {
                if (!MatchesAttribute(candidate, expression.Key, expression.Operator, expression.Value))
                {
                    continue;
                }

                var bound = TryBind(solution, expression.Node, candidate, 0.65, $"attribute:{expression.Key}:{expression.Operator}");
                if (bound is not null)
                {
                    yield return bound;
                }
            }
        }
    }

    private static IEnumerable<SearchSolution> EvaluateText(
        NodeTextSearchExpression expression,
        IEnumerable<SearchSolution> input,
        SearchGraph graph)
    {
        foreach (var solution in input)
        {
            foreach (var candidate in CandidateNodes(solution, expression.Node, graph))
            {
                var score = GetTextScore(candidate, expression.Value);
                if (score <= 0)
                {
                    continue;
                }

                var bound = TryBind(solution, expression.Node, candidate, score, $"text:{expression.Value}");
                if (bound is not null)
                {
                    yield return bound;
                }
            }
        }
    }

    private static IEnumerable<SearchSolution> EvaluatePath(
        NodeSearchNodeSelector left,
        NodeSearchNodeSelector right,
        int minDepth,
        int maxDepth,
        bool includeSelf,
        IEnumerable<SearchSolution> input,
        SearchGraph graph,
        string matchName)
    {
        foreach (var solution in input)
        {
            foreach (var (leftNode, rightNode, distance) in CandidatePaths(solution, left, right, minDepth, maxDepth, includeSelf, graph))
            {
                var bound = TryBind(solution, left, leftNode, PathScore(distance), $"{matchName}:depth={distance}");
                bound = bound is null ? null : TryBind(bound, right, rightNode);
                if (bound is not null)
                {
                    yield return bound;
                }
            }
        }
    }

    private static IEnumerable<SearchSolution> EvaluateDescendant(
        NodeDescendantSearchExpression expression,
        IEnumerable<SearchSolution> input,
        SearchGraph graph)
    {
        foreach (var solution in input)
        {
            foreach (var (ancestor, descendant, depth) in CandidateDescendants(solution, expression, graph))
            {
                var ancestorName = NormalizeNodeName(ancestor.Name);
                var bound = TryBind(solution, expression.Ancestor, ancestor, 0.3, $"ancestor:{ancestorName}");
                bound = bound is null ? null : TryBind(bound, expression.Descendant, descendant, 0.75 + 0.25 / depth, $"descendant-of:{ancestorName} depth={depth}");
                if (bound is not null)
                {
                    yield return bound;
                }
            }
        }
    }

    private static IEnumerable<SearchSolution> EvaluateDegree(
        NodeDegreeSearchExpression expression,
        IEnumerable<SearchSolution> input,
        SearchGraph graph)
    {
        foreach (var solution in input)
        {
            foreach (var candidate in CandidateNodes(solution, expression.Node, graph))
            {
                var degree = graph.GetDegree(candidate);
                if (!MatchesNumber(degree, expression.Operator, expression.Value))
                {
                    continue;
                }

                var bound = TryBind(solution, expression.Node, candidate, 0.45, $"degree:{expression.Operator}:{expression.Value}");
                if (bound is not null)
                {
                    yield return bound;
                }
            }
        }
    }

    private static IEnumerable<SearchSolution> EvaluateSame(
        NodeSameSearchExpression expression,
        IEnumerable<SearchSolution> input,
        SearchGraph graph)
    {
        foreach (var solution in input)
        {
            foreach (var (left, right) in CandidateNodePairs(solution, expression.Left, expression.Right, graph))
            {
                if (!SameNode(left, right))
                {
                    continue;
                }

                var bound = TryBind(solution, expression.Left, left, 0.1, "same");
                bound = bound is null ? null : TryBind(bound, expression.Right, right);
                if (bound is not null)
                {
                    yield return bound;
                }
            }
        }
    }

    private static IEnumerable<SearchSolution> EvaluateNotSame(
        NodeNotSameSearchExpression expression,
        IEnumerable<SearchSolution> input,
        SearchGraph graph)
    {
        foreach (var solution in input)
        {
            foreach (var (left, right) in CandidateNodePairs(solution, expression.Left, expression.Right, graph))
            {
                if (SameNode(left, right))
                {
                    continue;
                }

                var bound = TryBind(solution, expression.Left, left, 0.1, "not-same");
                bound = bound is null ? null : TryBind(bound, expression.Right, right);
                if (bound is not null)
                {
                    yield return bound;
                }
            }
        }
    }

    private static IEnumerable<Node> CandidateNodes(SearchSolution solution, NodeSearchNodeSelector selector, SearchGraph graph)
    {
        return selector switch
        {
            NodeLiteralSearchSelector literal => graph.TryGetNode(literal.Name, out var node)
                ? [node]
                : [],
            NodeVariableSearchSelector variable => solution.Bindings.TryGetValue(variable.Name, out var node)
                ? [node]
                : graph.Nodes,
            _ => throw new NotSupportedException($"Unsupported node selector type '{selector.GetType().Name}'.")
        };
    }

    private static IEnumerable<SearchSolution> BindSelector(
        SearchSolution solution,
        NodeSearchNodeSelector selector,
        IEnumerable<Node> candidates,
        double score,
        string match)
    {
        foreach (var candidate in candidates)
        {
            var bound = TryBind(solution, selector, candidate, score, match);
            if (bound is not null)
            {
                yield return bound;
            }
        }
    }

    private static SearchSolution? TryBind(
        SearchSolution solution,
        NodeSearchNodeSelector selector,
        Node candidate,
        double score = 0,
        string? match = null)
    {
        switch (selector)
        {
            case NodeLiteralSearchSelector literal:
                return string.Equals(NormalizeNodeName(literal.Name), NormalizeNodeName(candidate.Name), StringComparison.OrdinalIgnoreCase)
                    ? solution.AddMatch(match, score)
                    : null;

            case NodeVariableSearchSelector variable:
                if (solution.Bindings.TryGetValue(variable.Name, out var existing))
                {
                    return SameNode(existing, candidate)
                        ? solution.AddMatch(match, score)
                        : null;
                }

                var bindings = new Dictionary<string, Node>(solution.Bindings, StringComparer.OrdinalIgnoreCase)
                {
                    [variable.Name] = candidate
                };
                return new SearchSolution(bindings, solution.Score + score, AddMatch(solution.MatchedBy, match));

            default:
                throw new NotSupportedException($"Unsupported node selector type '{selector.GetType().Name}'.");
        }
    }

    private static IEnumerable<(Node Left, Node Right)> CandidateNodePairs(
        SearchSolution solution,
        NodeSearchNodeSelector left,
        NodeSearchNodeSelector right,
        SearchGraph graph)
    {
        var leftCandidates = CandidateNodes(solution, left, graph).ToArray();
        var rightCandidates = CandidateNodes(solution, right, graph).ToArray();

        foreach (var leftNode in leftCandidates)
        {
            foreach (var rightNode in rightCandidates)
            {
                yield return (leftNode, rightNode);
            }
        }
    }

    private static IEnumerable<(Node Left, Node Right, int Distance)> CandidatePaths(
        SearchSolution solution,
        NodeSearchNodeSelector left,
        NodeSearchNodeSelector right,
        int minDepth,
        int maxDepth,
        bool includeSelf,
        SearchGraph graph)
    {
        var leftCandidates = CandidateNodes(solution, left, graph).ToArray();
        var rightCandidates = CandidateNodes(solution, right, graph).ToArray();
        var rightNames = rightCandidates
            .Select(static node => NormalizeNodeName(node.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var leftNode in leftCandidates)
        {
            foreach (var path in graph.GetReachable(leftNode, minDepth, maxDepth, includeSelf))
            {
                if (rightNames.Contains(NormalizeNodeName(path.Node.Name)))
                {
                    yield return (leftNode, path.Node, path.Distance);
                }
            }
        }
    }

    private static IEnumerable<(Node Ancestor, Node Descendant, int Depth)> CandidateDescendants(
        SearchSolution solution,
        NodeDescendantSearchExpression expression,
        SearchGraph graph)
    {
        foreach (var ancestor in CandidateNodes(solution, expression.Ancestor, graph))
        {
            foreach (var descendant in CandidateNodes(solution, expression.Descendant, graph))
            {
                var depth = graph.GetDescendantDepth(ancestor, descendant);
                if (depth >= expression.MinDepth && depth <= expression.MaxDepth)
                {
                    yield return (ancestor, descendant, depth);
                }
            }
        }
    }

    private static IReadOnlyCollection<SearchSolution> DistinctByReturnVariables(
        IEnumerable<SearchSolution> solutions,
        string[] returnVariables)
    {
        var distinct = new Dictionary<string, SearchSolution>(StringComparer.OrdinalIgnoreCase);
        foreach (var solution in solutions)
        {
            if (returnVariables.Any(variable => !solution.Bindings.ContainsKey(variable)))
            {
                continue;
            }

            var key = string.Join('\u001f', returnVariables.Select(variable => NormalizeNodeName(solution.Bindings[variable].Name)));
            if (!distinct.TryGetValue(key, out var existing) || solution.Score > existing.Score)
            {
                distinct[key] = solution;
            }
        }

        return distinct.Values;
    }

    private static void Validate(NodeSearchQuery query)
    {
        if (query.Return is { Length: 0 })
        {
            throw new ArgumentException("At least one return variable must be provided.", nameof(query));
        }

        foreach (var variable in NormalizeReturnVariables(query.Return))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(variable);
        }

        if (query.Where is not null)
        {
            Validate(query.Where);
        }
    }

    private static void Validate(NodeSearchExpression expression)
    {
        switch (expression)
        {
            case AllNodeSearchExpression all:
                foreach (var child in all.Expressions)
                {
                    Validate(child);
                }
                break;

            case AnyNodeSearchExpression any:
                if (any.Expressions.Length == 0)
                {
                    throw new ArgumentException("'any' expression must contain at least one child expression.");
                }

                foreach (var child in any.Expressions)
                {
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
                if (!IsOperator(attribute.Operator, SearchOperators.Exists))
                {
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
                if (degree.Value < 0)
                {
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

    private static void Validate(NodeSearchNodeSelector selector)
    {
        switch (selector)
        {
            case NodeVariableSearchSelector variable:
                ArgumentException.ThrowIfNullOrWhiteSpace(variable.Name);
                break;

            case NodeLiteralSearchSelector literal:
                ArgumentException.ThrowIfNullOrWhiteSpace(literal.Name);
                break;

            default:
                throw new NotSupportedException($"Unsupported node selector type '{selector.GetType().Name}'.");
        }
    }

    private static void ValidateDepth(int minDepth, int maxDepth)
    {
        if (minDepth < 0 || maxDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minDepth), "Depth values must be non-negative.");
        }

        if (maxDepth < minDepth)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth), "Max depth must be greater than or equal to min depth.");
        }
    }

    private static string[] NormalizeReturnVariables(string[]? returnVariables)
    {
        return (returnVariables is { Length: > 0 } ? returnVariables : ["n"])
            .Where(static variable => !string.IsNullOrWhiteSpace(variable))
            .Select(static variable => variable.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool MatchesAttribute(Node node, string key, string op, string? value)
    {
        if (!node.Attributes.TryGetValue(key, out var attributeValue))
        {
            return false;
        }

        return IsOperator(op, SearchOperators.Exists) || MatchesText(attributeValue, op, value ?? string.Empty);
    }

    private static bool MatchesText(string source, string op, string value)
    {
        if (IsOperator(op, SearchOperators.Equal))
        {
            return string.Equals(source, value, StringComparison.OrdinalIgnoreCase);
        }

        if (IsOperator(op, SearchOperators.NotEquals))
        {
            return !string.Equals(source, value, StringComparison.OrdinalIgnoreCase);
        }

        if (IsOperator(op, SearchOperators.Contains))
        {
            return Contains(source, value);
        }

        if (IsOperator(op, SearchOperators.StartsWith))
        {
            return source.StartsWith(value, StringComparison.OrdinalIgnoreCase);
        }

        if (IsOperator(op, SearchOperators.EndsWith))
        {
            return source.EndsWith(value, StringComparison.OrdinalIgnoreCase);
        }

        throw new ArgumentException($"Unsupported text operator '{op}'.");
    }

    private static bool MatchesNumber(int source, string op, int value)
    {
        if (IsOperator(op, SearchOperators.Equal))
        {
            return source == value;
        }

        if (IsOperator(op, SearchOperators.NotEquals))
        {
            return source != value;
        }

        if (IsOperator(op, SearchOperators.GreaterThan))
        {
            return source > value;
        }

        if (IsOperator(op, SearchOperators.GreaterThanOrEqual))
        {
            return source >= value;
        }

        if (IsOperator(op, SearchOperators.LessThan))
        {
            return source < value;
        }

        if (IsOperator(op, SearchOperators.LessThanOrEqual))
        {
            return source <= value;
        }

        throw new ArgumentException($"Unsupported number operator '{op}'.");
    }

    private static double GetTextComparisonScore(string source, string op, string value)
    {
        if (IsOperator(op, SearchOperators.Equal) && string.Equals(source, value, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (IsOperator(op, SearchOperators.Contains) && Contains(source, value))
        {
            return 0.85;
        }

        return 0.4;
    }

    private static double GetTextScore(Node node, string text)
    {
        var normalizedText = text.Trim();
        if (normalizedText.Length == 0)
        {
            return 0;
        }

        if (string.Equals(node.Name, normalizedText, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (Contains(node.Name, normalizedText))
        {
            return 0.85;
        }

        if (node.Attributes.Any(attribute => Contains(attribute.Value, normalizedText)))
        {
            return 0.65;
        }

        var terms = normalizedText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static term => term.Length > 1)
            .ToArray();
        if (terms.Length == 0)
        {
            return 0;
        }

        var searchable = new StringBuilder(node.Name);
        foreach (var attribute in node.Attributes)
        {
            searchable.Append(' ').Append(attribute.Key).Append(' ').Append(attribute.Value);
        }

        var searchableText = searchable.ToString();
        return terms.All(term => Contains(searchableText, term))
            ? 0.5
            : 0;
    }

    private static bool Contains(string source, string value)
    {
        return CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            source,
            value,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
    }

    private static bool IsOperator(string actual, string expected)
    {
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static double PathScore(int distance)
    {
        return distance <= 0 ? 1 : 0.7 / distance;
    }

    private static bool SameNode(Node left, Node right)
    {
        return string.Equals(NormalizeNodeName(left.Name), NormalizeNodeName(right.Name), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeNodeName(string nodeName)
    {
        return nodeName.Replace('\\', '/').Trim('/');
    }

    private static int NormalizeLimit(int limit)
    {
        return Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);
    }

    private static IReadOnlyCollection<string> AddMatch(IReadOnlyCollection<string> matches, string? match)
    {
        if (string.IsNullOrWhiteSpace(match))
        {
            return matches;
        }

        return matches.Append(match).ToArray();
    }

    private sealed record SearchSolution(
        IReadOnlyDictionary<string, Node> Bindings,
        double Score,
        IReadOnlyCollection<string> MatchedBy)
    {
        public SearchSolution AddMatch(string? match, double score = 0)
        {
            return string.IsNullOrWhiteSpace(match)
                ? this with { Score = Score + score }
                : this with { Score = Score + score, MatchedBy = GraphSearchService.AddMatch(MatchedBy, match) };
        }
    }

    private sealed class SearchGraph
    {
        private readonly IReadOnlyDictionary<string, Node> _nodesByName;
        private readonly IReadOnlyDictionary<string, Node[]> _connectionsByName;
        private readonly Dictionary<string, NodeDistance[]> _reachableCache = new(StringComparer.OrdinalIgnoreCase);

        private SearchGraph(
            IReadOnlyCollection<Node> nodes,
            IReadOnlyDictionary<string, Node[]> connectionsByName)
        {
            Nodes = nodes;
            _nodesByName = nodes.ToDictionary(static node => NormalizeNodeName(node.Name), StringComparer.OrdinalIgnoreCase);
            _connectionsByName = connectionsByName;
        }

        public IReadOnlyCollection<Node> Nodes { get; }

        public static async Task<SearchGraph> CreateAsync(IGraphStorage storage)
        {
            if (storage is not IGraphNodeCatalog catalog)
            {
                throw new NotSupportedException("The configured graph storage provider does not expose a node catalog.");
            }

            var nodes = (await catalog.GetAllNodesAsync().ConfigureAwait(false))
                .OrderBy(static node => node.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var knownNodes = nodes
                .Select(static node => NormalizeNodeName(node.Name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var connections = new Dictionary<string, Node[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in nodes)
            {
                var connected = await storage.GetConnectedNodesAsync(node).ConfigureAwait(false);
                connections[NormalizeNodeName(node.Name)] = connected
                    .Where(connection => knownNodes.Contains(NormalizeNodeName(connection.Name)))
                    .OrderBy(static connection => connection.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            return new SearchGraph(nodes, connections);
        }

        public bool TryGetNode(string name, out Node node)
        {
            return _nodesByName.TryGetValue(NormalizeNodeName(name), out node!);
        }

        public int GetDegree(Node node)
        {
            return _connectionsByName.TryGetValue(NormalizeNodeName(node.Name), out var connections)
                ? connections.Length
                : 0;
        }

        public IReadOnlyCollection<NodeDistance> GetReachable(Node start, int minDepth, int maxDepth, bool includeSelf)
        {
            var cacheKey = $"{NormalizeNodeName(start.Name)}\u001f{minDepth}\u001f{maxDepth}\u001f{includeSelf}";
            if (_reachableCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            var result = new Dictionary<string, NodeDistance>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { NormalizeNodeName(start.Name) };
            var queue = new Queue<(Node Node, int Depth)>();
            queue.Enqueue((start, 0));

            if (includeSelf && minDepth == 0)
            {
                result[NormalizeNodeName(start.Name)] = new NodeDistance(start, 0);
            }

            while (queue.Count > 0)
            {
                var (node, depth) = queue.Dequeue();
                if (depth >= maxDepth)
                {
                    continue;
                }

                if (!_connectionsByName.TryGetValue(NormalizeNodeName(node.Name), out var connections))
                {
                    continue;
                }

                foreach (var connected in connections)
                {
                    if (!visited.Add(NormalizeNodeName(connected.Name)))
                    {
                        continue;
                    }

                    var nextDepth = depth + 1;
                    if (nextDepth >= minDepth)
                    {
                        result[NormalizeNodeName(connected.Name)] = new NodeDistance(connected, nextDepth);
                    }

                    queue.Enqueue((connected, nextDepth));
                }
            }

            var reachable = result.Values
                .OrderBy(static value => value.Distance)
                .ThenBy(static value => value.Node.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _reachableCache[cacheKey] = reachable;
            return reachable;
        }

        public int GetDescendantDepth(Node ancestor, Node descendant)
        {
            var ancestorName = NormalizeNodeName(ancestor.Name);
            var descendantName = NormalizeNodeName(descendant.Name);
            if (!IsStrictPathDescendant(ancestorName, descendantName))
            {
                return -1;
            }

            return CountPathSegments(descendantName) - CountPathSegments(ancestorName);
        }

        private static bool IsStrictPathDescendant(string ancestorName, string nodeName)
        {
            return nodeName.Length > ancestorName.Length
                && nodeName.StartsWith(ancestorName + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static int CountPathSegments(string nodeName)
        {
            return NormalizeNodeName(nodeName).Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
        }
    }

    private sealed record NodeDistance(Node Node, int Distance);
}
