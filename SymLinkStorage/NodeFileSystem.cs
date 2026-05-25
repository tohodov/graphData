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

    public string FolderPath => Combine(parentPath, LocalId); //TODO инкапсулировать
    public string MetadataPath => Combine(FolderPath, MetadataFileName);

    public override string LocalId { get; }
    public override NodePath GlobalId => new NodePath(FolderPath.Split('\\', '/'));//TODO удалить RootPath
    public override IReadOnlyDictionary<string, Edge> Edges => edges ??= GetEdges().ToDictionary(static x => x.Link.Name, static x => (Edge)x, StringComparer.OrdinalIgnoreCase);
    public override IReadOnlyCollection<Node> Nodes => nodes ??= Edges.Values.SelectMany(x => new[] { x.Node1, x.Node2 }).Except([this]).Distinct().ToArray();
    public override IReadOnlyDictionary<string, string> Attributes {
        get {
            if (attributes is not null)
                return attributes;
            try {
                var metadata = new FileInfo(Combine(FolderPath, MetadataFileName));
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
        set {
            WriteMetadata(value.ToDictionary());
            attributes = value;
        }
    }

    public NodeFileSystem(DirectoryInfo info) {
        LocalId = info.Name;
        parentPath = info.Parent?.FullName ?? "";
    }
    public NodeFileSystem(string name, string storageRootPath) {
        LocalId = name;
        parentPath = storageRootPath;
    }
    public NodeFileSystem(string name, NodeFileSystem parent) {
        LocalId = name;
        parentPath = parent.FolderPath;
    }

    public bool IsExists() => Directory.Exists(FolderPath);
    public DirectoryInfo GetInfo() => new DirectoryInfo(FolderPath);

    public IEnumerable<EdgeFileSystem> GetEdges() {
        if (!IsExists())
            yield break;
        foreach (var link in GetSymLinks())
            yield return new EdgeFileSystem(this, link);
    }

    IEnumerable<SymLink> GetSymLinks() {
        var directory = new DirectoryInfo(FolderPath);
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
            yield return new SymLink {
                Directory = FolderPath,
                Name = entry.Name,
                TargetPath = entry.FullName
            };
        }
    }

    async Task WriteMetadataAsync(Dictionary<string, string> data) {//TODO занести в базовый
        await using var stream = new FileStream(MetadataPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, data, GraphData.SymLinkStorage.SymLinkGraphStorage.SerializerOptions);
    }
    public void WriteMetadata(IDictionary<string, string> data) {//TODO занести в базовый
        attributes = null;
        using var stream = new FileStream(MetadataPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        JsonSerializer.Serialize(stream, data, GraphData.SymLinkStorage.SymLinkGraphStorage.SerializerOptions);
    }
    internal static NodeFileSystem Create(string name, NodeFileSystem parent, Dictionary<string, string>? attributes = null) {
        var node = new NodeFileSystem(name, parent.FolderPath);
        Directory.CreateDirectory(node.FolderPath);
        if (attributes != null)
            node.WriteMetadata(attributes);
        return node;
    }
}

internal record EdgeFileSystem : Edge {
    NodeFileSystem? child;

    public SymLink Link { get; }
    public NodeFileSystem Parent { get; }
    public NodeFileSystem Child => child ??= new NodeFileSystem(Link.Name, Parent);

    public override Node Node1 => Parent;
    public override Node Node2 => Child;

    public EdgeFileSystem(NodeFileSystem node1, SymLink link) {
        Parent = node1;
        Link = link;
    }
}
