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
    readonly string storageRootPath;
    IReadOnlyCollection<Edge>? edges;
    IReadOnlyCollection<Node>? nodes;
    IReadOnlyDictionary<string, string>? attributes;

    public string FolderPath => Combine(parentPath, LocalId); //TODO encapsulate
    public string MetadataPath => Combine(FolderPath, MetadataFileName);

    public override NodeLocalId LocalId { get; }
    public override NodeGlobalId GlobalId => new NodeGlobalId(
        GetRelativePath(storageRootPath, FolderPath)
            .Split(DirectorySeparatorChar, AltDirectorySeparatorChar)
            .Where(static part => part is not "." and not "")
            .Select(GraphData.SymLinkStorage.SymLinkGraphStorage.NormalizeNodeName));
    public override IReadOnlyCollection<Edge> Edges => edges ??= GetEdges().ToArray();
    public override IReadOnlyCollection<Node> Nodes => nodes ??= Edges.SelectMany(x => new[] { x.Node1, x.Node2 }).Except([this]).Distinct().ToArray();
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
        LocalId = new NodeLocalId(info.Name);
        parentPath = info.Parent?.FullName ?? "";
        storageRootPath = parentPath;
    }
    public NodeFileSystem(NodeLocalId name, string storageRootPath) {
        LocalId = name;
        this.storageRootPath = storageRootPath;
        parentPath = storageRootPath;
    }
    public NodeFileSystem(NodeLocalId name, NodeFileSystem parent) {
        LocalId = name;
        storageRootPath = parent.storageRootPath;
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

    async Task WriteMetadataAsync(Dictionary<string, string> data) {//TODO move to base
        await using var stream = new FileStream(MetadataPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, data, GraphData.SymLinkStorage.SymLinkGraphStorage.SerializerOptions);
    }
    public void WriteMetadata(IDictionary<string, string> data) {//TODO move to base
        attributes = null;
        using var stream = new FileStream(MetadataPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        JsonSerializer.Serialize(stream, data, GraphData.SymLinkStorage.SymLinkGraphStorage.SerializerOptions);
    }
}

internal record EdgeFileSystem : Edge {
    NodeFileSystem? child;

    public SymLink Link { get; }
    public NodeFileSystem Parent { get; }
    public NodeFileSystem Child => child ??= new NodeFileSystem(new(Link.Name), Parent);

    public override Node Node1 => Parent;
    public override Node Node2 => Child;

    public EdgeFileSystem(NodeFileSystem node1, SymLink link) {
        Parent = node1;
        Link = link;
    }
}
