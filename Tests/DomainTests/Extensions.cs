using Storage;

public static class Extensions {
    internal static NodeFileSystem GetBacking(this CarrierNode node) => (NodeFileSystem)node.Backing;
}
