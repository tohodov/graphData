using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Xml.Linq;
using GraphData.Core.Models;
using static System.IO.Path;

namespace SymLinkStorage;

internal record NodeFileSystem : Node {
    const string MetadataFileName = "node.json";

    readonly string parentPath;
    IReadOnlyDictionary<string, Edge>? edges;
    IReadOnlyCollection<Node>? nodes;
    IReadOnlyDictionary<string, string>? attributes;

    public string Path => Combine(parentPath, Name);
    public string MetadataPath => Combine(Path, MetadataFileName);

    public override string Name { get; }
    public override IReadOnlyDictionary<string, Edge> Edges => edges ??= GetEdges().ToDictionary(static x => x.Link.Name, static x => (Edge)x, StringComparer.OrdinalIgnoreCase);
    public override IReadOnlyCollection<Node> Nodes => nodes ??= Edges.Values.SelectMany(x => new[] { x.Node1, x.Node2 }).Except([this]).Distinct().ToArray();
    public override IReadOnlyDictionary<string, string> Attributes {
        get {
            if (attributes is not null)
                return attributes;
            try {
                var metadata = new FileInfo(Combine(Path, MetadataFileName));
                if (!metadata.Exists) {
                    attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    return attributes;
                }
                using var stream = metadata.OpenRead();
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
                attributes = dict is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase);
                return attributes;
            } catch {
                attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return attributes;
            }
        }
    }

    public NodeFileSystem(DirectoryInfo info) {
        Name = info.Name;
        parentPath = info.Parent?.FullName ?? "";
    }
    public NodeFileSystem(string name, string storageRootPath) {
        Name = name;
        parentPath = storageRootPath;
    }
    public NodeFileSystem(string name, NodeFileSystem parent) {
        Name = name;
        parentPath = parent.Path;
    }

    public bool IsExists() => Directory.Exists(Path);
    public DirectoryInfo GetInfo() => new DirectoryInfo(Path);

    public IEnumerable<EdgeFileSystem> GetEdges() {
        if (!IsExists())
            yield break;
        foreach (var link in GetSymLinks())
            yield return new EdgeFileSystem(this, link);
    }

    IEnumerable<SymLink> GetSymLinks() {
        var directory = new DirectoryInfo(Path);
        var edges = new List<EdgeFileSystem>();

        foreach (var entry in directory.EnumerateFileSystemInfos()) {
            if (string.Equals(entry.Name, MetadataFileName, StringComparison.OrdinalIgnoreCase))
                continue;
            var isReparse = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
            if (!isReparse)
                continue;
            string? targetPath = null;
            if (entry is DirectoryInfo di) {
                targetPath = di.LinkTarget;
            } else if (entry is FileInfo fi) {
                targetPath = fi.LinkTarget;
            } else {
                var asDir = new DirectoryInfo(entry.FullName);
                targetPath = asDir.LinkTarget;
            }
            var nodeName = GraphData.SymLinkStorage.SymLinkGraphStorage.FromLinkName(entry.Name);
            var targetFullPath = GraphData.SymLinkStorage.SymLinkGraphStorage.ResolveLinkTargetPath(entry.FullName, targetPath);
            yield return new SymLink {
                Directory = Path,
                Name = nodeName,
                TargetPath = targetFullPath,
                TargetRootPath = GraphData.SymLinkStorage.SymLinkGraphStorage.GetStorageRootPath(targetFullPath, nodeName)
            };
        }
    }

    async Task WriteMetadataAsync(Dictionary<string, string> data) {
        await using var stream = new FileStream(MetadataPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, data, GraphData.SymLinkStorage.SymLinkGraphStorage.SerializerOptions);
    }
    public void WriteMetadata(IDictionary<string, string> data) {
        attributes = null;
        using var stream = new FileStream(MetadataPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        JsonSerializer.Serialize(stream, data, GraphData.SymLinkStorage.SymLinkGraphStorage.SerializerOptions);
    }

    internal static NodeFileSystem Create(string name, string storageRootPath, Dictionary<string, string>? attributes = null) {
        var node = new NodeFileSystem(name, storageRootPath);
        Directory.CreateDirectory(node.Path);
        if(attributes != null)
            node.WriteMetadata(attributes);
        return node;
    }
    internal static NodeFileSystem Create(string name, NodeFileSystem parent, Dictionary<string, string>? attributes = null) {
        var node = new NodeFileSystem(name, parent.Path);
        Directory.CreateDirectory(node.Path);
        if (attributes != null)
            node.WriteMetadata(attributes);
        return node;
    }
}

internal record EdgeFileSystem : Edge {
    NodeFileSystem? child;

    public SymLink Link { get; }
    public NodeFileSystem Parent { get; }
    public NodeFileSystem Child => child ??= new NodeFileSystem(Link.Name, Link.TargetRootPath);

    public override Node Node1 => Parent;
    public override Node Node2 => Child;

    public EdgeFileSystem(NodeFileSystem node1, SymLink link) {
        Parent = node1;
        Link = link;
    }
}
