using System.Collections;

namespace Abstractions;

internal abstract class NodeBacking : IEquatable<NodeBacking>
{
    public abstract NodeLocalId LocalId { get; }
    public abstract InternalId GlobalId { get; }
    public abstract IAsyncCollection<EdgeBacking> Edges { get; }
    public abstract IAsyncCollection<NodeBacking> Nodes { get; }
    public abstract IDictionary<string, string> Attributes { get; set; }
    internal virtual Task Delete() => Task.CompletedTask;

    bool IEquatable<NodeBacking>.Equals(NodeBacking? other) => GlobalId.Equals(other?.GlobalId);
    public override bool Equals(object? obj) => GlobalId.Equals((obj as NodeBacking)?.GlobalId);
    public override int GetHashCode() => GlobalId.GetHashCode();

    public async IAsyncEnumerable<NodeBacking> Traverse() {
        var visited = new HashSet<InternalId>();
        var stack = new Stack<NodeBacking>();
        stack.Push(this);

        while (stack.Count > 0) {
            var node = stack.Pop();
            if (!visited.Add(node.GlobalId))
                continue;

            yield return node;

            await foreach (var neighbor in node.Nodes)
                if (!visited.Contains(neighbor.GlobalId))
                    stack.Push(neighbor);

            await Task.Yield();
        }
    }
}
