using Abstractions;
using GraphData.Core.Models;
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
        var checkResult = await service.GetNodeAsync(GraphSystemNodeIds.RuntimeTypesInitializer).ConfigureAwait(false);
        if (checkResult.Status == ServiceResultStatus.NotFound) {
            var storageStateNode = (Node)await service.GetNodeAsync(GraphSystemNodeIds.RuntimeTypesInitializer);
            storageStateNode.Nodes.Clear();
            storageStateNode.Nodes.Add(new Node(new NodeLocalId(RuntimeTypesVersion)));
            storageStateNode.Nodes.Add(new Node(new NodeLocalId(schemaRegistry.Fingerprint)));
            var result = await service.AddSubgraph(service.Root).ConfigureAwait(false);
            if (result.Status != ServiceResultStatus.Ok)
                throw new Exception(result.Error);
        } else if (checkResult.Status == ServiceResultStatus.Ok)
            return;
        else
            throw new Exception(checkResult.Error);
    }
}
