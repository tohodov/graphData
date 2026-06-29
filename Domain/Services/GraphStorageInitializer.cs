using GraphData.Core.Services;

namespace Domain.Services;

public class GraphStorageInitializer {
    readonly GraphService service;

    public GraphStorageInitializer(GraphService service) {
        this.service = service;
    }

    public async Task InitializeAsync() {
        await service.InitializeSchemaAsync().ConfigureAwait(false);
    }
}
