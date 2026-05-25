using System.Text.Json;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SymLinkStorage;

namespace GraphData.SymLinkStorage;

public sealed class SymLinkGraphStorage : IGraphStorage, IGraphNodeCatalog {
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    readonly DirectoryInfo root;
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
    }

    public Task<ServiceResult<Node>> Create(string name, NodePath? parent = null, IDictionary<string, string>? attributes = null) {
        if (!NodeNameValidator.TryValidateSegment(name, "Node name", out var validationError))
            return Task.FromResult(ServiceResult<Node>.BadRequest(validationError));

        var parentNode = parent is { Count: > 0 }
            ? FindNode(parent)
            : null;
        if (parent is { Count: > 0 } && parentNode is null)
            return Task.FromResult(ServiceResult<Node>.NotFound($"Parent node '{parent}' was not found."));

        var node = parentNode is null
            ? CreateRootNode(name, attributes)
            : NodeFileSystem.Create(name, parentNode, attributes);

        return Task.FromResult(ServiceResult<Node>.Ok(node));
    }

    public Task<ServiceResult<Node>> Get(NodePath path) {
        var node = FindNode(path);
        return Task.FromResult(node is null
            ? ServiceResult<Node>.NotFound()
            : ServiceResult<Node>.Ok(node));
    }

    public Task<ServiceResult> Delete(NodePath path) {
        var node = FindNode(path);
        if (node is null)
            return Task.FromResult(ServiceResult.NotFound());

        var connections = GetConnectedNodes(node);
        foreach (var connection in connections)
            DeleteLinkIfExists(GetLinkPath(connection.LocalId, node.LocalId));
        DeleteDirectoryWithoutFollowingLinks(node.GetInfo());

        return Task.FromResult(ServiceResult.Ok());
    }

    public Task<ServiceResult> Connect(NodePath leftPath, NodePath rightPath) {
        if (leftPath.SequenceEqual(rightPath))
            return Task.FromResult(ServiceResult.BadRequest("SourcePath and TargetPath must be different."));

        var left = FindNode(leftPath);
        var right = FindNode(rightPath);
        if (left is null || right is null)
            return Task.FromResult(ServiceResult.NotFound());

        try {
            ConnectNodes(left, right);
        } catch (Exception ex) {
            return Task.FromResult(ServiceResult.InternalServerError(ex.ToString()));
        }

        return Task.FromResult(ServiceResult.Ok());
    }

    public Task<ServiceResult> Disconnect(NodePath leftPath, NodePath rightPath) {
        return Task.FromResult(ServiceResult.InternalServerError(new NotImplementedException().ToString()));
    }

    public Task<ServiceResult<IReadOnlyCollection<Node>>> GetConnectedNodesAsync(Node node) {
        var nodePath = GetNodePath(node.LocalId);
        if (!Directory.Exists(nodePath))
            return Task.FromResult(ServiceResult<IReadOnlyCollection<Node>>.NotFound());

        return Task.FromResult(ServiceResult<IReadOnlyCollection<Node>>.Ok(GetConnectedNodes(node)));
    }

    public Task<IReadOnlyCollection<Node>> GetAllNodesAsync() {
        if (!root.Exists)
            return Task.FromResult<IReadOnlyCollection<Node>>(Array.Empty<Node>());

        var nodes = new List<Node>();
        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);

        while (stack.Count > 0) {
            cancellationTokens.Token.ThrowIfCancellationRequested();

            var current = stack.Pop();
            foreach (var directory in current.EnumerateDirectories()) {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                var relativePath = Path.GetRelativePath(root.FullName, directory.FullName);
                if (!string.IsNullOrWhiteSpace(relativePath) && relativePath != ".")
                    nodes.Add(new NodeFileSystem(NormalizeNodeName(relativePath), options.RootPath));

                stack.Push(directory);
            }
        }

        return Task.FromResult<IReadOnlyCollection<Node>>(nodes);
    }

    internal NodeFileSystem? GetInternal(NodeFileSystem? parent, string nodeId) {
        var path = parent == null
            ? GetNodePath(nodeId)
            : Path.Combine(parent.FolderPath, nodeId);
        if (!Directory.Exists(path))
            return null;
        return new NodeFileSystem(nodeId, parent == null ? options.RootPath : parent.FolderPath);
    }

    private NodeFileSystem CreateRootNode(string name, IDictionary<string, string>? attributes) {
        var node = new NodeFileSystem(name, options.RootPath);
        Directory.CreateDirectory(node.FolderPath);
        if (attributes != null)
            node.WriteMetadata(attributes);
        return node;
    }

    private NodeFileSystem? FindNode(NodePath path) {
        NodeFileSystem? node = null;
        foreach (var part in path) {
            if (GetInternal(node, part) is not NodeFileSystem child)
                return null;
            node = child;
        }
        return node;
    }

    private void ConnectNodes(Node left, Node right) {
        if (string.Equals(left.LocalId, right.LocalId, StringComparison.OrdinalIgnoreCase))
            return;

        if (IsHierarchyConnection(left.LocalId, right.LocalId))
            return;

        var sourcePath = GetNodePath(left.LocalId);
        var targetPath = GetNodePath(right.LocalId);
        CreateLinkIfMissing(sourcePath, targetPath, right.LocalId);
        CreateLinkIfMissing(targetPath, sourcePath, left.LocalId);
    }

    private IReadOnlyCollection<Node> GetConnectedNodes(Node node) {
        var nodePath = GetNodePath(node.LocalId);
        return EnumerateNeighborIds(nodePath)
            .Where(neighborId => !string.Equals(neighborId, node.LocalId, StringComparison.OrdinalIgnoreCase))
            .Select(neighborId => (Node)new NodeFileSystem(neighborId, options.RootPath))
            .ToArray();
    }

    private string GetNodePath(string name) {
        return Path.Combine(options.RootPath, name);
    }

    private string GetLinkPath(string sourceNodeName, string targetNodeName) {
        return Path.Combine(GetNodePath(sourceNodeName), GetLinkName(targetNodeName));
    }

    internal static string NormalizeNodeName(string nodeName) {
        return nodeName.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/').Trim('/');
    }

    internal static string GetLinkName(string nodeName) {
        return NormalizeNodeName(nodeName).Split('/').Last();
    }

    private static bool IsHierarchyConnection(string leftName, string rightName) {
        var left = NormalizeNodeName(leftName);
        var right = NormalizeNodeName(rightName);

        return right.StartsWith(left + '/', StringComparison.OrdinalIgnoreCase) ||
            left.StartsWith(right + '/', StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<string> EnumerateNeighborIds(string nodePath) {
        foreach (var entry in new DirectoryInfo(nodePath).EnumerateFileSystemInfos()) {
            if (string.Equals(entry.Name, "node.json", StringComparison.OrdinalIgnoreCase))
                continue;
            if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
                continue;
            yield return entry.Name;
        }
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
