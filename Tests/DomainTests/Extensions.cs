using Storage;

public static class Extensions {
    internal static NodeFileSystem GetBacking(this Node node) => (NodeFileSystem)node.Backing;
}
