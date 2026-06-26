using Abstractions;
using GraphData.Core.Services;

namespace Domain.Services;

public class GraphStorageInitializer {
    const string RuntimeTypesVersion = "6";
    readonly GraphService service;
    readonly GraphSchemaRegistry schemaRegistry;

    public GraphStorageInitializer(GraphService service, GraphSchemaRegistry schemaRegistry) {
        this.service = service;
        this.schemaRegistry = schemaRegistry;
    }

    public async Task InitializeAsync() {
        var graphData = GetOrAdd(service.Root, "graphdata");
        var storage = GetOrAdd(graphData, "storage");
        var initializers = GetOrAdd(storage, "initializers");
        var runtimeTypes = GetOrAdd(initializers, "runtime-types");//TODO сделать константой времени компиляции как service.TypesRoot

        runtimeTypes.Nodes.Clear();
        runtimeTypes.Nodes.Add(new Node(new NodeLocalId(RuntimeTypesVersion)));
        runtimeTypes.Nodes.Add(new Node(new NodeLocalId(schemaRegistry.Fingerprint)));

        var result = await service.AddSubgraph(service.Root).ConfigureAwait(false);
        if (result.Status != ServiceResultStatus.Ok)
            throw new Exception(result.Error);
    }

    private static Node GetOrAdd(Node parent, NodeLocalId localId) {
        var node = parent.Nodes.FirstOrDefault(node => node.LocalId == localId);
        if (node is not null)
            return node;
        node = new Node(localId);
        parent.Nodes.Add(node);
        return node;
    }

}
