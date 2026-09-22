using System.Collections;

namespace Abstractions;

internal abstract class CarrierNodeBacking : IEquatable<CarrierNodeBacking>
{
    public abstract NodeLocalId LocalId { get; }
    public abstract InternalId GlobalId { get; }
    public abstract IAsyncCollection<CarrierEdgeBacking> Edges { get; }
    public abstract IAsyncCollection<CarrierNodeBacking> Nodes { get; }
    public abstract IDictionary<string, string> Attributes { get; set; }
    internal virtual Task Delete() => Task.CompletedTask;

    bool IEquatable<CarrierNodeBacking>.Equals(CarrierNodeBacking? other) => GlobalId.Equals(other?.GlobalId);
    public override bool Equals(object? obj) => GlobalId.Equals((obj as CarrierNodeBacking)?.GlobalId);
    public override int GetHashCode() => GlobalId.GetHashCode();

    public async IAsyncEnumerable<CarrierNodeBacking> Traverse() {
        var visited = new HashSet<InternalId>();
        var stack = new Stack<CarrierNodeBacking>();
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
