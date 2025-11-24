using System;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;

namespace GraphData.Core.Services;

public sealed class NodeService(IGraphStorage storage)
{
    private readonly IGraphStorage _storage = storage;

    public async Task<Node?> Get(string name)
    {
        var node = await _storage.Get(name);
        if (node is null)
        {
            return null;
        }
        var connections = await _storage.GetConnectedNodesAsync(node);
        return node;
    }

    public async Task<Node> Create(Node? parent, string name)
    {
        return await _storage.Create(name, parent);
    }

    public Task Update(Node node)
    {
        return _storage.Update(node.Name, node.Attributes.ToDictionary());
    }

    public Task ConnectNodes(Node first, Node second)
    {
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

        if (query.MaxDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query.MaxDepth), "Depth must be non-negative.");
        }

        return _storage.GetSubgraphAsync(query);
    }
}
