using Abstractions;
using GraphData.Core.Services;

namespace Domain.Services;

public class GraphStorageInitializer {
    readonly GraphService service;

    public GraphStorageInitializer(GraphService service, GraphSchemaRegistry schemaRegistry) {
        this.service = service;
    }

    public async Task InitializeAsync() {
        var result = await service.AddSubgraph(service.Root).ConfigureAwait(false);
        if (result.Status != ServiceResultStatus.Ok)
            throw new Exception(result.Error);
    }
}
