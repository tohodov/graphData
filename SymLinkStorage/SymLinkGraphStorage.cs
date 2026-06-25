using System.Text.Json;
using Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Storage;

internal sealed class SymLinkGraphStorage : IGraphStorage {
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public NodeState Root { get; }

    internal readonly DirectoryInfo root;
    readonly NtfsGraphStorageOptions options;
    readonly ICancellationTokenAccessor cancellationTokens;
    readonly ILogger<SymLinkGraphStorage> logger;

    public SymLinkGraphStorage(IOptions<NtfsGraphStorageOptions> options, ICancellationTokenAccessor cancellationTokens, ILogger<SymLinkGraphStorage> logger) {
        this.options = options.Value;
        this.cancellationTokens = cancellationTokens;
        this.logger = logger;
        root = new DirectoryInfo(Path.GetFullPath(this.options.RootPath));
        if (!root.Exists)
            root.Create();
        Root = new NodeFileSystem(new(), root, this);
    }

    public Task<NodeState> Create(NodeLocalId name, NodeRef? path = null, IDictionary<string, string>? attributes = null) {
        if (!NodeNameValidator.TryValidateSegment(name, "Node name", out var validationError))
            throw new Exception(validationError);

        var parentNode = path != null
            ? FindNode(path)
            : null;
        if (path != null && parentNode is null)
            throw new Exception($"Parent node '{path}' was not found.");

        NodeFileSystem node;
        if (parentNode is null)
            node = new NodeFileSystem(new NodeLocalId(name), this);
        else
            node = new NodeFileSystem(new NodeLocalId(name), parentNode);
        Directory.CreateDirectory(node.FolderPath);
        if (attributes != null)
            node.WriteMetadata(attributes);

        return Task.FromResult<NodeState>(node);
    }

    public Task<NodeState?> Get(NodeRef path) {
        return Task.FromResult<NodeState?>(FindNode(path));
    }

    public Task Delete(NodeState node) => Delete((NodeFileSystem)node);
    Task Delete(NodeFileSystem node) {
        var connections = GetConnectedNodes(node);
        foreach (var connection in connections)
            DeleteLinkIfExists(Path.Combine(GetNodePath(connection), GetLinkName(node.LocalId)));
        DeleteDirectoryWithoutFollowingLinks(node.GetInfo());
        return Task.FromResult(true);
    }
    public Task Delete(NodeRef path) {
        var node = FindNode(path);
        if (node is null)
            return Task.FromResult(false);
        return Delete(node);
    }

    public Task Connect(NodeRef leftPath, NodeRef rightPath) {
        if (leftPath.Equals(rightPath))
            throw new Exception("SourcePath and TargetPath must be different.");
        if (!TryValidateNodeRef(leftPath, "Source node path", out var validationError))
            throw new Exception(validationError);
        if (!TryValidateNodeRef(rightPath, "Target node path", out validationError))
            throw new Exception(validationError);

        var left = FindNode(leftPath);
        var right = FindNode(rightPath);
        if (left is null || right is null)
            return Task.FromResult(false);
        return Task.FromResult(ConnectNodes(left, right));
    }

    public async Task Disconnect(NodeRef leftPath, NodeRef rightPath) {
        if (leftPath.Equals(rightPath))
            throw new Exception("SourcePath and TargetPath must be different.");
        if (!TryValidateNodeRef(leftPath, "Source node path", out var validationError))
            throw new Exception(validationError);
        if (!TryValidateNodeRef(rightPath, "Target node path", out validationError))
            throw new Exception(validationError);

        var left = FindNode(leftPath);
        var right = FindNode(rightPath);
        if (left is null || right is null)
            return;

        if (IsHierarchyConnection(left.FolderPath, right.FolderPath))
            throw new Exception("Hierarchy connections cannot be disconnected."); //TODO сделать переподключение

        DeleteLinkIfExists(Path.Combine(GetNodePath(left), GetLinkName(right.LocalId)));
        DeleteLinkIfExists(Path.Combine(GetNodePath(right), GetLinkName(left.LocalId)));

        left.InvalidateGraphCache();
        right.InvalidateGraphCache();
    }

    public async IAsyncEnumerable<NodeState> EnumerateNodesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
        if (!root.Exists)
            yield break;

        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);

        while (stack.Count > 0) {
            cancellationToken.ThrowIfCancellationRequested();
            cancellationTokens.Token.ThrowIfCancellationRequested();

            var current = stack.Pop();
            foreach (var directory in current.EnumerateDirectories()) {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                var relativePath = Path.GetRelativePath(root.FullName, directory.FullName);
                if (!string.IsNullOrWhiteSpace(relativePath) && relativePath != ".")
                    yield return new NodeFileSystem(new NodeLocalId(NormalizeNodeName(relativePath)), this);

                stack.Push(directory);
            }

            await Task.Yield();
        }
    }

    public IAsyncEnumerable<NodeState> GetCommonIntersection(NodeRef first, NodeRef second, params NodeRef[] other) {
        //TODO реализовать возврат общих узлов
        throw new NotImplementedException();
    }

    internal NodeFileSystem? GetInternal(NodeFileSystem? parent, NodeLocalId nodeId) {
        var path = parent == null
            ? GetNodePath(nodeId.ToString())
            : Path.Combine(parent.FolderPath, nodeId.ToString());
        if (!Directory.Exists(path))
            return null;
        return parent is null
            ? new NodeFileSystem(nodeId, this)
            : new NodeFileSystem(nodeId, parent);
    }

    private NodeFileSystem? FindNode(NodeRef nodeRef) => nodeRef switch {
        NodeRef.NodePath path => FindNode(path),
        NodeRef.InternalId id => FindNode((NodeRef.NodePath)id),//TODO пересмотреть поиск папки
        _ => throw new Exception(),
    };

    private static bool TryValidateNodeRef(NodeRef nodeRef, string subject, out string error) =>
        nodeRef switch {
            NodeRef.NodePath path => TryValidatePath(path, subject, out error),
            NodeRef.InternalId id => TryValidatePath((NodeRef.NodePath)id, subject, out error),
            _ => throw new ArgumentOutOfRangeException(nameof(nodeRef), nodeRef, "Unsupported node reference.")
        };

    private NodeFileSystem? FindNode(NodeRef.NodePath path) {
        if (!TryValidatePath(path, "Node path", out var validationError))
            return null;
        if (Root.Equals(new NodeRef.InternalId(path)))
            return new NodeFileSystem(root, this);
        NodeFileSystem? node = null;
        foreach (var part in path) {
            if (GetInternal(node, part) is not NodeFileSystem child)
                return null;
            node = child;
        }
        return node;
    }

    private static bool TryValidatePath(NodeRef.NodePath path, string subject, out string error) {
        foreach (var segment in path)
            if (!NodeNameValidator.TryValidateSegment(segment, $"{subject} segment", out error))
                return false;
        error = string.Empty;
        return true;
    }

    private bool ConnectNodes(NodeFileSystem left, NodeFileSystem right) {
        if (left.LocalId == right.LocalId)
            return false;
        if (IsHierarchyConnection(left.FolderPath, right.FolderPath))
            return false;
        var sourcePath = GetNodePath(left);
        var targetPath = GetNodePath(right);
        CreateLinkIfMissing(sourcePath, targetPath, right.LocalId);
        CreateLinkIfMissing(targetPath, sourcePath, left.LocalId);
        return true;
    }

    private IReadOnlyCollection<NodeState> GetConnectedNodes(NodeState node) {
        return node.Edges
            .Select(edge => edge.Node1.GlobalId == node.GlobalId ? edge.Node2 : edge.Node1)
            .Where(neighbor => neighbor.GlobalId != node.GlobalId)
            .GroupBy(static neighbor => neighbor.GlobalId)
            .Select(static group => group.First())
            .ToArray();
    }

    private string GetNodePath(string name) {
        return Path.Combine(root.FullName, name);
    }

    private string GetNodePath(NodeFileSystem node) {
        return node.FolderPath;
    }

    private string GetNodePath(NodeState node) {
        return node is NodeFileSystem fileSystemState
            ? fileSystemState.FolderPath
            : GetNodePath(node.LocalId);
    }

    internal static string NormalizeNodeName(string nodeName) {
        return nodeName.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/').Trim('/');
    }

    internal static string GetLinkName(string nodeName) {
        return NormalizeNodeName(nodeName).Split('/').Last();
    }

    private static bool IsHierarchyConnection(string left, string right) {
        var leftPath = NormalizeDirectoryPath(left);
        var rightPath = NormalizeDirectoryPath(right);
        return IsDescendantPath(leftPath, rightPath) || IsDescendantPath(rightPath, leftPath);
    }

    private static bool IsDescendantPath(string candidate, string ancestor) =>
        candidate.Length > ancestor.Length &&
        candidate.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDirectoryPath(string path) {
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath + Path.DirectorySeparatorChar;
    }

    private void CreateLinkIfMissing(string sourcePath, string targetPath, string targetNodeName) {
        var linkPath = Path.Combine(sourcePath, GetLinkName(targetNodeName));
        try {
            if (FileSystemEntryExists(linkPath)) {
                return;
            }

            var targetFullPath = Path.GetFullPath(targetPath);
            Directory.CreateSymbolicLink(linkPath, targetFullPath);
        } catch (Exception ex) {
            logger.LogError(ex, "Failed to create link from {Source} to {Target}", sourcePath, targetPath);
            throw;
        }
    }

    private static bool FileSystemEntryExists(string path) {
        try {
            _ = File.GetAttributes(path);
            return true;
        } catch (FileNotFoundException) {
            return false;
        } catch (DirectoryNotFoundException) {
            return false;
        }
    }

    private static void DeleteLinkIfExists(string path) {
        if (!FileSystemEntryExists(path))
            return;

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) == 0)
            return;

        if ((attributes & FileAttributes.Directory) != 0)
            Directory.Delete(path);
        else
            File.Delete(path);
    }

    private static void DeleteDirectoryWithoutFollowingLinks(DirectoryInfo directory) {
        if (!directory.Exists)
            return;

        foreach (var entry in directory.EnumerateFileSystemInfos()) {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) {
                entry.Delete();
                continue;
            }

            if (entry is DirectoryInfo childDirectory)
                DeleteDirectoryWithoutFollowingLinks(childDirectory);
            else
                entry.Delete();
        }

        directory.Delete();
    }
}
