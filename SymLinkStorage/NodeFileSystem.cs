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
    internal string StorageRootPath => storageRootPath;

    public override NodeLocalId LocalId { get; }
    public override NodeGlobalId GlobalId => new NodeGlobalId(
        GetRelativePath(storageRootPath, FolderPath)
            .Split(DirectorySeparatorChar, AltDirectorySeparatorChar)
            .Where(static part => part is not "." and not "")
            .Select(GraphData.SymLinkStorage.SymLinkGraphStorage.NormalizeNodeName));
    public override IReadOnlyCollection<Edge> Edges => edges ??= GetEdges().ToArray();
    public override IReadOnlyCollection<Node> Nodes => nodes ??= Edges
        .SelectMany(static edge => new[] { edge.Node1, edge.Node2 })
        .Where(node => node.GlobalId != GlobalId)
        .DistinctBy(static node => node.GlobalId)
        .ToArray();
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
    public NodeFileSystem(DirectoryInfo info, string storageRootPath) {
        info = new DirectoryInfo(GetFullPath(info.FullName));
        LocalId = new NodeLocalId(info.Name);
        this.storageRootPath = GetFullPath(storageRootPath);
        parentPath = info.Parent?.FullName ?? this.storageRootPath;
    }
    public NodeFileSystem(NodeLocalId name, NodeFileSystem parent) {
        LocalId = name;
        storageRootPath = parent.storageRootPath;
        parentPath = parent.FolderPath;
    }

    public bool IsExists() => Directory.Exists(FolderPath);
    public DirectoryInfo GetInfo() => new DirectoryInfo(FolderPath);

    public IEnumerable<Edge> GetEdges() {
        if (!IsExists())
            yield break;

        if (TryGetParent(out var parent))
            yield return new DirectoryEdgeFileSystem(parent, this);

        foreach (var child in GetChildNodes())
            yield return new DirectoryEdgeFileSystem(this, child);

        foreach (var link in GetSymLinks())
            yield return new LinkEdgeFileSystem(this, link);
    }

    IEnumerable<NodeFileSystem> GetChildNodes() {
        var directory = new DirectoryInfo(FolderPath);
        foreach (var entry in directory.EnumerateDirectories()) {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            yield return new NodeFileSystem(entry, storageRootPath);
        }
    }

    bool TryGetParent([NotNullWhen(true)] out NodeFileSystem? parent) {
        parent = null;
        if (IsStorageRoot(parentPath))
            return false;

        var parentDirectory = new DirectoryInfo(parentPath);
        if (!parentDirectory.Exists)
            return false;

        parent = new NodeFileSystem(parentDirectory, storageRootPath);
        return true;
    }

    bool IsStorageRoot(string path) =>
        string.Equals(
            NormalizeDirectoryPath(path),
            NormalizeDirectoryPath(storageRootPath),
            StringComparison.OrdinalIgnoreCase);

    static string NormalizeDirectoryPath(string path) =>
        GetFullPath(path)
            .TrimEnd(DirectorySeparatorChar, AltDirectorySeparatorChar);

    IEnumerable<SymLink> GetSymLinks() {
        var directory = new DirectoryInfo(FolderPath);

        foreach (var entry in directory.EnumerateFileSystemInfos()) {
            if (string.Equals(entry.Name, MetadataFileName, StringComparison.OrdinalIgnoreCase))
                continue;
            var isReparse = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
            if (!isReparse)
                continue;
            var targetPath = GetResolvedLinkTarget(entry);
            if (targetPath is null)
                continue;
            yield return new SymLink {
                Directory = FolderPath,
                Name = entry.Name,
                TargetPath = targetPath
            };
        }
    }

    static string? GetResolvedLinkTarget(FileSystemInfo entry) {
        var targetPath = entry switch {
            DirectoryInfo directory => directory.LinkTarget,
            FileInfo file => file.LinkTarget,
            _ => new DirectoryInfo(entry.FullName).LinkTarget
        };
        if (string.IsNullOrWhiteSpace(targetPath))
            return null;

        return IsPathFullyQualified(targetPath)
            ? GetFullPath(targetPath)
            : GetFullPath(targetPath, GetDirectoryName(entry.FullName) ?? Directory.GetCurrentDirectory());
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

internal abstract record EdgeFileSystem : Edge {
    protected EdgeFileSystem(NodeFileSystem node1, NodeFileSystem node2) {
        Node1FileSystem = node1;
        Node2FileSystem = node2;
    }

    public NodeFileSystem Node1FileSystem { get; }
    public NodeFileSystem Node2FileSystem { get; }

    public override Node Node1 => Node1FileSystem;
    public override Node Node2 => Node2FileSystem;
}

internal sealed record DirectoryEdgeFileSystem : EdgeFileSystem {
    public DirectoryEdgeFileSystem(NodeFileSystem parent, NodeFileSystem child)
        : base(parent, child) {
    }
}

internal sealed record LinkEdgeFileSystem : Edge {
    NodeFileSystem? child;

    public SymLink Link { get; }
    public NodeFileSystem Parent { get; }
    public NodeFileSystem Child => child ??= new NodeFileSystem(new DirectoryInfo(Link.TargetPath), Parent.StorageRootPath);

    public override Node Node1 => Parent;
    public override Node Node2 => Child;

    public LinkEdgeFileSystem(NodeFileSystem node1, SymLink link) {
        Parent = node1;
        Link = link;
    }
}
