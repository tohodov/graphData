using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;
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
        this.options = options.Value ?? throw new ArgumentNullException(nameof(options));
        this.cancellationTokens = cancellationTokens;
        this.logger = logger;
        root = new DirectoryInfo(Path.GetFullPath(this.options.RootPath));
        if (!root.Exists)
            root.Create();
    }

    public async Task<Node> Create(string name, Node? parent = null, Dictionary<string, string>? attributes = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var parentNodeInternal = parent == null ? null : parent as NodeFileSystem ?? await Get(parent.Name) as NodeFileSystem;
        if (parentNodeInternal != null)
            return NodeFileSystem.Create(name, parentNodeInternal, attributes);
        return NodeFileSystem.Create(name, options.RootPath, attributes);
    }

    public Task<Node?> Get(string name) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var nodePath = GetNodePath(name);
        if (!Directory.Exists(nodePath))
            return Task.FromResult<Node?>(null);
        return Task.FromResult<Node?>(new NodeFileSystem(name, options.RootPath));
    }
    public Task<Node?> Get(NodeQuery query) {
        NodeFileSystem? node = null;
        foreach (var part in query.SelectRecursive(x => x.Child))
            if (GetInternal(node, part.Name) is NodeFileSystem child)
                node = child;
            else
                break;
        return Task.FromResult<Node?>(node);
    }
    public Task<Node?> Get(Node? parent, string nodeId) => Task.FromResult<Node?>(GetInternal(parent as NodeFileSystem, nodeId));
    internal NodeFileSystem? GetInternal(NodeFileSystem? parent, string nodeId) {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        var path = parent == null
            ? GetNodePath(nodeId)
            : NodeNamePathCodec.CombinePath(parent.Path, nodeId);

        if (!Directory.Exists(path))
            return null;

        return new NodeFileSystem(nodeId, parent == null ? options.RootPath : parent.Path);
    }

    public Task Update(string name, IDictionary<string, string> attributes, Node? parent = null) {
        var node = GetInternal(parent as NodeFileSystem, name);
        if (node == null)
            throw new FileNotFoundException($"Node '{name}' not found.");
        node.WriteMetadata(attributes);
        return Task.CompletedTask;
    }

    public async Task Delete(string name, Node? parent = null) {
        var node = GetInternal(parent as NodeFileSystem, name);
        if (node == null)
            return;

        var connections = await GetConnectedNodesAsync(node);
        foreach (var connection in connections) {
            var reciprocalLinkPath = Path.Combine(GetNodePath(connection.Name), NodeNamePathCodec.EncodeFileName(node.Name));
            DeleteLinkIfExists(reciprocalLinkPath);
        }

        DeleteDirectoryWithoutFollowingLinks(node.GetInfo());
    }

    public Task Connect(Node left, Node right) {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)) {
            return Task.CompletedTask;
        }

        var sourcePath = GetNodePath(left.Name);
        var targetPath = GetNodePath(right.Name);
        CreateLinkIfMissing(sourcePath, targetPath, right.Name);
        CreateLinkIfMissing(targetPath, sourcePath, left.Name);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<Node>> GetConnectedNodesAsync(Node node) {
        var nodePath = GetNodePath(node.Name);
        if (!Directory.Exists(nodePath))
            throw new DirectoryNotFoundException(nodePath);

        var connected = EnumerateNeighborIds(nodePath)
            .Where(neighborId => !string.Equals(neighborId, node.Name, StringComparison.OrdinalIgnoreCase))
            .Select(neighborId => (Node)new NodeFileSystem(neighborId, options.RootPath))
            .ToArray();

        return Task.FromResult<IReadOnlyCollection<Node>>(connected);
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
                    nodes.Add(new NodeFileSystem(NormalizeNodeName(NodeNamePathCodec.DecodePath(relativePath)), options.RootPath));

                stack.Push(directory);
            }
        }

        return Task.FromResult<IReadOnlyCollection<Node>>(nodes);
    }

    public async Task<Subgraph> GetSubgraphAsync(SubgraphQuery query) {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var visited = new HashSet<string>(comparer);
        var discovered = new HashSet<string>(query.RootNodeIds, comparer);
        var queue = new Queue<(string NodeId, int Depth)>();

        foreach (var root in query.RootNodeIds) {
            queue.Enqueue((root, 0));
        }

        var nodes = new Dictionary<string, Node>(comparer);

        while (queue.Count > 0) {
            cancellationTokens.Token.ThrowIfCancellationRequested();

            var (nodeId, depth) = queue.Dequeue();
            if (!visited.Add(nodeId)) {
                continue;
            }

            var nodePath = GetNodePath(nodeId);
            if (!Directory.Exists(nodePath)) {
                continue;
            }

            nodes[nodeId] = new NodeFileSystem(nodeId, options.RootPath);

            if (depth >= query.MaxDepth) {
                continue;
            }

            foreach (var neighborId in EnumerateNeighborIds(nodePath)) {
                if (!string.Equals(neighborId, nodeId, StringComparison.OrdinalIgnoreCase) && discovered.Add(neighborId)) {
                    queue.Enqueue((neighborId, depth + 1));
                }
            }
        }

        return nodes.Count == 0
            ? Subgraph.Empty
            : new Subgraph {
                Nodes = nodes.Values
            };
    }

    private string GetNodePath(string name) {
        return NodeNamePathCodec.CombinePath(options.RootPath, name);
    }

    private static string NormalizeNodeName(string nodeName) {
        return nodeName.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/').Trim('/');
    }

    private static IEnumerable<string> EnumerateNeighborIds(string nodePath) {
        var directory = new DirectoryInfo(nodePath);

        foreach (var entry in directory.EnumerateFileSystemInfos()) {
            if (string.Equals(entry.Name, "node.json", StringComparison.OrdinalIgnoreCase))
                continue;

            if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
                continue;

            yield return NodeNamePathCodec.DecodeFileName(entry.Name);
        }
    }

    private void CreateLinkIfMissing(string sourcePath, string targetPath, string name) {
        var linkPath = Path.Combine(sourcePath, NodeNamePathCodec.EncodeFileName(name));
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
