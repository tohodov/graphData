using System;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

public sealed class NodeService(IGraphStorage storage)
{
    private readonly IGraphStorage _storage = storage;

    public async Task<Node?> Get(string name)
    {
        NodeNameValidator.Validate(name, nameof(name));

        var node = await _storage.Get(name);
        if (node is null)
        {
            return null;
        }

        return node;
    }

    public async Task<(Node Node, IReadOnlyCollection<Node> Connections)?> GetNeighborhood(string name)
    {
        NodeNameValidator.Validate(name, nameof(name));

        var node = await _storage.Get(name);
        if (node is null)
        {
            return null;
        }

        var connections = await _storage.GetConnectedNodesAsync(node);
        return (node, connections);
    }

    public async Task<Node> Create(Node? parent, string name, Dictionary<string, string>? attributes = null)
    {
        NodeNameValidator.Validate(name, nameof(name));
        return await _storage.Create(name, parent, attributes);
    }

    public Task Update(string name, IDictionary<string, string> attributes)
    {
        NodeNameValidator.Validate(name, nameof(name));
        return _storage.Update(name, attributes);
    }

    public Task Delete(string name)
    {
        NodeNameValidator.Validate(name, nameof(name));
        return _storage.Delete(name);
    }

    public Task ConnectNodes(Node first, Node second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        NodeNameValidator.Validate(first.Name, nameof(first));
        NodeNameValidator.Validate(second.Name, nameof(second));

        if(first == second)
            throw new ArgumentException("Node id must be provided.", nameof(second));

        return _storage.Connect(first, second);
    }

    public Task<Subgraph> GetSubgraph(SubgraphQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.RootNodeIds.Count == 0)
        {
            return Task.FromResult(Subgraph.Empty);
        }

        foreach (var rootNodeId in query.RootNodeIds)
        {
            NodeNameValidator.Validate(rootNodeId, nameof(query.RootNodeIds));
        }

        if (query.MaxDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query.MaxDepth), "Depth must be non-negative.");
        }

        return _storage.GetSubgraphAsync(query);
    }
}
