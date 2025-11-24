using System.Xml.Linq;

namespace GraphData.Core.Models;

public abstract record Node {
    public abstract string Name { get; }

    public abstract IReadOnlyDictionary<string, Edge> Edges { get; }
    public abstract IReadOnlyCollection<Node> Nodes { get; }

    public abstract IReadOnlyDictionary<string, string> Attributes { get; }
}

public abstract record TypedNode : Node {
    public abstract NodeType Type { get; }
}

public abstract record NodeType : Node {
    public abstract TypedNode[] Properties { get; }
    public abstract bool Constraint();
}

public class NodeQuery {
    public static readonly NodeQuery Unknown = new NodeQuery();

    public NodeId Name { get; private set; }
    public NodeQuery? Child { get; private set; }

    NodeQuery() { Name = string.Empty; }
    public NodeQuery(NodeId name) {
        this.Name = name;
    }

    public static NodeQuery Parse(string path) => path.Split(['/', '\\']).Select(x => x == "" || x == "." ? Unknown : new NodeQuery(x)).Aggregate((a, b) => a.Child = b);
}