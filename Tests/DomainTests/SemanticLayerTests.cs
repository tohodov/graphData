using Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[RelevantTestClass]
public sealed class SemanticLayerTests : GraphServiceTests
{
    [TestMethod]
    public async Task AssignNodeType_MaterializesMultipleTypesIdempotently()
    {
        var weapon = (await Service.CreateNodeType("SemanticWeapon")).Value!.Type;
        var selectable = (await Service.CreateNodeType("SemanticSelectable")).Value!.Type;
        var ak47 = (await Service.CreateNode("semantic-ak47")).Value!;

        Assert.AreEqual(ServiceResultStatus.Ok, (await Service.AssignNodeTypeAsync(ak47.GlobalId, weapon.GlobalId)).Status);
        Assert.AreEqual(ServiceResultStatus.Ok, (await Service.AssignNodeTypeAsync(ak47.GlobalId, selectable.GlobalId)).Status);

        var firstRead = (await Service.GetSemanticNodeAsync(ak47.GlobalId)).Value!;
        CollectionAssert.AreEquivalent(
            new[] { weapon.GlobalId, selectable.GlobalId },
            firstRead.AssignedTypes.Select(static type => type.GlobalId).ToArray());
        Assert.IsTrue(firstRead.TypeInstances.All(static instance => instance.IsMaterialized));
        var witnesses = firstRead.TypeInstances.Select(static instance => instance.Witness!.GlobalId).ToArray();
        var instanceOfDefinition = TypedEdgeDefinition.Create(
            Graph.GetNodeTypeDefinition(Graph.GetRuntimeType<InstanceOfConnectionNodeType>()!));
        var endpointOccurrences = firstRead.TypeInstances
            .SelectMany(static instance => instance.Witness!.Nodes)
            .Where(node => witnesses.Any(witnessId =>
                TypedEdgeSubgraphCodec.IsDirectChildOf(node.GlobalId, witnessId)))
            .ToArray();
        Assert.AreEqual(4, endpointOccurrences.Length);
        Assert.AreEqual(4, endpointOccurrences.Select(static node => node.LocalId).Distinct().Count());
        Assert.IsTrue(endpointOccurrences.All(node =>
            node.LocalId.ToString().StartsWith("member-instance-of-", StringComparison.Ordinal)));
        Assert.IsTrue(endpointOccurrences.Any(node => TypedEdgeSubgraphCodec.HasMemberClassifier(
            node,
            instanceOfDefinition.Endpoints.Single(endpoint =>
                endpoint.Name == nameof(InstanceOfConnectionNodeType.Instance)).MemberTypeId)));
        Assert.IsTrue(endpointOccurrences.Any(node => TypedEdgeSubgraphCodec.HasMemberClassifier(
            node,
            instanceOfDefinition.Endpoints.Single(endpoint =>
                endpoint.Name == nameof(InstanceOfConnectionNodeType.Type)).MemberTypeId)));

        Assert.AreEqual(ServiceResultStatus.Ok, (await Service.AssignNodeTypeAsync(ak47.GlobalId, weapon.GlobalId)).Status);
        var secondRead = (await Service.GetSemanticNodeAsync(ak47.GlobalId)).Value!;
        CollectionAssert.AreEquivalent(
            witnesses,
            secondRead.TypeInstances.Select(static instance => instance.Witness!.GlobalId).ToArray());
    }

    [TestMethod]
    public async Task DeleteNode_RemovesMaterializedTypeInstanceSubgraphs()
    {
        var type = (await Service.CreateNodeType("SemanticDisposable")).Value!.Type;
        var node = (await Service.CreateNode("semantic-disposable")).Value!;
        Assert.AreEqual(ServiceResultStatus.Ok, (await Service.AssignNodeTypeAsync(node.GlobalId, type.GlobalId)).Status);
        var witnessIds = (await Service.GetSemanticNodeAsync(node.GlobalId)).Value!.TypeInstances
            .Select(static instance => instance.Witness!.GlobalId)
            .ToArray();

        Assert.AreEqual(ServiceResultStatus.Ok, (await Service.DeleteNode(node.GlobalId)).Status);

        Assert.IsNull(await Storage.Get(node.GlobalId));
        foreach (var witnessId in witnessIds)
            Assert.IsNull(await Storage.Get(witnessId));
    }

    [TestMethod]
    public async Task DeleteNode_DeletingEndpointInstanceRemovesOwningTypedEdgeCarrier()
    {
        var first = (await Service.CreateNode("delete-endpoint-first")).Value!;
        var second = (await Service.CreateNode("delete-endpoint-second")).Value!;
        var create = await Service.ChangeEdgeTypeAsync<InstanceOfConnectionNodeType>(
            [first.GlobalId, second.GlobalId]);
        Assert.AreEqual(ServiceResultStatus.Ok, create.Status, create.Error);
        var relation = create.Value!.Nodes.Single(node =>
            node.GlobalId.Count() == 1
            && node.LocalId.ToString().StartsWith("instance-of-", StringComparison.Ordinal));
        var definition = TypedEdgeDefinition.Create(
            Graph.GetNodeTypeDefinition(Graph.GetRuntimeType<InstanceOfConnectionNodeType>()!));
        var endpoint = await FindEndpointAsync(
            (await Storage.Get(relation.GlobalId))!,
            definition,
            nameof(InstanceOfConnectionNodeType.Instance));

        var delete = await Service.DeleteNode(endpoint.GlobalId);

        Assert.AreEqual(ServiceResultStatus.Ok, delete.Status, delete.Error);
        Assert.IsNull(await Storage.Get(relation.GlobalId));
        Assert.IsNull(await Storage.Get(endpoint.GlobalId));
        Assert.IsNotNull(await Storage.Get(first.GlobalId));
        Assert.IsNotNull(await Storage.Get(second.GlobalId));
    }

    [TestMethod]
    public async Task DisconnectHierarchy_DoesNotRelocateTypedNodeThroughSemanticInfrastructure()
    {
        var type = (await Service.CreateNodeType("SemanticHierarchyType")).Value!.Type;
        var oldParent = (await Service.CreateNode("semantic-old-parent")).Value!;
        var newParent = (await Service.CreateNode("semantic-new-parent")).Value!;
        var child = (await Service.CreateNode("semantic-child", oldParent.GlobalId)).Value!;
        Assert.AreEqual(ServiceResultStatus.Ok, (await Service.AssignNodeTypeAsync(child.GlobalId, type.GlobalId)).Status);
        Assert.AreEqual(ServiceResultStatus.Ok, (await Service.ConnectNodesAsync(child.GlobalId, newParent.GlobalId)).Status);

        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => Service.Disconnect(oldParent.GlobalId, child.GlobalId));

        StringAssert.Contains(error.Message, "relocation is ambiguous");
        Assert.IsNotNull(await Storage.Get(child.GlobalId));
        Assert.IsNotNull(await Storage.Get(oldParent.GlobalId, child.LocalId));
    }

    [TestMethod]
    public async Task AssignNodeType_MaterializesDiamondOnceAndTerminatesOnCycleAfterReopen()
    {
        var root = (await Service.CreateNodeType("SemanticRoot")).Value!.Type;
        var left = (await Service.CreateNodeType("SemanticLeft", requiredTypes: [root])).Value!.Type;
        var right = (await Service.CreateNodeType("SemanticRight", requiredTypes: [root])).Value!.Type;
        var leaf = (await Service.CreateNodeType("SemanticLeaf", requiredTypes: [left, right])).Value!.Type;
        Assert.AreEqual(
            ServiceResultStatus.Ok,
            (await Service.AddNodeTypeRequirementAsync(root.GlobalId, leaf.GlobalId)).Status);

        var ak47 = (await Service.CreateNode("semantic-cycle-ak47")).Value!;
        var assignment = await Service.AssignNodeTypeAsync(ak47.GlobalId, leaf.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, assignment.Status, assignment.Error);

        var expected = new[] { root.GlobalId, left.GlobalId, right.GlobalId, leaf.GlobalId };
        var projected = (await Service.GetSemanticNodeAsync(ak47.GlobalId)).Value!;
        CollectionAssert.AreEquivalent(expected, projected.AssignedTypes.Select(static type => type.GlobalId).ToArray());
        Assert.AreEqual(4, projected.TypeInstances.Count);

        var reopenedGraph = await global::Graph.OpenAsync(Storage, GraphSchemaRegistry.Create());
        var reopenedService = new GraphService(reopenedGraph, new GraphSearchService(Storage));
        var reopened = await reopenedService.GetSemanticNodeAsync(ak47.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, reopened.Status, reopened.Error);
        CollectionAssert.AreEquivalent(expected, reopened.Value!.AssignedTypes.Select(static type => type.GlobalId).ToArray());
        Assert.AreEqual(4, reopened.Value.TypeInstances.Count);
    }

    [TestMethod]
    public async Task AssignNodeType_RejectsDirectAbstractTypeButMaterializesItAsRequiredBase()
    {
        var abstractBase = (await Service.CreateNodeType("SemanticAbstractBase", isAbstract: true)).Value!.Type;
        var concrete = (await Service.CreateNodeType(
            "SemanticConcrete",
            requiredTypes: [abstractBase])).Value!.Type;
        var node = (await Service.CreateNode("semantic-concrete-instance")).Value!;

        var invalid = await Service.AssignNodeTypeAsync(node.GlobalId, abstractBase.GlobalId);
        Assert.AreEqual(ServiceResultStatus.BadRequest, invalid.Status);
        StringAssert.Contains(invalid.Error, "cannot be assigned directly");

        var valid = await Service.AssignNodeTypeAsync(node.GlobalId, concrete.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, valid.Status, valid.Error);
        CollectionAssert.AreEquivalent(
            new[] { abstractBase.GlobalId, concrete.GlobalId },
            (await Service.GetSemanticNodeAsync(node.GlobalId)).Value!.AssignedTypes
                .Select(static type => type.GlobalId)
                .ToArray());
    }

    [TestMethod]
    public async Task AddingRequirement_InvalidatesOldProjectionUntilFacetsAreMaterialized()
    {
        var required = (await Service.CreateNodeType("LateRequired")).Value!.Type;
        var derived = (await Service.CreateNodeType("LateDerived")).Value!.Type;
        var node = (await Service.CreateNode("late-required-instance")).Value!;
        Assert.AreEqual(ServiceResultStatus.Ok, (await Service.AssignNodeTypeAsync(node.GlobalId, derived.GlobalId)).Status);

        Assert.AreEqual(
            ServiceResultStatus.Ok,
            (await Service.AddNodeTypeRequirementAsync(derived.GlobalId, required.GlobalId)).Status);
        var stale = await Service.GetSemanticNodeAsync(node.GlobalId);
        Assert.AreEqual(ServiceResultStatus.BadRequest, stale.Status);
        StringAssert.Contains(stale.Error, "missing materialized required types");

        Assert.AreEqual(ServiceResultStatus.Ok, (await Service.AssignNodeTypeAsync(node.GlobalId, derived.GlobalId)).Status);
        CollectionAssert.AreEquivalent(
            new[] { required.GlobalId, derived.GlobalId },
            (await Service.GetSemanticNodeAsync(node.GlobalId)).Value!.AssignedTypes
                .Select(static type => type.GlobalId)
                .ToArray());
    }

    [TestMethod]
    public async Task SemanticProjection_UsesExplicitBasisOutsideDefaultTypeCatalog()
    {
        var externalType = (await Service.CreateNode("external-basis-type")).Value!;
        var instance = (await Service.CreateNode("external-basis-instance")).Value!;
        var materialize = await Service.ChangeEdgeTypeAsync<InstanceOfConnectionNodeType>(
            [instance.GlobalId, externalType.GlobalId]);
        Assert.AreEqual(ServiceResultStatus.Ok, materialize.Status, materialize.Error);

        var defaultProjection = (await Service.GetSemanticNodeAsync(instance.GlobalId)).Value!;
        Assert.AreEqual(0, defaultProjection.AssignedTypes.Count);

        var selectedProjection = await Service.GetSemanticNodeAsync(instance.GlobalId, [externalType.GlobalId]);
        Assert.AreEqual(ServiceResultStatus.Ok, selectedProjection.Status, selectedProjection.Error);
        Assert.AreEqual(externalType.GlobalId, selectedProjection.Value!.AssignedTypes.Single().GlobalId);
        Assert.IsTrue(selectedProjection.Value.TypeInstances.Single().IsMaterialized);
    }

    [TestMethod]
    public async Task SemanticProjection_DoesNotTreatBasisRootAsInstanceOfItsChildren()
    {
        var firstType = (await Service.CreateNodeType("BasisRootFirst")).Value!.Type;
        var secondType = (await Service.CreateNodeType("BasisRootSecond")).Value!.Type;

        var projection = await Service.GetSemanticNodeAsync(
            Graph.NodeTypes.GlobalId,
            [firstType.GlobalId, secondType.GlobalId]);

        Assert.AreEqual(ServiceResultStatus.Ok, projection.Status, projection.Error);
        Assert.AreEqual(0, projection.Value!.AssignedTypes.Count);
    }

    [TestMethod]
    public async Task TypedEdgeReader_RejectsAmbiguousSingleEndpoint()
    {
        var first = (await Service.CreateNode("reader-first")).Value!;
        var second = (await Service.CreateNode("reader-second")).Value!;
        var extra = (await Service.CreateNode("reader-extra")).Value!;
        var create = await Service.ChangeEdgeTypeAsync<InstanceOfConnectionNodeType>([first.GlobalId, second.GlobalId]);
        Assert.AreEqual(ServiceResultStatus.Ok, create.Status, create.Error);
        var relation = create.Value!.Nodes.Single(node =>
            node.LocalId.ToString().StartsWith("instance-of-", StringComparison.Ordinal)
            && node.GlobalId.ToString() == node.LocalId.ToString());
        var definition = TypedEdgeDefinition.Create(
            Graph.GetNodeTypeDefinition(Graph.GetRuntimeType<InstanceOfConnectionNodeType>()!));
        var instanceEndpoint = await FindEndpointAsync(
            (await Storage.Get(relation.GlobalId))!,
            definition,
            nameof(InstanceOfConnectionNodeType.Instance));
        await Storage.Connect(instanceEndpoint.GlobalId, extra.GlobalId);

        var read = await Service.GetTypedEdgeInstanceAsync(relation.GlobalId);
        Assert.AreEqual(ServiceResultStatus.BadRequest, read.Status);
        StringAssert.Contains(read.Error, "expects 1..1 participants");
    }

    [TestMethod]
    public async Task RawRead_RemainsAvailableWhenSemanticProjectionIsAmbiguous()
    {
        var type = (await Service.CreateNodeType("AmbiguousSemanticType")).Value!.Type;
        var node = (await Service.CreateNode("ambiguous-semantic-node")).Value!;
        Assert.AreEqual(ServiceResultStatus.Ok, (await Service.AssignNodeTypeAsync(node.GlobalId, type.GlobalId)).Status);
        var instanceOfType = Graph.GetRuntimeType<InstanceOfConnectionNodeType>()!;
        var instanceOfDefinition = TypedEdgeDefinition.Create(Graph.GetNodeTypeDefinition(instanceOfType));
        await TypedEdgeSubgraphCodec.CreateAsync(
            Storage,
            "duplicate-instance-of",
            instanceOfDefinition,
            new Dictionary<string, IReadOnlyCollection<NodeBacking>>(StringComparer.Ordinal) {
                [nameof(InstanceOfConnectionNodeType.Instance)] = [node.Backing],
                [nameof(InstanceOfConnectionNodeType.Type)] = [type.Backing]
            });

        var raw = await Service.GetNode(node.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, raw.Status, raw.Error);
        Assert.AreEqual(node.GlobalId, raw.Value!.GlobalId);

        var semantic = await Service.GetSemanticNodeAsync(node.GlobalId);
        Assert.AreEqual(ServiceResultStatus.BadRequest, semantic.Status);
        StringAssert.Contains(semantic.Error, "more than one materialized instance");
    }

    [TestMethod]
    public async Task AssignNodeType_RejectsSameRangeMembersUntilMemberInstancesExist()
    {
        var person = (await Service.CreateNodeType("SemanticPerson")).Value!.Type;
        var flight = (await Service.CreateNodeType(
            "SemanticFlight",
            fields: [
                new NodeFieldDefinition("Origin", NodeFieldValueKind.Node, typeof(Node), NodeSlotCardinality.Required(), false, person),
                new NodeFieldDefinition("Destination", NodeFieldValueKind.Node, typeof(Node), NodeSlotCardinality.Required(), false, person)
            ])).Value!.Type;
        var participant = (await Service.CreateNode("semantic-person")).Value!;
        Assert.AreEqual(ServiceResultStatus.Ok, (await Service.AssignNodeTypeAsync(participant.GlobalId, person.GlobalId)).Status);
        var candidate = (await Service.CreateNode("semantic-flight")).Value!;
        Assert.AreEqual(ServiceResultStatus.Ok, (await Service.ConnectNodesAsync(candidate.GlobalId, participant.GlobalId)).Status);

        var assignment = await Service.AssignNodeTypeAsync(candidate.GlobalId, flight.GlobalId);

        Assert.AreEqual(ServiceResultStatus.BadRequest, assignment.Status);
        StringAssert.Contains(assignment.Error, "Materialized member instances are required");
    }

    [TestMethod]
    public async Task ChangeEdgeType_RejectsAmbiguousExistingCarriersWithoutMutation()
    {
        static NodeFieldDefinition Endpoint(string name) => new(
            name,
            NodeFieldValueKind.Node,
            typeof(Node),
            NodeSlotCardinality.Required(),
            IsCollection: false);

        var oldDefinition = (await Service.CreateNodeType(
            "AmbiguousOldEdge",
            fields: [Endpoint("Source"), Endpoint("Target")])).Value!;
        var replacement = (await Service.CreateNodeType(
            "AmbiguousReplacementEdge",
            fields: [Endpoint("Left"), Endpoint("Right")])).Value!;
        var source = (await Service.CreateNode("ambiguous-edge-source")).Value!;
        var target = (await Service.CreateNode("ambiguous-edge-target")).Value!;
        var typedDefinition = TypedEdgeDefinition.Create(oldDefinition);
        var participants = new Dictionary<string, IReadOnlyCollection<NodeBacking>>(StringComparer.Ordinal) {
            ["Source"] = [source.Backing],
            ["Target"] = [target.Backing]
        };
        var first = await TypedEdgeSubgraphCodec.CreateAsync(
            Storage,
            "ambiguous-edge-first",
            typedDefinition,
            participants);
        var second = await TypedEdgeSubgraphCodec.CreateAsync(
            Storage,
            "ambiguous-edge-second",
            typedDefinition,
            participants);

        var result = await Service.ChangeEdgeTypeAsync(
            source.GlobalId,
            target.GlobalId,
            replacement.Type.GlobalId);

        Assert.AreEqual(ServiceResultStatus.BadRequest, result.Status);
        StringAssert.Contains(result.Error, "ambiguous");
        Assert.IsNotNull(await Storage.Get(first.GlobalId));
        Assert.IsNotNull(await Storage.Get(second.GlobalId));
        Assert.IsNull(await Storage.Get(new NodePath("ambiguous-replacement-edge-1")));
    }

    private static async Task<NodeBacking> FindEndpointAsync(
        NodeBacking relation,
        TypedEdgeDefinition definition,
        string memberName) {
        var memberTypeId = definition.Endpoints
            .Single(endpoint => endpoint.Name == memberName)
            .MemberTypeId;
        var matches = new List<NodeBacking>();
        await foreach (var child in relation.Nodes) {
            if (!TypedEdgeSubgraphCodec.IsDirectChildOf(child.GlobalId, relation.GlobalId))
                continue;
            if (await TypedEdgeSubgraphCodec.HasMemberClassifierAsync(child, memberTypeId))
                matches.Add(child);
        }
        return matches.Single();
    }

}
