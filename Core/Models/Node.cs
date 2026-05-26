namespace GraphData.Core.Models;

public abstract record Node {
    public abstract NodeLocalId LocalId { get; }
    public abstract NodeGlobalId GlobalId { get; }
     
    public abstract IReadOnlyCollection<Edge> Edges { get; }
    public abstract IReadOnlyCollection<Node> Nodes { get; }

    public abstract IReadOnlyDictionary<string, string> Attributes { get; set; }
}