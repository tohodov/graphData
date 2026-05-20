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

        var explanations = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        HashSet<string>? candidateNames = null;
        var connectedToAll = query.ConnectedToAll ?? [];
        var connectedToAny = query.ConnectedToAny ?? [];

        if (query.DescendantOf is not null)
        {
            var descendants = await GetDescendantNamesAsync(query.DescendantOf, explanations, scores).ConfigureAwait(false);
            candidateNames = ApplyRequiredSet(candidateNames, descendants.Keys);
        }

        foreach (var pattern in connectedToAll)
        {
            var connected = await GetReachableNamesAsync(pattern, explanations, scores).ConfigureAwait(false);
            candidateNames = ApplyRequiredSet(candidateNames, connected.Keys);
        }

        if (connectedToAny.Length > 0)
        {
            var anyConnected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pattern in connectedToAny)
            {
                var connected = await GetReachableNamesAsync(pattern, explanations, scores).ConfigureAwait(false);
                anyConnected.UnionWith(connected.Keys);
            }

            candidateNames = ApplyRequiredSet(candidateNames, anyConnected);
        }

        var candidates = candidateNames is null
            ? await GetAllNodesAsync().ConfigureAwait(false)
            : await GetNodesByNameAsync(candidateNames).ConfigureAwait(false);

        var matches = new List<NodeSearchMatch>();
        foreach (var node in candidates)
        {
            var name = NormalizeNodeName(node.Name);
            var nodeExplanations = explanations.TryGetValue(name, out var list)
                ? list
                : [];
            var score = scores.GetValueOrDefault(name);

            if (!string.IsNullOrWhiteSpace(query.Text))
            {
                var textScore = GetTextScore(node, query.Text);
                if (textScore <= 0)
                {
                    continue;
                }

                nodeExplanations.Add($"text:{query.Text}");
                score += textScore;
            }

            matches.Add(new NodeSearchMatch
            {
                Node = node,
                Score = Math.Round(score, 4),
                MatchedBy = nodeExplanations.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            });
        }

        var limit = NormalizeLimit(query.Limit);
        return matches
            .OrderByDescending(static match => match.Score)
            .ThenBy(static match => match.Node.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();
    }

    private static void Validate(NodeSearchQuery query)
    {
        if (query.DescendantOf?.MaxDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query.DescendantOf.MaxDepth), "Depth must be non-negative.");
        }

        foreach (var pattern in (query.ConnectedToAll ?? []).Concat(query.ConnectedToAny ?? []))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pattern.NodeName);
            if (pattern.MaxDepth < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pattern.MaxDepth), "Depth must be non-negative.");
            }
        }

        if (query.DescendantOf is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(query.DescendantOf.NodeName);
        }
    }

    private async Task<IReadOnlyCollection<Node>> GetAllNodesAsync()
    {
        if (_storage is not IGraphNodeCatalog catalog)
        {
            throw new NotSupportedException("The configured graph storage provider does not expose a node catalog.");
        }

        return await catalog.GetAllNodesAsync().ConfigureAwait(false);
    }

    private async Task<Dictionary<string, int>> GetDescendantNamesAsync(
        HierarchySearchPattern pattern,
        Dictionary<string, List<string>> explanations,
        Dictionary<string, double> scores)
    {
        var ancestor = await _storage.Get(pattern.NodeName).ConfigureAwait(false);
        if (ancestor is null)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        var ancestorName = NormalizeNodeName(ancestor.Name);
        var ancestorDepth = CountPathSegments(ancestorName);
        var descendants = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var allNodes = await GetAllNodesAsync().ConfigureAwait(false);

        foreach (var node in allNodes)
        {
            var nodeName = NormalizeNodeName(node.Name);
            if (!IsStrictPathDescendant(ancestorName, nodeName))
            {
                continue;
            }

            var depth = CountPathSegments(nodeName) - ancestorDepth;
            if (depth <= 0 || depth > pattern.MaxDepth)
            {
                continue;
            }

            descendants[nodeName] = depth;
            AddExplanation(explanations, nodeName, $"descendant-of:{ancestorName} depth={depth}");
            AddScore(scores, nodeName, 0.75 + 0.25 / depth);
        }

        return descendants;
    }

    private async Task<Dictionary<string, int>> GetReachableNamesAsync(
        ConnectionSearchPattern pattern,
        Dictionary<string, List<string>> explanations,
        Dictionary<string, double> scores)
    {
        var start = await _storage.Get(pattern.NodeName).ConfigureAwait(false);
        if (start is null)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(Node Node, int Depth)>();

        visited.Add(start.Name);
        queue.Enqueue((start, 0));

        if (pattern.IncludeSelf)
        {
            result[start.Name] = 0;
            AddExplanation(explanations, start.Name, $"connected-to:{start.Name} depth=0");
            AddScore(scores, start.Name, 1);
        }

        while (queue.Count > 0)
        {
            var (node, depth) = queue.Dequeue();
            if (depth >= pattern.MaxDepth)
            {
                continue;
            }

            var connectedNodes = await _storage.GetConnectedNodesAsync(node).ConfigureAwait(false);
            foreach (var connected in connectedNodes)
            {
                if (!visited.Add(connected.Name))
                {
                    continue;
                }

                var nextDepth = depth + 1;
                result[connected.Name] = nextDepth;
                AddExplanation(explanations, connected.Name, $"connected-to:{start.Name} depth={nextDepth}");
                AddScore(scores, connected.Name, 0.7 / nextDepth);
                queue.Enqueue((connected, nextDepth));
            }
        }

        return result;
    }

    private async Task<IReadOnlyCollection<Node>> GetNodesByNameAsync(IEnumerable<string> names)
    {
        var nodes = new List<Node>();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var node = await _storage.Get(name).ConfigureAwait(false);
            if (node is not null)
            {
                nodes.Add(node);
            }
        }

        return nodes;
    }

    private static HashSet<string> ApplyRequiredSet(HashSet<string>? current, IEnumerable<string> required)
    {
        var requiredSet = new HashSet<string>(required.Select(NormalizeNodeName), StringComparer.OrdinalIgnoreCase);
        if (current is null)
        {
            return requiredSet;
        }

        current.IntersectWith(requiredSet);
        return current;
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

    private static void AddExplanation(Dictionary<string, List<string>> explanations, string nodeName, string explanation)
    {
        var normalizedName = NormalizeNodeName(nodeName);
        if (!explanations.TryGetValue(normalizedName, out var list))
        {
            list = [];
            explanations[normalizedName] = list;
        }

        list.Add(explanation);
    }

    private static void AddScore(Dictionary<string, double> scores, string nodeName, double score)
    {
        var normalizedName = NormalizeNodeName(nodeName);
        scores[normalizedName] = scores.GetValueOrDefault(normalizedName) + score;
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

    private static string NormalizeNodeName(string nodeName)
    {
        return nodeName.Replace('\\', '/').Trim('/');
    }

    private static int NormalizeLimit(int limit)
    {
        return Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);
    }
}
