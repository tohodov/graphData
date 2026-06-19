namespace Abstractions;

internal abstract class NodeState : IEquatable<NodeState>
{
    public abstract NodeLocalId LocalId { get; }
    public abstract NodeGlobalId GlobalId { get; }
    public abstract ICollection<EdgeState> Edges { get; }
    public abstract ILazyCollection<NodeState> Nodes { get; }
    public abstract IDictionary<string, string> Attributes { get; set; }

    bool IEquatable<NodeState>.Equals(NodeState? other) => GlobalId.Equals(other?.GlobalId);
    public override bool Equals(object? obj) => GlobalId.Equals((obj as NodeState)?.GlobalId);
    public override int GetHashCode() => GlobalId.GetHashCode();
}
internal interface ILazyCollection<T> : ICollection<T> { //TODO заменить IEnumerable на IAsyncEnumerable
    IAsyncEnumerable<T> Traverse();
}
