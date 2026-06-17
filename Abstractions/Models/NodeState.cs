namespace GraphData.Core.Models;

public abstract class NodeState
{
    public abstract NodeLocalId LocalId { get; }
    public abstract NodeGlobalId GlobalId { get; }
    public abstract ICollection<EdgeState> Edges { get; }
    public abstract ICollection<NodeState> Nodes { get; }
    public abstract IDictionary<string, string> Attributes { get; set; }
    public virtual NodeGlobalId? TypeId => null;
}
