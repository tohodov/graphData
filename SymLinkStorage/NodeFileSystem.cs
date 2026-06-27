using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Abstractions;
using static System.IO.Path;

namespace Storage;

internal sealed class NodeFileSystem : NodeBacking {
    const string MetadataFileName = "node.json";

    readonly string parentPath;
    readonly string folderPath;
    internal readonly SymLinkGraphStorage storage;
    EdgeBacking[]? edgeSnapshot;
    IReadOnlyCollection<EdgeBacking> EdgeSnapshot => edgeSnapshot ??= GetEdges().ToArray();
    NodeBacking[]? nodeSnapshot2;
    IReadOnlyCollection<NodeBacking> NodeSnapshot => nodeSnapshot2 ??= EdgeSnapshot//TODO переписать на постепенное чтение в LiveNodeCollection
            .SelectMany(static edge => new[] { edge.Node1, edge.Node2 })
            .Where(node => node.GlobalId != GlobalId)
            .DistinctBy(static node => node.GlobalId)
            .ToArray();
    IDictionary<string, string>? attributes;
    Dictionary<string, string>? attributesSnapshot;

    public string FolderPath => folderPath; //TODO encapsulate
    public string MetadataPath => Combine(FolderPath, MetadataFileName);

    public override NodeLocalId LocalId { get; }
    public override NodeRef.InternalId GlobalId => new NodeRef.InternalId(
        GetRelativePath(storage.root.FullName, FolderPath)
            .Split(DirectorySeparatorChar, AltDirectorySeparatorChar)
            .Where(static part => part is not "." and not "")
            .Select(SymLinkGraphStorage.NormalizeNodeName)
            .Select(x => new NodeLocalId(x)));
    public override IAsyncCollection<EdgeBacking> Edges { get; }
    public override IAsyncCollection<NodeBacking> Nodes { get; }
    public override IDictionary<string, string> Attributes {
        get => attributes ??= new LiveAttributeDictionary(this);
        set {
            ReplaceAttributes(value);
        }
    }

    public NodeFileSystem(NodeLocalId id, DirectoryInfo info, SymLinkGraphStorage storage) {
        this.storage = storage;
        folderPath = ResolveDirectoryPath(info.FullName);
        LocalId = id;
        parentPath = GetDirectoryName(folderPath) ?? "";
        Edges = new LiveEdgeCollection(this);
        Nodes = new LiveNodeCollection(this);
    }
    public NodeFileSystem(DirectoryInfo info, SymLinkGraphStorage storage) {
        this.storage = storage;
        folderPath = ResolveDirectoryPath(info.FullName);
        LocalId = new NodeLocalId(new DirectoryInfo(folderPath).Name);
        parentPath = GetDirectoryName(folderPath) ?? "";
        Edges = new LiveEdgeCollection(this);
        Nodes = new LiveNodeCollection(this);
    }
    public NodeFileSystem(NodeLocalId name, SymLinkGraphStorage storage) {
        this.storage = storage;
        LocalId = name;
        parentPath = storage.root.FullName;
        folderPath = ResolveDirectoryPath(Combine(parentPath, name.ToString()));
        Edges = new LiveEdgeCollection(this);
        Nodes = new LiveNodeCollection(this);
    }
    public NodeFileSystem(NodeLocalId name, NodeFileSystem parent) {
        storage = parent.storage;
        LocalId = name;
        parentPath = parent.FolderPath;
        folderPath = ResolveDirectoryPath(Combine(parentPath, name.ToString()));
        Edges = new LiveEdgeCollection(this);
        Nodes = new LiveNodeCollection(this);
    }

    public bool IsExists() => Directory.Exists(FolderPath);
    public DirectoryInfo GetInfo() => new DirectoryInfo(FolderPath);
    internal override Task Delete() => storage.Delete(this);

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

    internal async Task ConnectTo(NodeBacking target) {
        await storage.Connect(GlobalId, target.GlobalId);
        InvalidateGraphCache();
        if (target is NodeFileSystem fileSystemState)
            fileSystemState.InvalidateGraphCache();
    }

    internal async Task DisconnectFrom(NodeBacking target) {
        if (!NodeSnapshot.Any(node => node.GlobalId == target.GlobalId))
            return;
        await storage.Disconnect(GlobalId, target.GlobalId);
        InvalidateGraphCache();
        if (target is NodeFileSystem fileSystemState)
            fileSystemState.InvalidateGraphCache();
        return;
    }

    internal void InvalidateGraphCache() {
        edgeSnapshot = null;
        nodeSnapshot2 = null;
    }

    IEnumerable<EdgeBacking> GetEdges() {
        if (!IsExists())
            yield break;

        if (TryGetParent(out var parent))
            yield return new DirectoryEdgeFileSystemState(parent, this);

        foreach (var child in GetChildNodes())
            yield return new DirectoryEdgeFileSystemState(this, child);

        foreach (var link in GetSymLinks())
            yield return new LinkEdgeFileSystemState(this, link);
    }

    IEnumerable<NodeFileSystem> GetChildNodes() {
        var directory = new DirectoryInfo(FolderPath);
        foreach (var entry in directory.EnumerateDirectories()) {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            yield return new NodeFileSystem(entry, storage);
        }
    }

    bool TryGetParent([NotNullWhen(true)] out NodeFileSystem? parent) {
        parent = null;
        if (IsStorageRoot(folderPath) || IsStorageRoot(parentPath))
            return false;

        var parentDirectory = new DirectoryInfo(parentPath);
        if (!parentDirectory.Exists)
            return false;

        parent = new NodeFileSystem(parentDirectory, storage);
        return true;
    }

    bool IsStorageRoot(string path) =>
        string.Equals(
            ResolveDirectoryPath(path).TrimEnd(DirectorySeparatorChar, AltDirectorySeparatorChar),
            ResolveDirectoryPath(storage.root.FullName).TrimEnd(DirectorySeparatorChar, AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

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

    internal static string? GetResolvedLinkTarget(FileSystemInfo entry) {
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
        await JsonSerializer.SerializeAsync(stream, data, SymLinkGraphStorage.SerializerOptions);
    }
    public void WriteMetadata(IDictionary<string, string> data) {//TODO move to base
        attributesSnapshot = null;
        using var stream = new FileStream(MetadataPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        JsonSerializer.Serialize(stream, data, SymLinkGraphStorage.SerializerOptions);
    }

    sealed class LiveNodeCollection(NodeFileSystem owner) : IAsyncCollection<NodeBacking> {
        public async Task<NodeBacking> Add(NodeBacking item) {
            if (item is VirtualNodeState) {
                var node = owner.storage.GetInternal(owner, item.LocalId) ?? new NodeFileSystem(item.LocalId, owner);
                Directory.CreateDirectory(node.FolderPath);
                if (item.Attributes.Count > 0)
                    node.WriteMetadata(item.Attributes);
                owner.InvalidateGraphCache();
                return node;
            }

            await owner.ConnectTo(item);
            return item;
        }
        public Task Remove(NodeBacking item) => owner.DisconnectFrom(item);
        public async Task Clear() {
            foreach (var node in owner.NodeSnapshot)
                await owner.DisconnectFrom(node);
        }
        public async Task<bool> Contains(NodeBacking item) => owner.NodeSnapshot.Any(node => node.GlobalId == item.GlobalId);

        IAsyncEnumerator<NodeBacking> IAsyncEnumerable<NodeBacking>.GetAsyncEnumerator(CancellationToken t) => owner.NodeSnapshot.ToAsyncEnumerable().GetAsyncEnumerator(t);
    }

    sealed class LiveEdgeCollection(NodeFileSystem owner) : IAsyncCollection<EdgeBacking> {
        public async Task<EdgeBacking> Add(EdgeBacking item) {
            await owner.ConnectTo(GetOtherEndpoint(item));
            return item;
        }
        public Task Remove(EdgeBacking item) => owner.DisconnectFrom(GetOtherEndpoint(item));
        public async Task Clear() {
            await foreach (var node in owner.Nodes)
                await owner.DisconnectFrom(node);
        }
        public async Task<bool> Contains(EdgeBacking item) {
            var other = GetOtherEndpoint(item);
            return await owner.Nodes.AnyAsync(node => node.GlobalId == other.GlobalId);
        }

        NodeBacking GetOtherEndpoint(EdgeBacking edge) {
            if (edge.Node1.GlobalId == owner.GlobalId)
                return edge.Node2;
            if (edge.Node2.GlobalId == owner.GlobalId)
                return edge.Node1;

            throw new InvalidOperationException($"Edge does not belong to node '{owner.GlobalId}'.");
        }

        IAsyncEnumerator<EdgeBacking> IAsyncEnumerable<EdgeBacking>.GetAsyncEnumerator(CancellationToken t) =>
            owner.EdgeSnapshot.ToAsyncEnumerable().GetAsyncEnumerator(t);
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

internal sealed class DirectoryEdgeFileSystemState(NodeFileSystem node1, NodeFileSystem node2) : EdgeBacking {
    public NodeFileSystem Node1FileSystem { get; } = node1;
    public NodeFileSystem Node2FileSystem { get; } = node2;

    public override NodeBacking Node1 => Node1FileSystem;
    public override NodeBacking Node2 => Node2FileSystem;
}

internal sealed class LinkEdgeFileSystemState(NodeFileSystem node1, SymLink link) : EdgeBacking {
    NodeFileSystem? child;

    public SymLink Link { get; } = link;
    public NodeFileSystem Parent { get; } = node1;
    public NodeFileSystem Child => child ??= new NodeFileSystem(new DirectoryInfo(Link.TargetPath), Parent.storage);

    public override NodeBacking Node1 => Parent;
    public override NodeBacking Node2 => Child;
}
