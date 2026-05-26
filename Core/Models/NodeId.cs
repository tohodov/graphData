global using NodeLocalId = System.String;
//global using NodePath = System.Collections.Generic.IReadOnlyCollection<string>;
public class NodePath : List<string>, IReadOnlyCollection<string> {
    public NodePath(IEnumerable<string> path) : base(path) { }
    public static implicit operator NodePath(string path) =>
        new(path.Split('/', StringSplitOptions.RemoveEmptyEntries));
    public static explicit operator NodePath?(string[]? path) => path == null ? null : new(path);
    public override string ToString() => string.Join("/", this);
}
