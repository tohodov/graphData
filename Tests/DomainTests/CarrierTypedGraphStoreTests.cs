using Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using GraphData.Typed;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[RelevantTestClass]
public sealed class CarrierTypedGraphStoreTests : GraphServiceTests
{
    [TestMethod]
    public async Task CreateCollision_DoesNotOverwriteOrDeleteAnExistingCarrier()
    {
        using var typed = CreateTypedGraph();
        await typed.CreateTypeAsync(InstanceType("CollisionType"));
        var existing = (await Service.CreateNode("occupied", attributes: new Dictionary<string, string> { ["label"] = "before" })).Value!;
        var child = (await Service.CreateNode("child", existing.GlobalId)).Value!;

        var error = await Assert.ThrowsExceptionAsync<TypedGraphException>(() => typed.CreateInstanceAsync(
            "occupied", ["NodeTypes/CollisionType"], new Dictionary<string, string> { ["label"] = "after" }));

        Assert.AreEqual(TypedGraphError.Conflict, error.Error);
        Assert.AreEqual("before", (await Storage.Get(existing.GlobalId))!.Attributes["label"]);
        Assert.IsNotNull(await Storage.Get(child.GlobalId));
    }

    [TestMethod]
    public async Task MissingRelationRole_IsCorruptInsteadOfAnOrdinaryInstance()
    {
        using var typed = CreateTypedGraph();
        await CreatePairAsync(typed);
        await typed.CreateRelationAsync("relation", "NodeTypes/Pair", Pair("left", "right"), EmptyAttributes());
        var carrier = (await Service.GetTypedEdgeInstanceAsync(new NodePath("relation"))).Value!;
        await Storage.Delete(carrier.Endpoint("Left").EndpointNode.Backing);

        var error = await Assert.ThrowsExceptionAsync<TypedGraphException>(() => typed.GetAsync("relation"));

        Assert.AreEqual(TypedGraphError.Corrupt, error.Error);
        Assert.IsNotNull(await Storage.Get(new NodePath("relation")));
    }

    [TestMethod]
    public async Task MissingRelationClassifier_DoesNotHideTypedRoleCarriers()
    {
        using var typed = CreateTypedGraph();
        await CreatePairAsync(typed);
        await typed.CreateRelationAsync("unclassified", "NodeTypes/Pair", Pair("left", "right"), EmptyAttributes());
        await Storage.Disconnect(new NodePath("unclassified"), new NodePath("NodeTypes", "Pair"));

        var error = await Assert.ThrowsExceptionAsync<TypedGraphException>(() => typed.GetAsync("unclassified"));

        Assert.AreEqual(TypedGraphError.Corrupt, error.Error);
    }

    [TestMethod]
    public async Task DeleteWithRawConnection_RejectsBeforeRemovingFacetWitnesses()
    {
        using var typed = CreateTypedGraph();
        await typed.CreateTypeAsync(InstanceType("GuardedType"));
        await typed.CreateInstanceAsync("guarded", ["NodeTypes/GuardedType"], EmptyAttributes());
        var unrelated = (await Service.CreateNode("unrepresented")).Value!;
        await Service.ConnectNodesAsync(new NodePath("guarded"), unrelated.GlobalId);
        var before = (await Service.GetSemanticNodeAsync(new NodePath("guarded"))).Value!;
        var witnessId = before.TypeInstances.Single().Witness!.GlobalId;

        var error = await Assert.ThrowsExceptionAsync<TypedGraphException>(() => typed.DeleteAsync("guarded"));

        Assert.AreEqual(TypedGraphError.Unsupported, error.Error);
        Assert.IsNotNull(await Storage.Get(witnessId));
        Assert.IsNotNull(await Storage.Get(new NodePath("guarded")));
        Assert.IsNotNull(await Storage.Get(unrelated.GlobalId));
    }

    [TestMethod]
    public async Task HigherOrderRelation_RemainsReadableAndDeletionIsRestricted()
    {
        using var typed = CreateTypedGraph();
        await CreatePairAsync(typed);
        await typed.CreateRelationAsync("base-relation", "NodeTypes/Pair", Pair("left", "right"), EmptyAttributes());
        await typed.CreateRelationAsync("annotation", "NodeTypes/Pair", Pair("base-relation", "left"), EmptyAttributes());

        Assert.AreEqual(TypedElementKind.Relation, (await typed.GetAsync("base-relation")).Kind);
        var incident = await typed.FindIncidentRelationsAsync("base-relation");
        CollectionAssert.AreEqual(new[] { "annotation" }, incident.Select(static element => element.Id).ToArray());
        var error = await Assert.ThrowsExceptionAsync<TypedGraphException>(() => typed.DeleteAsync("base-relation"));
        Assert.AreEqual(TypedGraphError.Conflict, error.Error);
        await typed.DeleteAsync("annotation");
        await typed.DeleteAsync("base-relation");
        Assert.IsNull(await Storage.Get(new NodePath("base-relation")));
    }

    [TestMethod]
    public async Task InfrastructureIsHiddenAndAbstractRequiredFacetsAreCleanedUp()
    {
        using var typed = CreateTypedGraph();
        var abstractType = InstanceType("AbstractFacet") with { IsAbstract = true };
        await typed.CreateTypeAsync(abstractType);
        await typed.CreateTypeAsync(InstanceType("ConcreteFacet") with { RequiredTypeIds = [abstractType.Id] });
        var instance = await typed.CreateInstanceAsync("faceted", ["NodeTypes/ConcreteFacet"], EmptyAttributes());
        CollectionAssert.AreEquivalent(new[] { "NodeTypes/AbstractFacet", "NodeTypes/ConcreteFacet" }, instance.TypeIds.ToArray());
        var raw = (await Service.GetSemanticNodeAsync(new NodePath("faceted"))).Value!;
        var witnesses = raw.TypeInstances.Select(static facet => facet.Witness!.GlobalId).ToArray();
        var catalog = await typed.GetTypesAsync();
        Assert.IsFalse(catalog.Any(type => type.Id == Graph.GetRuntimeType<InstanceOfEdge>()!.GlobalId.ToString()));
        var error = await Assert.ThrowsExceptionAsync<TypedGraphException>(() => typed.GetAsync(witnesses[0].ToString()));
        Assert.AreEqual(TypedGraphError.Unsupported, error.Error);

        await typed.DeleteAsync("faceted");

        foreach (var witness in witnesses)
            Assert.IsNull(await Storage.Get(witness));
        Assert.IsNotNull(await typed.GetTypeAsync(abstractType.Id));
    }

    [TestMethod]
    public async Task ExistingNamedInstanceFields_ThrowUnsupported()
    {
        var person = (await Service.CreateNodeType("NamedPerson")).Value!.Type;
        var unsupported = (await Service.CreateNodeType("NamedFieldOwner", fields: [
            new NodeFieldDefinition("Owner", NodeFieldValueKind.Node, typeof(Node), NodeSlotCardinality.Optional(), false, person)
        ])).Value!.Type;
        using var typed = CreateTypedGraph();

        var error = await Assert.ThrowsExceptionAsync<TypedGraphException>(() => typed.GetTypeAsync(unsupported.GlobalId.ToString()));

        Assert.AreEqual(TypedGraphError.Unsupported, error.Error);
    }

    [DataTestMethod]
    [DataRow("clrType")]
    [DataRow("min")]
    public async Task PersistedSchemaConflict_IsNotHiddenByTheCachedDefinition(string changedAttribute)
    {
        using var typed = CreateTypedGraph();
        var definition = InstanceType("SchemaConflict") with {
            Attributes = [new TypedAttributeDefinition("Count", ScalarKind.Int32, Required: true)]
        };
        await typed.CreateTypeAsync(definition);
        Assert.AreEqual(ScalarKind.Int32, (await typed.GetTypeAsync(definition.Id)).Attributes.Single().Kind);
        var fieldPath = new NodePath("NodeTypes", "SchemaConflict", "Definition", "Fields", "Count");
        var field = await Storage.Get(fieldPath);
        Assert.IsNotNull(field);
        var attributes = field.Attributes.ToDictionary();
        attributes[changedAttribute] = changedAttribute == "clrType" ? typeof(bool).AssemblyQualifiedName! : "0";
        await Storage.Update(fieldPath, attributes);

        // The old raw reader retains its existing cache behavior; only the adapter rejects ambiguity.
        var carrierType = await Service.GetTypeNode(new NodePath("NodeTypes", "SchemaConflict"));
        Assert.IsNotNull(carrierType);
        var cached = Graph.GetNodeTypeDefinition(carrierType).Fields.Single();
        Assert.AreEqual(typeof(int), cached.ClrType);
        Assert.AreEqual(1, cached.Cardinality.Min);
        var readError = await Assert.ThrowsExceptionAsync<TypedGraphException>(() => typed.GetTypeAsync(definition.Id));
        Assert.AreEqual(TypedGraphError.Corrupt, readError.Error);
        StringAssert.Contains(readError.Message, "conflicting");
        var catalogError = await Assert.ThrowsExceptionAsync<TypedGraphException>(() => typed.GetTypesAsync());
        Assert.AreEqual(TypedGraphError.Corrupt, catalogError.Error);
    }

    [TestMethod]
    public async Task MissingPersistedSchema_IsNotReplacedByTheCachedDefinition()
    {
        using var typed = CreateTypedGraph();
        var definition = InstanceType("MissingSchema") with {
            Attributes = [new TypedAttributeDefinition("Count", ScalarKind.Int32, Required: true)]
        };
        await typed.CreateTypeAsync(definition);
        Assert.AreEqual(ScalarKind.Int32, (await typed.GetTypeAsync(definition.Id)).Attributes.Single().Kind);
        var persisted = await Storage.Get(new NodePath("NodeTypes", "MissingSchema", "Definition"));
        Assert.IsNotNull(persisted);
        await Storage.Delete(persisted);

        var error = await Assert.ThrowsExceptionAsync<TypedGraphException>(() => typed.GetTypeAsync(definition.Id));

        Assert.AreEqual(TypedGraphError.Corrupt, error.Error);
        StringAssert.Contains(error.Message, "no persisted definition");
    }

    [TestMethod]
    public async Task SameLocalParticipantNames_RejectBeforeCreatingRelationOrMemberTypes()
    {
        using var typed = CreateTypedGraph();
        await typed.CreateTypeAsync(InstanceType("FirstParticipant"));
        await typed.CreateTypeAsync(InstanceType("SecondParticipant"));
        await typed.CreateTypeAsync(new TypedType("NodeTypes/Group", TypedElementKind.Relation, false, [], [
            new TypedMemberDefinition("Sources", null, 2, 2),
            new TypedMemberDefinition("Target", null)
        ], []));
        var firstParent = (await Service.CreateNode("first-parent")).Value!;
        var secondParent = (await Service.CreateNode("second-parent")).Value!;
        var first = (await Service.CreateNode("same", firstParent.GlobalId)).Value!;
        var second = (await Service.CreateNode("same", secondParent.GlobalId)).Value!;
        Assert.AreEqual(ServiceResultStatus.Ok, (await Service.AssignNodeTypeAsync(first.GlobalId, new NodePath("NodeTypes", "FirstParticipant"))).Status);
        Assert.AreEqual(ServiceResultStatus.Ok, (await Service.AssignNodeTypeAsync(second.GlobalId, new NodePath("NodeTypes", "SecondParticipant"))).Status);
        var before = await CarrierIdsAsync();

        var error = await Assert.ThrowsExceptionAsync<TypedGraphException>(() => typed.CreateRelationAsync(
            "group", "NodeTypes/Group", new Dictionary<string, IReadOnlyList<string>> {
                ["Sources"] = [first.GlobalId.ToString(), second.GlobalId.ToString()],
                ["Target"] = [first.GlobalId.ToString()]
            }, EmptyAttributes()));

        Assert.AreEqual(TypedGraphError.Unsupported, error.Error);
        StringAssert.Contains(error.Message, "same");
        CollectionAssert.AreEqual(before, await CarrierIdsAsync());
        Assert.IsNull(await Storage.Get(new NodePath("group")));
    }

    [TestMethod]
    public async Task RelationTypeNameCollidesWithRoleCarrier_RejectBeforeCodecWrites()
    {
        using var typed = CreateTypedGraph();
        await CreatePairAsync(typed);
        const string typeId = "NodeTypes/MEMBER-collision-1";
        await typed.CreateTypeAsync(new TypedType(typeId, TypedElementKind.Relation, false, [], [
            new TypedMemberDefinition("Left", null), new TypedMemberDefinition("Right", null)
        ], []));
        var before = await CarrierIdsAsync();

        var error = await Assert.ThrowsExceptionAsync<TypedGraphException>(() => typed.CreateRelationAsync(
            "collision", typeId, Pair("left", "right"), EmptyAttributes()));

        Assert.AreEqual(TypedGraphError.Unsupported, error.Error);
        CollectionAssert.AreEqual(before, await CarrierIdsAsync());
        Assert.AreEqual(2, (await typed.GetTypeAsync(typeId)).Members.Count);
    }

    [TestMethod]
    public async Task ParticipantBacklinkSlotIsOccupied_RejectBeforeCodecWrites()
    {
        using var typed = CreateTypedGraph();
        await CreatePairAsync(typed);
        var existing = (await Service.CreateNode("member-relation-1")).Value!;
        await Storage.Connect(new NodePath("left"), existing.GlobalId);
        var before = await CarrierIdsAsync();

        var error = await Assert.ThrowsExceptionAsync<TypedGraphException>(() => typed.CreateRelationAsync(
            "relation", "NodeTypes/Pair", Pair("left", "right"), EmptyAttributes()));

        Assert.AreEqual(TypedGraphError.Unsupported, error.Error);
        CollectionAssert.AreEqual(before, await CarrierIdsAsync());
        Assert.IsTrue(await (await Storage.Get(new NodePath("left")))!.Nodes.AnyAsync(node => node.GlobalId == existing.GlobalId));
    }

    [TestMethod]
    public async Task InstanceNameCollidesWithTypeSchemaChild_RejectBeforeMaterializingFacets()
    {
        using var typed = CreateTypedGraph();
        await typed.CreateTypeAsync(InstanceType("SchemaOwner"));
        var before = await CarrierIdsAsync();

        var error = await Assert.ThrowsExceptionAsync<TypedGraphException>(() => typed.CreateInstanceAsync(
            "Definition", ["NodeTypes/SchemaOwner"], EmptyAttributes()));

        Assert.AreEqual(TypedGraphError.Unsupported, error.Error);
        CollectionAssert.AreEqual(before, await CarrierIdsAsync());
        Assert.IsNull(await Storage.Get(new NodePath("Definition")));
        Assert.IsNotNull(await Storage.Get(new NodePath("NodeTypes", "SchemaOwner", "Definition")));
    }

    async Task<string[]> CarrierIdsAsync() => (await Service.GetSubgraph([], int.MaxValue)).Value!.Nodes
        .Select(static node => node.GlobalId.ToString()).Order(StringComparer.Ordinal).ToArray();

    TypedGraphService CreateTypedGraph() => new(new CarrierTypedGraphStore(Graph));

    static TypedType InstanceType(string name) => new("NodeTypes/" + name, TypedElementKind.Instance, false, [], [], []);

    static async Task CreatePairAsync(ITypedGraph typed)
    {
        await typed.CreateTypeAsync(InstanceType("Item"));
        await typed.CreateTypeAsync(new TypedType("NodeTypes/Pair", TypedElementKind.Relation, false, [], [
            new TypedMemberDefinition("Left", null), new TypedMemberDefinition("Right", null)
        ], []));
        await typed.CreateInstanceAsync("left", ["NodeTypes/Item"], EmptyAttributes());
        await typed.CreateInstanceAsync("right", ["NodeTypes/Item"], EmptyAttributes());
    }

    static IReadOnlyDictionary<string, IReadOnlyList<string>> Pair(string left, string right) =>
        new Dictionary<string, IReadOnlyList<string>> { ["Left"] = new[] { left }, ["Right"] = new[] { right } };

    static IReadOnlyDictionary<string, string> EmptyAttributes() => new Dictionary<string, string>();
}
