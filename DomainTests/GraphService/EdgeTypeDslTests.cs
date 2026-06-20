using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.GraphService;

[RelevantTestClass]
public sealed class EdgeTypeDslTests
{
    [TestMethod]
    public async Task GraphService_ChangeEdgeTypeAsync_CreatesMetadataFreeTypedEdgeSubgraph()
    {
        await using var scope = TestGraphStorageScope.Create();
        await new GraphStorageInitializer(scope.Storage, typeof(ManufacturedByEdgeType).Assembly).InitializeAsync();
        var graph = new GraphData.Core.Services.GraphService(
            scope.Storage,
            new GraphData.Core.Services.GraphSearchService(scope.Storage),
            new CancellationTokensAccessorMock(),
            typeof(ManufacturedByEdgeType).Assembly);
        var weapon = await graph.CreateNode<EdgeWeaponNodeType>(new("ak-47"));
        Assert.AreEqual(ServiceResultStatus.Ok, weapon.Status, weapon.Error);
        var manufacturer = await graph.CreateNode<EdgeManufacturerNodeType>(new("kalashnikov"));
        Assert.AreEqual(ServiceResultStatus.Ok, manufacturer.Status, manufacturer.Error);
        await graph.ConnectNodesAsync(weapon.Value!.GlobalId, manufacturer.Value!.GlobalId);

        var result = await graph.ChangeEdgeTypeAsync<ManufacturedByEdgeType>(
            weapon.Value.GlobalId,
            manufacturer.Value.GlobalId,
            relationLocalId: "manufactured-by-1");

        Assert.AreEqual(ServiceResultStatus.Ok, result.Status, result.Error);
        var relationId = new InternalId("manufactured-by-1");
        var relation = await GetRequiredAsync(scope, relationId);
        AssertMetadataFree(relation);

        var weaponEndpointId = new InternalId("manufactured-by-1", EndpointInstanceLocalId(nameof(ManufacturedByEdgeType.Weapon)));
        var manufacturerEndpointId = new InternalId("manufactured-by-1", EndpointInstanceLocalId(nameof(ManufacturedByEdgeType.Manufacturer)));
        var weaponEndpoint = await GetRequiredAsync(scope, weaponEndpointId);
        var manufacturerEndpoint = await GetRequiredAsync(scope, manufacturerEndpointId);
        AssertMetadataFree(weaponEndpoint);
        AssertMetadataFree(manufacturerEndpoint);

        var edgeTypeId = await GetEdgeTypeIdAsync<ManufacturedByEdgeType>(graph);
        var weaponNodeTypeId = await GetNodeTypeIdAsync<EdgeWeaponNodeType>(graph);
        var manufacturerNodeTypeId = await GetNodeTypeIdAsync<EdgeManufacturerNodeType>(graph);
        var weaponEndpointSpec = await GetEndpointSpecAsync(scope, weaponEndpointId, relationId, weapon.Value.GlobalId);
        var manufacturerEndpointSpec = await GetEndpointSpecAsync(scope, manufacturerEndpointId, relationId, manufacturer.Value.GlobalId);

        await AssertConnectedAsync(scope, relationId, edgeTypeId);
        await AssertConnectedAsync(scope, edgeTypeId, GraphBaseTypeIds.Relation);
        await AssertConnectedAsync(scope, weaponEndpointId, GraphBaseTypeIds.Endpoint);
        await AssertConnectedAsync(scope, manufacturerEndpointId, GraphBaseTypeIds.Endpoint);
        await AssertConnectedAsync(scope, weaponEndpointId, weapon.Value.GlobalId);
        await AssertConnectedAsync(scope, weaponEndpointId, weaponEndpointSpec.GlobalId);
        await AssertConnectedAsync(scope, manufacturerEndpointId, manufacturer.Value.GlobalId);
        await AssertConnectedAsync(scope, manufacturerEndpointId, manufacturerEndpointSpec.GlobalId);
        await AssertConnectedAsync(scope, weaponEndpointSpec.GlobalId, weaponNodeTypeId);
        await AssertConnectedAsync(scope, manufacturerEndpointSpec.GlobalId, manufacturerNodeTypeId);

        var weaponConnections = (await scope.Storage.GetConnectedNodesAsync((await scope.Storage.Get(weapon.Value.GlobalId)).Value!)).Value!;
        Assert.IsFalse(weaponConnections.Any(node => node.GlobalId == manufacturer.Value.GlobalId));

        var returnedIds = result.Value!.Nodes.Select(static node => node.GlobalId).ToArray();
        CollectionAssert.IsSubsetOf(
            new[] {
                relationId,
                edgeTypeId,
                GraphBaseTypeIds.Endpoint,
                weapon.Value.GlobalId,
                manufacturer.Value.GlobalId,
                weaponEndpointId,
                manufacturerEndpointId,
                weaponEndpointSpec.GlobalId,
                manufacturerEndpointSpec.GlobalId
            },
            returnedIds);
    }

    [TestMethod]
    public async Task GraphService_GetEdgeTypeDefinitionAsync_DiscoversRichEndpointMembers()
    {
        await using var scope = TestGraphStorageScope.Create();
        await new GraphStorageInitializer(scope.Storage, typeof(ShipmentEdgeType).Assembly).InitializeAsync();
        var graph = new GraphData.Core.Services.GraphService(
            scope.Storage,
            new GraphData.Core.Services.GraphSearchService(scope.Storage),
            new CancellationTokensAccessorMock(),
            typeof(ShipmentEdgeType).Assembly);

        var result = await graph.GetEdgeTypeDefinitionAsync<ShipmentEdgeType>();

        Assert.AreEqual(ServiceResultStatus.Ok, result.Status, result.Error);
        var definition = result.Value!;
        var typeState = await GetRequiredAsync(scope, definition.TypeId);
        AssertMetadataFree(typeState);
        var weaponNodeTypeId = await GetNodeTypeIdAsync<EdgeWeaponNodeType>(graph);
        var manufacturerNodeTypeId = await GetNodeTypeIdAsync<EdgeManufacturerNodeType>(graph);

        Assert.AreEqual(4, definition.Endpoints.Count);
        AssertEndpoint(definition, nameof(ShipmentEdgeType.Weapon), typeof(EdgeWeaponNodeType), NodeSlotCardinality.Required(), weaponNodeTypeId);
        AssertEndpoint(definition, nameof(ShipmentEdgeType.Counterparty), typeof(Node), NodeSlotCardinality.Required());
        AssertEndpoint(definition, nameof(ShipmentEdgeType.OptionalWaypoint), typeof(Node), NodeSlotCardinality.Optional());
        AssertEndpoint(definition, nameof(ShipmentEdgeType.Manufacturers), typeof(EdgeManufacturerNodeType), NodeSlotCardinality.Many(), manufacturerNodeTypeId, isCollection: true);
        Assert.IsFalse(definition.Endpoints.Any(endpoint => endpoint.Name == nameof(ShipmentEdgeType.Note)));
    }

    private static string EndpointInstanceLocalId(string endpointName) =>
        $"endpoint-{endpointName}";

    private static async Task<InternalId> GetEdgeTypeIdAsync<TEdgeType>(GraphData.Core.Services.GraphService graph)
        where TEdgeType : EdgeType
    {
        var result = await graph.GetEdgeTypeDefinitionAsync<TEdgeType>();
        Assert.AreEqual(ServiceResultStatus.Ok, result.Status, result.Error);
        return result.Value!.TypeId;
    }

    private static async Task<InternalId> GetNodeTypeIdAsync<TNodeType>(GraphData.Core.Services.GraphService graph)
        where TNodeType : NodeType
    {
        var result = await graph.GetNodeTypeDefinitionAsync<TNodeType>();
        Assert.AreEqual(ServiceResultStatus.Ok, result.Status, result.Error);
        return result.Value!.Type.GlobalId;
    }

    private static async Task<NodeState> GetRequiredAsync(TestGraphStorageScope scope, InternalId id)
    {
        var result = await scope.Storage.Get(id);
        Assert.AreEqual(ServiceResultStatus.Ok, result.Status, result.Error);
        Assert.IsNotNull(result.Value);
        return result.Value;
    }

    private static void AssertMetadataFree(NodeState node)
    {
        Assert.AreEqual(0, node.Attributes.Count);
        Assert.IsFalse(node.Attributes.ContainsKey("graph.kind"));
        Assert.IsFalse(node.Attributes.ContainsKey("graph.role"));
        Assert.IsFalse(node.Attributes.ContainsKey("graph.typeName"));
    }

    private static async Task AssertConnectedAsync(TestGraphStorageScope scope, InternalId sourceId, InternalId targetId)
    {
        var source = await GetRequiredAsync(scope, sourceId);
        var connected = (await scope.Storage.GetConnectedNodesAsync(source)).Value!;
        Assert.IsTrue(
            connected.Any(node => node.GlobalId == targetId),
            $"Expected '{sourceId}' to be connected to '{targetId}'.");
    }

    private static async Task<NodeState> GetEndpointSpecAsync(
        TestGraphStorageScope scope,
        InternalId endpointInstanceId,
        InternalId relationId,
        InternalId endpointNodeId)
    {
        var endpoint = await GetRequiredAsync(scope, endpointInstanceId);
        var connected = (await scope.Storage.GetConnectedNodesAsync(endpoint)).Value!;
        var spec = connected.Single(node =>
            node.GlobalId != relationId
            && node.GlobalId != endpointNodeId
            && node.GlobalId != GraphBaseTypeIds.Endpoint);
        return spec;
    }

    private static void AssertEndpoint(
        EdgeTypeDefinition definition,
        string name,
        Type clrType,
        NodeSlotCardinality cardinality,
        InternalId? nodeTypeId = null,
        bool isCollection = false)
    {
        var endpoint = definition.Endpoints.Single(value => value.Name == name);
        Assert.AreEqual(clrType, endpoint.ClrType);
        Assert.AreEqual(cardinality, endpoint.Cardinality);
        Assert.AreEqual(nodeTypeId, endpoint.NodeTypeId);
        Assert.AreEqual(isCollection, endpoint.IsCollection);
    }

    private sealed class EdgeWeaponNodeType : NodeType
    {
    }

    private sealed class EdgeManufacturerNodeType : NodeType
    {
    }

    private sealed class ManufacturedByEdgeType : EdgeType
    {
        public EdgeWeaponNodeType Weapon = null!;
        public EdgeManufacturerNodeType Manufacturer = null!;
    }

    private sealed class ShipmentEdgeType : EdgeType
    {
        public EdgeWeaponNodeType Weapon = null!;
        public Node Counterparty = null!;
        public Node? OptionalWaypoint = null;
        public IReadOnlyCollection<EdgeManufacturerNodeType> Manufacturers = [];
        public string Note = "";
    }
}
