using Abstractions;

namespace GraphData.Core.Services;

public sealed class GraphFactory {
    readonly IGraphStorage storage;
    readonly GraphSchemaRegistry schemaRegistry;

    internal GraphFactory(IGraphStorage storage, GraphSchemaRegistry schemaRegistry) {
        this.storage = storage;
        this.schemaRegistry = schemaRegistry;
    }

    public async Task<Graph> OpenAsync() {
        var nodeTypes = await EnsureNodeTypesRootAsync().ConfigureAwait(false);
        foreach (var type in schemaRegistry.Types)
            await EnsureNodeTypeAsync(type.Id).ConfigureAwait(false);

        return new Graph(storage.Root, nodeTypes);
    }

    async Task<NodeBacking> EnsureNodeTypesRootAsync() {
        return await storage.Get(FixedGraphTopology.NodeTypesPath).ConfigureAwait(false)
            ?? await storage.Create(FixedGraphTopology.NodeTypesLocalId).ConfigureAwait(false);
    }

    async Task EnsureNodeTypeAsync(NodeLocalId localId) {
        if (await storage.Get(FixedGraphTopology.NodeTypePath(localId)).ConfigureAwait(false) is not null)
            return;

        await storage.Create(localId, FixedGraphTopology.NodeTypesId).ConfigureAwait(false);
    }
}
