using Abstractions;

namespace GraphData.Core.Services;

public sealed class GraphProvider {
    readonly IGraphStorage storage;
    readonly GraphSchemaRegistry schemaRegistry;
    readonly SemaphoreSlim gate = new(1, 1);
    Graph? graph;

    internal GraphProvider(IGraphStorage storage, GraphSchemaRegistry schemaRegistry) {
        this.storage = storage;
        this.schemaRegistry = schemaRegistry;
    }

    public async ValueTask<Graph> GetGraphAsync() {
        if (graph is not null)
            return graph;

        await gate.WaitAsync().ConfigureAwait(false);
        try {
            graph ??= await Graph.OpenAsync(storage, schemaRegistry).ConfigureAwait(false);
            return graph;
        } finally {
            gate.Release();
        }
    }
}
