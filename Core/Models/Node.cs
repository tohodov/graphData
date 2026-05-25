namespace GraphData.Core.Models;

public abstract record Node {
    public abstract NodeLocalId LocalId { get; }
    public abstract NodePath GlobalId { get; }

    public abstract IReadOnlyDictionary<string, Edge> Edges { get; }
    public abstract IReadOnlyCollection<Node> Nodes { get; }

    public abstract IReadOnlyDictionary<string, string> Attributes { get; set; }
}

public abstract record TypedNode : Node {
    public abstract NodeType Type { get; }
}

public abstract record NodeType : Node {
    public abstract TypedNode[] Properties { get; }
    public abstract bool Constraint();
}