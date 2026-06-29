using GraphData.Core.Services;

namespace Domain.Services;

public class GraphStorageInitializer {
    readonly GraphFactory graphFactory;

    public GraphStorageInitializer(GraphFactory graphFactory) {
        this.graphFactory = graphFactory;
    }

    public async Task<Graph> InitializeAsync() {
        return await graphFactory.OpenAsync().ConfigureAwait(false);
    }
}
