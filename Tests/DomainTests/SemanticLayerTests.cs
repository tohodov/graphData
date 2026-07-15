using System.Reflection;
using System.Text.Json;
using Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[RelevantTestClass]
public sealed class SemanticLayerTests : GraphServiceTests
{
    [TestMethod]
    public async Task TypedEdgeDefinition_DoesNotInferEdgeFromNodeFields()
    {
        var nodeType = (await Service.CreateNodeType(
            "TwoReferencesAreStillNodeType",
            fields: [Endpoint("First"), Endpoint("Second")])).Value!;

        Assert.IsFalse(TypedEdgeDefinition.TryCreate(nodeType, out _));
        var edgeType = await Service.CreateEdgeType(
            "TwoReferencesAreEdgeType",
            fields: [Endpoint("First"), Endpoint("Second")]);

        Assert.AreEqual(ServiceResultStatus.Ok, edgeType.Status, edgeType.Error);
        Assert.IsTrue(TypedEdgeDefinition.TryCreate(edgeType.Value!, out _));
    }

    [TestMethod]
    public async Task AssignNodeType_PersistsLocalTypeRefsIdempotentlyWithoutCarrierWitnesses()
    {
        var weapon = (await Service.CreateNodeType("SemanticWeapon")).Value!.Type;
        var selectable = (await Service.CreateNodeType("SemanticSelectable")).Value!.Type;
        var ak47 = (await Service.CreateNode("semantic-ak47")).Value!;

        Assert.AreEqual(ServiceResultStatus.Ok, (await Service.AssignNodeTypeAsync(ak47.GlobalId, weapon.GlobalId)).Status);
        Assert.AreEqual(ServiceResultStatus.Ok, (await Service.AssignNodeTypeAsync(ak47.GlobalId, selectable.GlobalId)).Status);
        Assert.AreEqual(ServiceResultStatus.Ok, (await Service.AssignNodeTypeAsync(ak47.GlobalId, weapon.GlobalId)).Status);

        var semantic = (await Service.GetSemanticNodeAsync(ak47.GlobalId)).Value!;
        CollectionAssert.AreEquivalent(
            new[] { weapon.GlobalId, selectable.GlobalId },
            semantic.DeclaredTypes.Select(static type => type.GlobalId).ToArray());
        Assert.IsTrue(semantic.TypeInstances.All(static instance => !instance.IsMaterialized));

        var metadataPath = Path.Combine(
            StorageOptions.RootPath,
            "instances",
            ak47.LocalId,
            "instance.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath));
        CollectionAssert.AreEquivalent(
            new[] { weapon.GlobalId.ToString(), selectable.GlobalId.ToString() },
            document.RootElement.GetProperty("typeIds")
                .EnumerateArray()
                .Select(static value => value.GetString())
                .ToArray());
        Assert.IsFalse(Directory.EnumerateFiles(
                StorageOptions.RootPath,
                "instance-of-*",
                SearchOption.AllDirectories)
            .Any());
    }

    [TestMethod]
    public async Task RequiredTypes_AreEffectiveButOnlyLeafIsDeclared_AfterReopenAndCycle()
    {
        var root = (await Service.CreateNodeType("SemanticRoot")).Value!.Type;
        var left = (await Service.CreateNodeType("SemanticLeft", requiredTypes: [root])).Value!.Type;
        var right = (await Service.CreateNodeType("SemanticRight", requiredTypes: [root])).Value!.Type;
        var leaf = (await Service.CreateNodeType("SemanticLeaf", requiredTypes: [left, right])).Value!.Type;
        Assert.AreEqual(
            ServiceResultStatus.Ok,
            (await Service.AddNodeTypeRequirementAsync(root.GlobalId, leaf.GlobalId)).Status);
        var instance = (await Service.CreateNode("semantic-cycle-instance")).Value!;

        var assignment = await Service.AssignNodeTypeAsync(instance.GlobalId, leaf.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, assignment.Status, assignment.Error);

        var expectedEffective = new[] { root.GlobalId, left.GlobalId, right.GlobalId, leaf.GlobalId };
        var first = (await Service.GetSemanticNodeAsync(instance.GlobalId)).Value!;
        CollectionAssert.AreEqual(new[] { leaf.GlobalId }, first.DeclaredTypes.Select(static type => type.GlobalId).ToArray());
        CollectionAssert.AreEquivalent(expectedEffective, first.AssignedTypes.Select(static type => type.GlobalId).ToArray());

        var reopenedGraph = await global::Graph.OpenAsync(Storage, GraphSchemaRegistry.Create());
        var reopenedService = new GraphService(reopenedGraph, new GraphSearchService(Storage));
        var reopened = await reopenedService.GetSemanticNodeAsync(instance.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, reopened.Status, reopened.Error);
        CollectionAssert.AreEqual(new[] { leaf.GlobalId }, reopened.Value!.DeclaredTypes.Select(static type => type.GlobalId).ToArray());
        CollectionAssert.AreEquivalent(expectedEffective, reopened.Value.AssignedTypes.Select(static type => type.GlobalId).ToArray());
    }

    [TestMethod]
    public async Task AddingRequirement_ChangesEffectiveClosureWithoutRewritingInstances()
    {
        var required = (await Service.CreateNodeType("LateRequired")).Value!.Type;
        var derived = (await Service.CreateNodeType("LateDerived")).Value!.Type;
        var node = (await Service.CreateNode("late-required-instance")).Value!;
        Assert.AreEqual(ServiceResultStatus.Ok, (await Service.AssignNodeTypeAsync(node.GlobalId, derived.GlobalId)).Status);

        Assert.AreEqual(
            ServiceResultStatus.Ok,
            (await Service.AddNodeTypeRequirementAsync(derived.GlobalId, required.GlobalId)).Status);

        var semantic = (await Service.GetSemanticNodeAsync(node.GlobalId)).Value!;
        CollectionAssert.AreEqual(new[] { derived.GlobalId }, semantic.DeclaredTypes.Select(static type => type.GlobalId).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { derived.GlobalId, required.GlobalId },
            semantic.AssignedTypes.Select(static type => type.GlobalId).ToArray());
    }

    [TestMethod]
    public async Task AssignNodeType_RejectsDirectAbstractTypeButIncludesItInEffectiveClosure()
    {
        var abstractBase = (await Service.CreateNodeType("SemanticAbstractBase", isAbstract: true)).Value!.Type;
        var concrete = (await Service.CreateNodeType(
            "SemanticConcrete",
            requiredTypes: [abstractBase])).Value!.Type;
        var node = (await Service.CreateNode("semantic-concrete-instance")).Value!;

        var invalid = await Service.AssignNodeTypeAsync(node.GlobalId, abstractBase.GlobalId);
        Assert.AreEqual(ServiceResultStatus.BadRequest, invalid.Status);

        var valid = await Service.AssignNodeTypeAsync(node.GlobalId, concrete.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, valid.Status, valid.Error);
        var semantic = (await Service.GetSemanticNodeAsync(node.GlobalId)).Value!;
        CollectionAssert.AreEqual(new[] { concrete.GlobalId }, semantic.DeclaredTypes.Select(static type => type.GlobalId).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { abstractBase.GlobalId, concrete.GlobalId },
            semantic.AssignedTypes.Select(static type => type.GlobalId).ToArray());
    }

    [TestMethod]
    public async Task AssignNodeType_EnforcesTypeElementKindAndTargetSemanticKind()
    {
        var nodeType = (await Service.CreateNodeType("ClassifiableInstance")).Value!.Type;
        var edgeType = (await Service.CreateEdgeType(
            "ClassifiableRelation",
            fields: [Endpoint("Source"), Endpoint("Target")])).Value!.Type;
        var otherEdgeType = (await Service.CreateEdgeType(
            "OtherClassifiableRelation",
            fields: [Endpoint("Source"), Endpoint("Target")])).Value!.Type;
        var instance = (await Service.CreateNode("kind-target-instance")).Value!;
        var source = (await Service.CreateNode("kind-source")).Value!;
        var target = (await Service.CreateNode("kind-target")).Value!;
        var relation = (await Service.CreateRelationAsync(
            "kind-relation",
            edgeType.GlobalId,
            new Dictionary<string, IReadOnlyCollection<NodeRef>> {
                ["Source"] = [source.GlobalId],
                ["Target"] = [target.GlobalId]
            })).Value!.Relation;

        var nodeOnInstance = await Service.AssignNodeTypeAsync(instance.GlobalId, nodeType.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, nodeOnInstance.Status, nodeOnInstance.Error);

        var edgeOnRelation = await Service.AssignNodeTypeAsync(relation.GlobalId, edgeType.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, edgeOnRelation.Status, edgeOnRelation.Error);

        var secondClassifier = await Service.AssignNodeTypeAsync(relation.GlobalId, otherEdgeType.GlobalId);
        Assert.AreEqual(ServiceResultStatus.BadRequest, secondClassifier.Status);
        StringAssert.Contains(secondClassifier.Error, "exactly one concrete Edge classifier");

        var edgeOnInstance = await Service.AssignNodeTypeAsync(instance.GlobalId, edgeType.GlobalId);
        Assert.AreEqual(ServiceResultStatus.BadRequest, edgeOnInstance.Status);
        StringAssert.Contains(edgeOnInstance.Error, "semantic Relation");

        var nodeOnRelation = await Service.AssignNodeTypeAsync(relation.GlobalId, nodeType.GlobalId);
        Assert.AreEqual(ServiceResultStatus.BadRequest, nodeOnRelation.Status);
        StringAssert.Contains(nodeOnRelation.Error, "semantic Instance");

        var nodeOnType = await Service.AssignNodeTypeAsync(nodeType.GlobalId, nodeType.GlobalId);
        Assert.AreEqual(ServiceResultStatus.BadRequest, nodeOnType.Status);
        StringAssert.Contains(nodeOnType.Error, "semantic Instance");

        CollectionAssert.AreEqual(
            new[] { nodeType.GlobalId },
            ((ISemanticNodeBacking)instance.Backing).DeclaredTypeIds.ToArray());
        CollectionAssert.AreEqual(
            new[] { edgeType.GlobalId },
            ((ISemanticNodeBacking)relation.Backing).DeclaredTypeIds.ToArray());
    }

    [TestMethod]
    public async Task TypeCatalog_DoesNotConfuseReservedLocalIdsWithInfrastructure()
    {
        var reservedNames = new[] { "Definition", "Fields", "Slots" };
        var created = new List<NodeTypeDefinition>();
        foreach (var name in reservedNames) {
            var result = await Service.CreateNodeType(name);
            Assert.AreEqual(ServiceResultStatus.Ok, result.Status, result.Error);
            created.Add(result.Value!);
        }

        foreach (var definition in created)
            Assert.AreEqual(
                SemanticElementKind.Type,
                ((ISemanticNodeBacking)definition.Type.Backing).SemanticKind);

        var listed = await Service.GetTypeDefinitionsAsync();
        Assert.AreEqual(ServiceResultStatus.Ok, listed.Status, listed.Error);
        CollectionAssert.IsSubsetOf(
            created.Select(static definition => definition.Type.GlobalId).ToArray(),
            listed.Value!.Select(static definition => definition.Type.GlobalId).ToArray());
    }

    [TestMethod]
    public async Task NeighborLocalIdMayBeAmbiguousWhileGlobalIdsRemainExact()
    {
        var anchor = (await Service.CreateNode("duplicate-local-anchor")).Value!;
        var leftNamespace = (await Service.CreateNode("duplicate-local-left")).Value!;
        var rightNamespace = (await Service.CreateNode("duplicate-local-right")).Value!;
        var left = (await Service.CreateNode("shared", leftNamespace.GlobalId)).Value!;
        var right = (await Service.CreateNode("shared", rightNamespace.GlobalId)).Value!;
        Assert.AreEqual(
            ServiceResultStatus.Ok,
            (await Service.ConnectNodesAsync(anchor.GlobalId, left.GlobalId)).Status);
        Assert.AreEqual(
            ServiceResultStatus.Ok,
            (await Service.ConnectNodesAsync(anchor.GlobalId, right.GlobalId)).Status);

        var ambiguous = await Service.GetNode(anchor.GlobalId, "shared");
        Assert.AreEqual(ServiceResultStatus.BadRequest, ambiguous.Status);
        StringAssert.Contains(ambiguous.Error, "more than one graph neighbor");

        var exactLeft = await Service.GetNode(left.GlobalId);
        var exactRight = await Service.GetNode(right.GlobalId);
        Assert.AreEqual(left.GlobalId, exactLeft.Value!.GlobalId);
        Assert.AreEqual(right.GlobalId, exactRight.Value!.GlobalId);
    }

    [TestMethod]
    public async Task CreateRelation_StoresNaryMembersAndAllowsParallelRelations()
    {
        var shipmentType = (await Service.CreateEdgeType(
            "Shipment",
            fields: [
                Endpoint("Source"),
                new NodeFieldDefinition(
                    "Cargo",
                    NodeFieldValueKind.Node,
                    typeof(Node),
                    NodeSlotCardinality.Many(1),
                    IsCollection: true)
            ])).Value!.Type;
        var source = (await Service.CreateNode("factory")).Value!;
        var first = (await Service.CreateNode("rifle-1")).Value!;
        var second = (await Service.CreateNode("rifle-2")).Value!;
        var members = new Dictionary<string, IReadOnlyCollection<NodeRef>>(StringComparer.Ordinal) {
            ["Source"] = [source.GlobalId],
            ["Cargo"] = [first.GlobalId, second.GlobalId]
        };

        var firstRelation = await Service.CreateRelationAsync("shipment-1", shipmentType.GlobalId, members);
        var secondRelation = await Service.CreateRelationAsync("shipment-2", shipmentType.GlobalId, members);

        Assert.AreEqual(ServiceResultStatus.Ok, firstRelation.Status, firstRelation.Error);
        Assert.AreEqual(ServiceResultStatus.Ok, secondRelation.Status, secondRelation.Error);
        Assert.AreEqual(2, firstRelation.Value!.Endpoints.Count);
        CollectionAssert.AreEquivalent(
            new[] { first.GlobalId, second.GlobalId },
            firstRelation.Value.Endpoint("Cargo").Participants.Select(static node => node.GlobalId).ToArray());
        Assert.AreNotEqual(firstRelation.Value.Relation.GlobalId, secondRelation.Value!.Relation.GlobalId);

        var canonical = await ((ISemanticGraphStorage)Storage)
            .GetRelationAsync(firstRelation.Value.Relation.GlobalId);
        Assert.IsNotNull(canonical);
        Assert.AreEqual(2, canonical.Members.Count);
        Assert.AreEqual(2, canonical.Members.Single(static member => member.Name == "Cargo").ParticipantIds.Count);
    }

    [TestMethod]
    public async Task RelationMayBeParticipantAndDeletingParticipantRemovesIncidentRelations()
    {
        var edgeType = (await Service.CreateEdgeType(
            "SemanticAnnotation",
            fields: [Endpoint("Subject"), Endpoint("Context")])).Value!.Type;
        var first = (await Service.CreateNode("first")).Value!;
        var second = (await Service.CreateNode("second")).Value!;
        var baseRelation = (await Service.CreateRelationAsync(
            "base-relation",
            edgeType.GlobalId,
            new Dictionary<string, IReadOnlyCollection<NodeRef>> {
                ["Subject"] = [first.GlobalId],
                ["Context"] = [second.GlobalId]
            })).Value!;
        var annotation = (await Service.CreateRelationAsync(
            "higher-order-relation",
            edgeType.GlobalId,
            new Dictionary<string, IReadOnlyCollection<NodeRef>> {
                ["Subject"] = [baseRelation.Relation.GlobalId],
                ["Context"] = [second.GlobalId]
            })).Value!;

        var incident = await ((ISemanticGraphStorage)Storage)
            .FindIncidentRelationsAsync(baseRelation.Relation.GlobalId)
            .ToArrayAsync();
        Assert.AreEqual(annotation.Relation.GlobalId, incident.Single().Id);

        Assert.AreEqual(ServiceResultStatus.Ok, (await Service.DeleteNode(second.GlobalId)).Status);
        Assert.IsNull(await Storage.Get(baseRelation.Relation.GlobalId));
        Assert.IsNull(await Storage.Get(annotation.Relation.GlobalId));
    }

    [TestMethod]
    public async Task InstanceRemainsReadableWhenTypeCatalogEntryIsUnavailable()
    {
        var type = (await Service.CreateNodeType("TemporarilyUnavailableType")).Value!.Type;
        var node = (await Service.CreateNode("locally-readable-instance")).Value!;
        Assert.AreEqual(ServiceResultStatus.Ok, (await Service.AssignNodeTypeAsync(node.GlobalId, type.GlobalId)).Status);

        await Storage.Delete(type.Backing);

        var raw = await Service.GetNode(node.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, raw.Status, raw.Error);
        CollectionAssert.AreEqual(
            new[] { type.GlobalId },
            raw.Value!.DeclaredTypeIds.ToArray());
        var semantic = await Service.GetSemanticNodeAsync(node.GlobalId);
        Assert.AreEqual(ServiceResultStatus.Ok, semantic.Status, semantic.Error);
        Assert.AreEqual(0, semantic.Value!.AssignedTypes.Count);
    }

    [TestMethod]
    public void SemanticRead_HasNoArbitraryBasisParameter()
    {
        var method = typeof(GraphService).GetMethod(
            nameof(GraphService.GetSemanticNodeAsync),
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: [typeof(NodeRef)],
            modifiers: null);

        Assert.IsNotNull(method);
        Assert.AreEqual(1, method.GetParameters().Length);
    }

    private static NodeFieldDefinition Endpoint(string name) => new(
        name,
        NodeFieldValueKind.Node,
        typeof(Node),
        NodeSlotCardinality.Required(),
        IsCollection: false);
}
