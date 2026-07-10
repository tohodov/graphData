using System.Text.Json;
using Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using InternalId = Abstractions.NodeRef.InternalId;

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
    Task Delete(NodeFileSystem node) {
        if (IsStorageRoot(node.FolderPath))
            throw new InvalidOperationException("Storage root cannot be deleted.");

        var externalBacklinks = CollectLinksTargetingSubtree(node.FolderPath)
            .Where(link => !IsSameOrDescendantPath(link.LinkPath, node.FolderPath))
            .ToArray();
        foreach (var backlink in externalBacklinks)
            DeleteLinkIfExists(backlink.LinkPath);

        DeleteDirectoryWithoutFollowingLinks(node.GetInfo());
        return Task.CompletedTask;
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
        ConnectNodes(left, right);
        return Task.CompletedTask;
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

        DeleteLinksToTarget(GetNodePath(left), right.FolderPath);
        DeleteLinksToTarget(GetNodePath(right), left.FolderPath);
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

        var possibleParents = (await node.Nodes
                .OfType<NodeFileSystem>()
                .Where(neighbor => !IsHierarchyConnection(node.FolderPath, neighbor.FolderPath))
                .DistinctBy(neighbor => neighbor.GlobalId)
                .OrderBy(neighbor => neighbor.GlobalId.ToString(), StringComparer.Ordinal)
                .Take(2)
                .ToArrayAsync())
            .ToArray();
        if (possibleParents.Length == 0)
            throw new InvalidOperationException($"Node '{node.GlobalId}' has no non-hierarchy connections to move through.");
        if (possibleParents.Length > 1)
            throw new InvalidOperationException(
                $"Node '{node.GlobalId}' has more than one non-hierarchy connection; relocation is ambiguous.");
        var newParent = possibleParents[0];

        var sourcePath = NormalizeDirectoryPath(node.FolderPath);
        var destinationPath = NormalizeDirectoryPath(Path.Combine(newParent.FolderPath, node.LocalId.ToString()));

        var nodeToParentLinks = FindLinksToTarget(node.FolderPath, newParent.FolderPath);
        var parentToNodeLinks = FindLinksToTarget(newParent.FolderPath, node.FolderPath);
        if (FileSystemEntryExists(destinationPath)
            && !parentToNodeLinks.Any(linkPath => PathsEqual(linkPath, destinationPath)))
            throw new InvalidOperationException($"Destination node path '{destinationPath}' already exists.");

        var selectedConnectionLinks = nodeToParentLinks
            .Concat(parentToNodeLinks)
            .ToArray();
        var linksToRewrite = CollectLinksTargetingSubtree(sourcePath)
            .Where(link => !selectedConnectionLinks.Any(selected => PathsEqual(selected, link.LinkPath)))
            .ToArray();
        if (linksToRewrite.Any(link => BecomesHierarchyConnectionAfterMove(link, sourcePath, destinationPath)))
            throw new InvalidOperationException(
                $"Moving node '{node.GlobalId}' under '{newParent.GlobalId}' would turn an existing graph edge into an ancestor-descendant connection.");

        foreach (var linkPath in selectedConnectionLinks)
            DeleteLinkIfExists(linkPath);

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

    private void ConnectNodes(NodeFileSystem left, NodeFileSystem right) {
        if (left.GlobalId == right.GlobalId)
            return;
        if (IsHierarchyConnection(left.FolderPath, right.FolderPath))
            throw new InvalidOperationException("Nodes already have a hierarchy connection.");
        var sourcePath = GetNodePath(left);
        var targetPath = GetNodePath(right);
        var sourceLinkPath = GetLinkPath(sourcePath, right.LocalId);
        var targetLinkPath = GetLinkPath(targetPath, left.LocalId);
        EnsureLinkSlotIsAvailable(sourceLinkPath, targetPath, left.GlobalId, right.GlobalId);
        EnsureLinkSlotIsAvailable(targetLinkPath, sourcePath, left.GlobalId, right.GlobalId);

        Directory.CreateSymbolicLink(sourceLinkPath, Path.GetFullPath(targetPath));
        try {
            Directory.CreateSymbolicLink(targetLinkPath, Path.GetFullPath(sourcePath));
        } catch {
            DeleteLinkIfExists(sourceLinkPath);
            throw;
        }
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
        return Path.GetFullPath(Path.Combine(newRoot, relative));
    }

    private static bool BecomesHierarchyConnectionAfterMove(LinkRewrite link, string oldRoot, string newRoot) {
        var linkOwnerPath = Path.GetDirectoryName(link.LinkPath);
        if (linkOwnerPath is null)
            return false;

        var rewrittenOwner = RewritePathIfInsideSubtree(linkOwnerPath, oldRoot, newRoot);
        var rewrittenTarget = RewritePathIfInsideSubtree(link.TargetPath, oldRoot, newRoot);
        return IsHierarchyConnection(rewrittenOwner, rewrittenTarget);
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

    private static string GetLinkPath(string sourcePath, NodeLocalId targetNodeName) {
        return Path.Combine(sourcePath, GetLinkName(targetNodeName.ToString()));
    }

    private static void EnsureLinkSlotIsAvailable(
        string linkPath,
        string expectedTargetPath,
        InternalId left,
        InternalId right) {
        if (!FileSystemEntryExists(linkPath))
            return;

        var existingTarget = NodeFileSystem.GetResolvedLinkTarget(new DirectoryInfo(linkPath));
        if (existingTarget is not null && PathsEqual(existingTarget, expectedTargetPath))
            throw new InvalidOperationException($"Nodes '{left}' and '{right}' are already connected.");

        throw new InvalidOperationException(
            $"Connection namespace entry '{linkPath}' is already occupied by another node while connecting '{left}' and '{right}' to '{expectedTargetPath}'.");
    }

    private static void DeleteLinksToTarget(string sourcePath, string targetPath) {
        foreach (var linkPath in FindLinksToTarget(sourcePath, targetPath))
            DeleteLinkIfExists(linkPath);
    }

    private static IReadOnlyCollection<string> FindLinksToTarget(string sourcePath, string targetPath) {
        if (!Directory.Exists(sourcePath))
            return Array.Empty<string>();

        var targetFullPath = Path.GetFullPath(targetPath);
        var result = new List<string>();
        foreach (var entry in new DirectoryInfo(sourcePath).EnumerateFileSystemInfos().ToArray()) {
            if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
                continue;
            var existingTarget = NodeFileSystem.GetResolvedLinkTarget(entry);
            if (existingTarget is not null && PathsEqual(existingTarget, targetFullPath))
                result.Add(entry.FullName);
        }
        return result;
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
