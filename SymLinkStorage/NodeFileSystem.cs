using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using static System.IO.Path;

namespace SymLinkStorage;

internal sealed class NodeFileSystem {
    const string MetadataFileName = "node.json";

    readonly string parentPath;
    readonly string storageRootPath;
    readonly string folderPath;
    internal readonly IGraphStorage storage;
    IReadOnlyCollection<Edge>? edgeSnapshot;
    IReadOnlyCollection<Node>? nodeSnapshot;
    IDictionary<string, string>? attributes;
    Dictionary<string, string>? attributesSnapshot;

    public Node AsNode => new GraphNode(new NodeFileSystemState(this));
    public string FolderPath => folderPath; //TODO encapsulate
    public string MetadataPath => Combine(FolderPath, MetadataFileName);
    internal string StorageRootPath => storageRootPath;

    public NodeLocalId LocalId { get; }
    public NodeGlobalId GlobalId => new NodeGlobalId(
        GetRelativePath(storageRootPath, FolderPath)
            .Split(DirectorySeparatorChar, AltDirectorySeparatorChar)
            .Where(static part => part is not "." and not "")
            .Select(GraphData.SymLinkStorage.SymLinkGraphStorage.NormalizeNodeName));
    public ICollection<Edge> Edges { get; }
    public ICollection<Node> Nodes { get; }
    public IDictionary<string, string> Attributes {
        get => attributes ??= new LiveAttributeDictionary(this);
        set {
            ReplaceAttributes(value);
        }
    }

    public NodeFileSystem(DirectoryInfo info, IGraphStorage storage) {
        this.storage = storage;
        folderPath = ResolveDirectoryPath(info.FullName);
        LocalId = new NodeLocalId(new DirectoryInfo(folderPath).Name);
        parentPath = GetDirectoryName(folderPath) ?? "";
        storageRootPath = parentPath;
        Edges = new LiveEdgeCollection(this);
        Nodes = new LiveNodeCollection(this);
    }
    public NodeFileSystem(NodeLocalId name, string storageRootPath, IGraphStorage storage) {
        this.storage = storage;
        LocalId = name;
        this.storageRootPath = ResolveDirectoryPath(storageRootPath);
        parentPath = this.storageRootPath;
        folderPath = ResolveDirectoryPath(Combine(parentPath, name.ToString()));
        Edges = new LiveEdgeCollection(this);
        Nodes = new LiveNodeCollection(this);
    }
    public NodeFileSystem(DirectoryInfo info, string storageRootPath, IGraphStorage storage) {
        this.storage = storage;
        info = new DirectoryInfo(GetFullPath(info.FullName));
        LocalId = new NodeLocalId(info.Name);
        this.storageRootPath = ResolveDirectoryPath(storageRootPath);
        folderPath = ResolveDirectoryPath(info.FullName);
        parentPath = GetDirectoryName(folderPath) ?? this.storageRootPath;
        Edges = new LiveEdgeCollection(this);
        Nodes = new LiveNodeCollection(this);
    }
    public NodeFileSystem(NodeLocalId name, NodeFileSystem parent) {
        storage = parent.storage;
        LocalId = name;
        storageRootPath = parent.storageRootPath;
        parentPath = parent.FolderPath;
        folderPath = ResolveDirectoryPath(Combine(parentPath, name.ToString()));
        Edges = new LiveEdgeCollection(this);
        Nodes = new LiveNodeCollection(this);
    }

    public bool IsExists() => Directory.Exists(FolderPath);
    public DirectoryInfo GetInfo() => new DirectoryInfo(FolderPath);

    public IReadOnlyCollection<Edge> ReadEdges() {
        return edgeSnapshot ??= GetEdges().ToArray();
    }

    public IReadOnlyCollection<Node> ReadNodes() {
        return nodeSnapshot ??= ReadEdges()
            .SelectMany(static edge => new[] { edge.Node1, edge.Node2 })
            .Where(node => node.GlobalId != GlobalId)
            .DistinctBy(static node => node.GlobalId)
            .ToArray();
    }

    internal IDictionary<string, string> ReadAttributes() {
        if (attributesSnapshot is not null)
            return attributesSnapshot;

        try {
            var metadata = new FileInfo(Combine(FolderPath, MetadataFileName));
            if (!metadata.Exists) {
                attributesSnapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return attributesSnapshot;
            }
            using var stream = metadata.OpenRead();
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            attributesSnapshot = dict is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase);
            return attributesSnapshot;
        } catch {
            attributesSnapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return attributesSnapshot;
        }
    }

    internal void ReplaceAttributes(IDictionary<string, string> data) {
        var copy = new Dictionary<string, string>(data, StringComparer.OrdinalIgnoreCase);
        WriteMetadata(copy);
        attributesSnapshot = copy;
    }

    internal void ConnectTo(Node target) {
        ThrowIfFailed(storage.Connect(GlobalId, target.GlobalId).GetAwaiter().GetResult());
        InvalidateGraphCache();
        if (target.TryGetState<NodeFileSystemState>(out var fileSystemState))
            fileSystemState.Handle.InvalidateGraphCache();
    }

    internal bool DisconnectFrom(Node target) {
        if (!ReadNodes().Any(node => node.GlobalId == target.GlobalId))
            return false;

        ThrowIfFailed(storage.Disconnect(GlobalId, target.GlobalId).GetAwaiter().GetResult());
        InvalidateGraphCache();
        if (target.TryGetState<NodeFileSystemState>(out var fileSystemState))
            fileSystemState.Handle.InvalidateGraphCache();
        return true;
    }

    internal void InvalidateGraphCache() {
        edgeSnapshot = null;
        nodeSnapshot = null;
    }

    static void ThrowIfFailed(ServiceResult result) {
        if (result.Status == ServiceResultStatus.Ok)
            return;

        throw new InvalidOperationException(result.Error ?? $"Graph operation failed with status '{result.Status}'.");
    }

    IEnumerable<Edge> GetEdges() {
        if (!IsExists())
            yield break;

        if (TryGetParent(out var parent))
            yield return new GraphEdge(new DirectoryEdgeFileSystemState(parent, this));

        foreach (var child in GetChildNodes())
            yield return new GraphEdge(new DirectoryEdgeFileSystemState(this, child));

        foreach (var link in GetSymLinks())
            yield return new GraphEdge(new LinkEdgeFileSystemState(this, link));
    }

    IEnumerable<NodeFileSystem> GetChildNodes() {
        var directory = new DirectoryInfo(FolderPath);
        foreach (var entry in directory.EnumerateDirectories()) {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            yield return new NodeFileSystem(entry, storageRootPath, storage);
        }
    }

    bool TryGetParent([NotNullWhen(true)] out NodeFileSystem? parent) {
        parent = null;
        if (IsStorageRoot(parentPath))
            return false;

        var parentDirectory = new DirectoryInfo(parentPath);
        if (!parentDirectory.Exists)
            return false;

        parent = new NodeFileSystem(parentDirectory, storageRootPath, storage);
        return true;
    }

    bool IsStorageRoot(string path) =>
        string.Equals(
            NormalizeDirectoryPath(path),
            NormalizeDirectoryPath(storageRootPath),
            StringComparison.OrdinalIgnoreCase);

    static string NormalizeDirectoryPath(string path) =>
        ResolveDirectoryPath(path)
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

        var fullTargetPath = IsPathFullyQualified(targetPath)
            ? GetFullPath(targetPath)
            : GetFullPath(targetPath, GetDirectoryName(entry.FullName) ?? Directory.GetCurrentDirectory());

        return ResolveDirectoryPath(fullTargetPath);
    }

    internal static string ResolveDirectoryPath(string path) {
        var fullPath = GetFullPath(path);
        var root = GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
            return fullPath;

        var relative = GetRelativePath(root, fullPath);
        if (relative is "." or "")
            return GetFullPath(root);

        var current = root;
        foreach (var segment in relative.Split(DirectorySeparatorChar, AltDirectorySeparatorChar)) {
            if (segment is "" or ".")
                continue;

            current = Combine(current, segment);
            current = ResolveCurrentDirectoryLink(current);
        }

        return GetFullPath(current);
    }

    static string ResolveCurrentDirectoryLink(string path) {
        try {
            var info = new DirectoryInfo(path);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) == 0)
                return path;

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            return target is null
                ? path
                : GetFullPath(target.FullName);
        } catch (IOException) {
            return path;
        } catch (UnauthorizedAccessException) {
            return path;
        }
    }

    async Task WriteMetadataAsync(Dictionary<string, string> data) {//TODO move to base
        await using var stream = new FileStream(MetadataPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, data, GraphData.SymLinkStorage.SymLinkGraphStorage.SerializerOptions);
    }
    public void WriteMetadata(IDictionary<string, string> data) {//TODO move to base
        attributesSnapshot = null;
        using var stream = new FileStream(MetadataPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        JsonSerializer.Serialize(stream, data, GraphData.SymLinkStorage.SymLinkGraphStorage.SerializerOptions);
    }

    sealed class LiveNodeCollection(NodeFileSystem owner) : ICollection<Node> {
        public int Count => owner.ReadNodes().Count;
        public bool IsReadOnly => false;

        public void Add(Node item) => owner.ConnectTo(item);
        public bool Remove(Node item) => owner.DisconnectFrom(item);
        public void Clear() {
            foreach (var node in owner.ReadNodes().ToArray())
                owner.DisconnectFrom(node);
        }
        public bool Contains(Node item) => owner.ReadNodes().Any(node => node.GlobalId == item.GlobalId);
        public void CopyTo(Node[] array, int arrayIndex) => owner.ReadNodes().ToArray().CopyTo(array, arrayIndex);
        public IEnumerator<Node> GetEnumerator() => owner.ReadNodes().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    sealed class LiveEdgeCollection(NodeFileSystem owner) : ICollection<Edge> {
        public int Count => owner.ReadEdges().Count;
        public bool IsReadOnly => false;

        public void Add(Edge item) => owner.ConnectTo(GetOtherEndpoint(item));
        public bool Remove(Edge item) => owner.DisconnectFrom(GetOtherEndpoint(item));
        public void Clear() {
            foreach (var node in owner.ReadNodes().ToArray())
                owner.DisconnectFrom(node);
        }
        public bool Contains(Edge item) {
            var other = GetOtherEndpoint(item);
            return owner.ReadNodes().Any(node => node.GlobalId == other.GlobalId);
        }
        public void CopyTo(Edge[] array, int arrayIndex) => owner.ReadEdges().ToArray().CopyTo(array, arrayIndex);
        public IEnumerator<Edge> GetEnumerator() => owner.ReadEdges().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        Node GetOtherEndpoint(Edge edge) {
            if (edge.Node1.GlobalId == owner.GlobalId)
                return edge.Node2;
            if (edge.Node2.GlobalId == owner.GlobalId)
                return edge.Node1;

            throw new InvalidOperationException($"Edge does not belong to node '{owner.GlobalId}'.");
        }
    }

    sealed class LiveAttributeDictionary(NodeFileSystem owner) : IDictionary<string, string> {
        public ICollection<string> Keys => owner.ReadAttributes().Keys.ToArray();
        public ICollection<string> Values => owner.ReadAttributes().Values.ToArray();
        public int Count => owner.ReadAttributes().Count;
        public bool IsReadOnly => false;

        public string this[string key] {
            get => owner.ReadAttributes()[key];
            set => Mutate(attributes => attributes[key] = value);
        }

        public void Add(string key, string value) => Mutate(attributes => attributes.Add(key, value));
        public bool Remove(string key) {
            if (!owner.ReadAttributes().ContainsKey(key))
                return false;

            Mutate(attributes => attributes.Remove(key));
            return true;
        }
        public void Clear() => owner.ReplaceAttributes(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        public bool ContainsKey(string key) => owner.ReadAttributes().ContainsKey(key);
        public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value) => owner.ReadAttributes().TryGetValue(key, out value);
        public void Add(KeyValuePair<string, string> item) => Add(item.Key, item.Value);
        public bool Contains(KeyValuePair<string, string> item) => ((ICollection<KeyValuePair<string, string>>)owner.ReadAttributes()).Contains(item);
        public void CopyTo(KeyValuePair<string, string>[] array, int arrayIndex) => ((ICollection<KeyValuePair<string, string>>)owner.ReadAttributes()).CopyTo(array, arrayIndex);
        public bool Remove(KeyValuePair<string, string> item) {
            if (!Contains(item))
                return false;

            return Remove(item.Key);
        }
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => owner.ReadAttributes().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        void Mutate(Action<Dictionary<string, string>> mutation) {
            var copy = new Dictionary<string, string>(owner.ReadAttributes(), StringComparer.OrdinalIgnoreCase);
            mutation(copy);
            owner.ReplaceAttributes(copy);
        }
    }
}

internal sealed class NodeFileSystemState(NodeFileSystem handle) : NodeState {
    public NodeFileSystem Handle { get; } = handle;

    public override NodeLocalId LocalId => Handle.LocalId;
    public override NodeGlobalId GlobalId => Handle.GlobalId;
    public override ICollection<Edge> Edges => Handle.Edges;
    public override ICollection<Node> Nodes => Handle.Nodes;

    public override IDictionary<string, string> Attributes {
        get => Handle.Attributes;
        set => Handle.Attributes = value;
    }
}

internal sealed class DirectoryEdgeFileSystemState(NodeFileSystem node1, NodeFileSystem node2) : EdgeState {
    public NodeFileSystem Node1FileSystem { get; } = node1;
    public NodeFileSystem Node2FileSystem { get; } = node2;

    public override Node Node1 => Node1FileSystem.AsNode;
    public override Node Node2 => Node2FileSystem.AsNode;
}

internal sealed class LinkEdgeFileSystemState(NodeFileSystem node1, SymLink link) : EdgeState {
    NodeFileSystem? child;

    public SymLink Link { get; } = link;
    public NodeFileSystem Parent { get; } = node1;
    public NodeFileSystem Child => child ??= new NodeFileSystem(new DirectoryInfo(Link.TargetPath), Parent.StorageRootPath, Parent.storage);

    public override Node Node1 => Parent.AsNode;
    public override Node Node2 => Child.AsNode;
}
