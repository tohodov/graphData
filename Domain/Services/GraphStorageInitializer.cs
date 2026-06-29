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
        if (!service.Root.Attributes.ContainsKey(schemaRegistry.Fingerprint)) {
            var result = await service.AddSubgraph(service.Root);//TODO добавлять только новые типы
            if (result.Status != ServiceResultStatus.Ok)
                throw new Exception(result.Error);
            service.Root.Attributes[schemaRegistry.Fingerprint] = RuntimeTypesVersion;
        }
    }
}
