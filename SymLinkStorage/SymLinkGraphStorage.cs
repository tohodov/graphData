using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SymLinkStorage;

namespace GraphData.SymLinkStorage;

public sealed class SymLinkGraphStorage : IGraphStorage {
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
        var parentNodeInternal = parent == null ? null : parent as NodeFileSystem ?? await Get(parent.Name) as NodeFileSystem;
        if (parentNodeInternal != null)
            return NodeFileSystem.Create(name, parentNodeInternal, attributes);
        return NodeFileSystem.Create(name, options.RootPath, attributes);
    }

    public Task<Node?> Get(string name) {
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
        DirectoryInfo? info;
        if (parent == null)
            info = root.EnumerateDirectories().Where(x => x.Name == nodeId).FirstOrDefault();
        else
            info = new DirectoryInfo(Path.Combine(parent.Path, nodeId));
        if (info == null || !info.Exists)
            return null;
        return new NodeFileSystem(info);
    }

    public Task Update(string name, IDictionary<string, string> attributes, Node? parent = null) {
        var node = GetInternal(parent as NodeFileSystem, name);
        if (node == null)
            throw new FileNotFoundException($"Node '{name}' not found.");
        node.WriteMetadata(attributes);
        return Task.CompletedTask;
    }

    public Task Connect(Node left, Node right) {
        var sourcePath = GetNodePath(left.Name);
        var targetPath = GetNodePath(right.Name);
        CreateLinkIfMissing(sourcePath, targetPath, right.Name);
        CreateLinkIfMissing(targetPath, sourcePath, left.Name);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyCollection<Node>> GetConnectedNodesAsync(Node node) {
        var internalNode = node as NodeFileSystem ?? await Get(node.Name) as NodeFileSystem;
        if (internalNode == null)
            throw new Exception();
        return internalNode.Nodes;
    }

    public async Task<Subgraph> GetSubgraphAsync(SubgraphQuery query) {
        var visited = new HashSet<string>();
        var discovered = new HashSet<string>(query.RootNodeIds);
        var queue = new Queue<(string NodeId, int Depth)>();

        foreach (var root in query.RootNodeIds) {
            queue.Enqueue((root, 0));
        }

        var nodes = new Dictionary<string, Node>();

        while (queue.Count > 0) {
            cancellationTokens.Token.ThrowIfCancellationRequested();

            var (nodeId, depth) = queue.Dequeue();
            if (!visited.Add(nodeId)) {
                continue;
            }

            var node = await Get(nodeId);
            if (node is null) {
                continue;
            }

            var connections = await GetConnectedNodesAsync(node);
            var relevantConnections = new HashSet<string>();

            foreach (var connection in connections) {
                if (depth < query.MaxDepth && discovered.Add(connection.Name)) {
                    queue.Enqueue((connection.Name, depth + 1));
                }

                if (discovered.Contains(connection.Name)) {
                    relevantConnections.Add(connection.Name);
                }
            }

            nodes[nodeId] = NodeFileSystem.Create(nodeId, options.RootPath);
        }

        return nodes.Count == 0
            ? Subgraph.Empty
            : new Subgraph {
                Nodes = nodes.Values
            };
    }

    private string GetNodePath(string name) {
        return Path.Combine(options.RootPath, name);
    }

    private void CreateLinkIfMissing(string sourcePath, string targetPath, string name) {
        var linkPath = Path.Combine(sourcePath, name);
        try {
            if (Directory.Exists(linkPath) || File.Exists(linkPath)) {
                return;
            }

            var targetFullPath = Path.GetFullPath(targetPath);
            Directory.CreateSymbolicLink(linkPath, targetFullPath);
        } catch (Exception ex) {
            logger.LogError(ex, "Failed to create link from {Source} to {Target}", sourcePath, targetPath);
            throw;
        }
    }
}
