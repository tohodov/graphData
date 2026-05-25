global using NodeLocalId = System.String;
//global using NodePath = System.Collections.Generic.IReadOnlyCollection<string>;
public class NodePath : List<string>, IReadOnlyCollection<string> {
    public NodePath(IEnumerable<string> path) : base(path) { }
    public static explicit operator NodePath(string[] path) => new(path);
}