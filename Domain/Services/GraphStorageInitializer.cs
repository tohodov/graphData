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
        var runtimeTypes = service.RuntimeTypesInitializer;

        runtimeTypes.Nodes.Clear();
        runtimeTypes.Nodes.Add(new Node(new NodeLocalId(RuntimeTypesVersion)));
        runtimeTypes.Nodes.Add(new Node(new NodeLocalId(schemaRegistry.Fingerprint)));

        var result = await service.AddSubgraph(service.Root).ConfigureAwait(false);
        if (result.Status != ServiceResultStatus.Ok)
            throw new Exception(result.Error);
    }
}
