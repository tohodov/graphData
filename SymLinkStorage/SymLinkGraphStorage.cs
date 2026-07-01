using System.Text.Json;
using Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Storage;

internal sealed class SymLinkGraphStorage : IGraphStorage {
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public NodeBacking Root { get; }

    internal readonly DirectoryInfo root;
    readonly NtfsGraphStorageOptions options;
    readonly ICancellationTokenAccessor cancellationTokens;

    public SymLinkGraphStorage(IOptions<NtfsGraphStorageOptions> options, ICancellationTokenAccessor cancellationTokens) {
        this.options = options.Value;
        this.cancellationTokens = cancellationTokens;
        root = new DirectoryInfo(Path.GetFullPath(this.options.RootPath));
        if (!root.Exists)
            root.Create();
        Root = new NodeFileSystem(new(), root, this);
    }

    public Task<NodeBacking> Create(NodeLocalId name, NodeRef? path = null, IDictionary<string, string>? attributes = null) {
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

        return Task.FromResult<NodeBacking>(node);
    }

    public Task<NodeBacking?> Get(NodeRef path) {
        return Task.FromResult<NodeBacking?>(FindNode(path));
    }

    public Task Delete(NodeBacking node) => Delete((NodeFileSystem)node);
    async Task Delete(NodeFileSystem node) {
        var connections = await node.Edges
            .Select(edge => edge.Node1.GlobalId == node.GlobalId ? edge.Node2 : edge.Node1)
            .Where(neighbor => neighbor.GlobalId != node.GlobalId)
            .GroupBy(static neighbor => neighbor.GlobalId)
            .Select(static group => group.First())
            .ToArrayAsync();
        foreach (var connection in connections)
            DeleteLinkIfExists(Path.Combine(GetNodePath(connection), GetLinkName(node.LocalId)));
        DeleteDirectoryWithoutFollowingLinks(node.GetInfo());
    }
    public Task Delete(NodeRef path) {
        var node = FindNode(path);
        if (node is null)
            return Task.FromResult(false);
        return Delete(node);
    }

    public Task Connect(NodeRef leftPath, NodeRef rightPath) {
        if (leftPath.Equals(rightPath))
            return Task.CompletedTask;
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
            return;
        if (!TryValidateNodeRef(leftPath, "Source node path", out var validationError))
            throw new Exception(validationError);
        if (!TryValidateNodeRef(rightPath, "Target node path", out validationError))
            throw new Exception(validationError);

        var left = FindNode(leftPath);
        var right = FindNode(rightPath);
        if (left is null || right is null)
            return;

        var hierarchyChild = GetDirectHierarchyChild(left, right);
        if (hierarchyChild is not null) {
            await MoveNodeToConnectedNode(hierarchyChild.GlobalId);
            return;
        }

        if (IsHierarchyConnection(left.FolderPath, right.FolderPath))
            throw new InvalidOperationException("Only direct hierarchy connections can be disconnected.");

        DeleteLinkIfExists(Path.Combine(GetNodePath(left), GetLinkName(right.LocalId)));
        DeleteLinkIfExists(Path.Combine(GetNodePath(right), GetLinkName(left.LocalId)));
    }

    public async IAsyncEnumerable<NodeBacking> EnumerateNodesAsync(
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

    public async IAsyncEnumerable<NodeBacking> GetCommonIntersection(NodeRef first, NodeRef second, params NodeRef[] other) {
        var roots = new List<NodeBacking>();
        foreach (var path in new[] { first, second }.Concat(other))
            if (await Get(path) is NodeBacking node)
                roots.Add(node);
            else
                throw new Exception(path.ToString());
        var selected = roots.First();
        roots.Remove(selected);
        await foreach (var candidate in selected.Nodes)
            if ((await Task.WhenAll(roots.Select(async x => await x.Nodes.Contains(candidate)))).All(x => x))
                yield return candidate;
    }

    public async Task<NodeBacking> MoveNodeToConnectedNode(NodeRef nodeRef) {
        var node = FindNode(nodeRef) ?? throw new InvalidOperationException($"Node '{nodeRef}' was not found.");
        if (IsStorageRoot(node.FolderPath))
            throw new InvalidOperationException("Storage root cannot be moved.");

        var newParent = (await node.Nodes
                .OfType<NodeFileSystem>()
                .Where(neighbor => !IsHierarchyConnection(node.FolderPath, neighbor.FolderPath))
                .DistinctBy(neighbor => neighbor.GlobalId)
                .OrderBy(neighbor => neighbor.GlobalId.ToString(), StringComparer.Ordinal)
                .ToArrayAsync())
            .FirstOrDefault();
        if (newParent is null)
            throw new InvalidOperationException($"Node '{node.GlobalId}' has no non-hierarchy connections to move through.");

        var sourcePath = NormalizeDirectoryPath(node.FolderPath);
        var destinationPath = NormalizeDirectoryPath(Path.Combine(newParent.FolderPath, node.LocalId.ToString()));

        var selectedParentBackLink = Path.Combine(newParent.FolderPath, GetLinkName(node.LocalId));
        DeleteLinkIfExists(Path.Combine(node.FolderPath, GetLinkName(newParent.LocalId)));
        DeleteLinkIfExists(selectedParentBackLink);

        if (FileSystemEntryExists(destinationPath))
            throw new InvalidOperationException($"Destination node path '{destinationPath}' already exists.");

        var linksToRewrite = CollectLinksTargetingSubtree(sourcePath)
            .Where(link => !PathsEqual(link.LinkPath, selectedParentBackLink))
            .ToArray();

        foreach (var link in linksToRewrite)
            DeleteLinkIfExists(link.LinkPath);

        Directory.Move(node.FolderPath, destinationPath);

        foreach (var link in linksToRewrite)
            Directory.CreateSymbolicLink(
                RewritePathIfInsideSubtree(link.LinkPath, sourcePath, destinationPath),
                RewritePathIfInsideSubtree(link.TargetPath, sourcePath, destinationPath));

        return new NodeFileSystem(new DirectoryInfo(destinationPath), this);
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
        if (Root.GlobalId == new NodeRef.InternalId(path))
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
        if (left.GlobalId == right.GlobalId)
            return false;
        if (IsHierarchyConnection(left.FolderPath, right.FolderPath))
            return false;
        var sourcePath = GetNodePath(left);
        var targetPath = GetNodePath(right);
        CreateLinkIfMissing(sourcePath, targetPath, right.LocalId);
        CreateLinkIfMissing(targetPath, sourcePath, left.LocalId);
        return true;
    }

    private string GetNodePath(string name) {
        return Path.Combine(root.FullName, name);
    }

    private string GetNodePath(NodeFileSystem node) {
        return node.FolderPath;
    }

    private string GetNodePath(NodeBacking node) {
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

    private static NodeFileSystem? GetDirectHierarchyChild(NodeFileSystem left, NodeFileSystem right) {
        if (IsDirectChildPath(left.FolderPath, right.FolderPath))
            return left;
        if (IsDirectChildPath(right.FolderPath, left.FolderPath))
            return right;
        return null;
    }

    private static bool IsDirectChildPath(string child, string parent) {
        var childPath = NormalizeDirectoryPath(child);
        var childWithoutTrailingSeparator = childPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parentPath = Path.GetDirectoryName(childWithoutTrailingSeparator);
        return parentPath is not null && PathsEqual(parentPath, parent);
    }

    private static bool IsDescendantPath(string candidate, string ancestor) =>
        candidate.Length > ancestor.Length &&
        candidate.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase);

    private bool IsStorageRoot(string path) => PathsEqual(path, root.FullName);

    private IEnumerable<LinkRewrite> CollectLinksTargetingSubtree(string subtreePath) {
        if (!root.Exists)
            yield break;

        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);

        while (stack.Count > 0) {
            var current = stack.Pop();
            foreach (var entry in current.EnumerateFileSystemInfos()) {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) {
                    var targetPath = NodeFileSystem.GetResolvedLinkTarget(entry);
                    if (targetPath is not null && IsSameOrDescendantPath(targetPath, subtreePath))
                        yield return new LinkRewrite(entry.FullName, targetPath);
                    continue;
                }

                if (entry is DirectoryInfo directory)
                    stack.Push(directory);
            }
        }
    }

    private static string RewritePathIfInsideSubtree(string path, string oldRoot, string newRoot) {
        if (!IsSameOrDescendantPath(path, oldRoot))
            return path;

        var relative = Path.GetRelativePath(oldRoot, path);
        return NodeFileSystem.ResolveDirectoryPath(Path.Combine(newRoot, relative));
    }

    private static bool IsSameOrDescendantPath(string candidate, string ancestor) {
        var normalizedCandidate = NormalizeDirectoryPath(candidate);
        var normalizedAncestor = NormalizeDirectoryPath(ancestor);
        return PathsEqual(normalizedCandidate, normalizedAncestor)
            || IsDescendantPath(normalizedCandidate, normalizedAncestor);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            NormalizeDirectoryPath(left),
            NormalizeDirectoryPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDirectoryPath(string path) {
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath + Path.DirectorySeparatorChar;
    }

    private sealed record LinkRewrite(string LinkPath, string TargetPath);

    private void CreateLinkIfMissing(string sourcePath, string targetPath, string targetNodeName) {
        var linkPath = Path.Combine(sourcePath, GetLinkName(targetNodeName));
        if (FileSystemEntryExists(linkPath))
            return;

        var targetFullPath = Path.GetFullPath(targetPath);
        Directory.CreateSymbolicLink(linkPath, targetFullPath);
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
