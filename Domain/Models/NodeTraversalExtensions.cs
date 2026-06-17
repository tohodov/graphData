namespace GraphData.Core.Models;

public static class NodeTraversalExtensions
{
    public static IEnumerable<Node> Traverse(this Node node, int maxDepth = int.MaxValue, bool includeSelf = true)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (maxDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(maxDepth), "Max depth must be non-negative.");

        var visited = new HashSet<NodeGlobalId>();
        var stack = new Stack<(Node Node, int Depth)>();
        stack.Push((node, 0));

        while (stack.Count > 0) {
            var (current, depth) = stack.Pop();
            if (!visited.Add(current.GlobalId))
                continue;

            if (includeSelf || depth > 0)
                yield return current;

            if (depth >= maxDepth)
                continue;

            foreach (var neighbor in current.Nodes.Reverse())
                if (!visited.Contains(neighbor.GlobalId))
                    stack.Push((neighbor, depth + 1));
        }
    }
}
