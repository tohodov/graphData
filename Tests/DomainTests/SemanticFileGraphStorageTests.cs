using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SemanticStorage;

[RelevantTestClass]
public sealed class SemanticFileGraphStorageTests : GraphStorageContractTests
{
    [TestMethod]
    public async Task CanonicalRecords_AreSeparatedIntoTypeAndInstanceStores()
    {
        var instance = await Storage.Create(
            "ak-47",
            attributes: new Dictionary<string, string> { ["title"] = "AK-47" });
        var semantic = (ISemanticGraphStorage)Storage;
        var type = await Storage.Create("Weapon", semantic.TypeRoot.GlobalId);

        Assert.IsTrue(File.Exists(Path.Combine(
            StorageOptions.RootPath,
            "instances",
            instance.LocalId,
            "instance.json")));
        Assert.IsTrue(File.Exists(Path.Combine(
            StorageOptions.RootPath,
            "types",
            type.LocalId,
            "type.json")));
        Assert.IsTrue(File.Exists(Path.Combine(
            StorageOptions.RootPath,
            "indexes",
            "incidents",
            instance.LocalId,
            "relations.json")));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(
            StorageOptions.RootPath,
            "*",
            SearchOption.AllDirectories).Any(static path =>
                File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)));
    }

    [TestMethod]
    public async Task ExplicitTypeKind_DoesNotDependOnInfrastructureLikeLocalNames()
    {
        var semantic = (ISemanticGraphStorage)Storage;
        var container = await semantic.CreateTypeElementAsync(
            "Container",
            semantic.TypeRoot.GlobalId,
            SemanticElementKind.Type,
            Array.Empty<NodeRef>());
        var actualType = await semantic.CreateTypeElementAsync(
            "Definition",
            container.GlobalId,
            SemanticElementKind.Type,
            Array.Empty<NodeRef>());
        var infrastructure = await semantic.CreateTypeElementAsync(
            "DomainType",
            container.GlobalId,
            SemanticElementKind.TypeInfrastructure,
            Array.Empty<NodeRef>());
        var compatibilityDefault = await Storage.Create("Fields", container.GlobalId);

        Assert.IsTrue(semantic.IsType(actualType));
        Assert.AreEqual(
            SemanticElementKind.Type,
            ((ISemanticNodeBacking)actualType).SemanticKind);
        Assert.IsFalse(semantic.IsType(infrastructure));
        Assert.AreEqual(
            SemanticElementKind.TypeInfrastructure,
            ((ISemanticNodeBacking)infrastructure).SemanticKind);
        Assert.IsTrue(
            semantic.IsType(compatibilityDefault),
            "Generic compatibility creation below the type tree must default to an actual Type, never infer infrastructure from its name.");
    }

    [TestMethod]
    public async Task TypeInfrastructure_CannotDeclareRequiredTypes()
    {
        var semantic = (ISemanticGraphStorage)Storage;
        var type = await semantic.CreateTypeElementAsync(
            "BaseType",
            semantic.TypeRoot.GlobalId,
            SemanticElementKind.Type,
            Array.Empty<NodeRef>());

        var createError = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            semantic.CreateTypeElementAsync(
                "Definition",
                type.GlobalId,
                SemanticElementKind.TypeInfrastructure,
                [type.GlobalId]));
        StringAssert.Contains(createError.Message, "cannot declare required types");

        var infrastructure = await semantic.CreateTypeElementAsync(
            "Definition",
            type.GlobalId,
            SemanticElementKind.TypeInfrastructure,
            Array.Empty<NodeRef>());
        var setError = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            semantic.SetRequiredTypeIdsAsync(infrastructure.GlobalId, [type.GlobalId]));
        StringAssert.Contains(setError.Message, "cannot declare required types");
    }

    [TestMethod]
    public async Task LegacyTypeReferenceSetters_DropEmptyRootIds()
    {
        var semantic = (ISemanticGraphStorage)Storage;
        var instance = await Storage.Create("empty-instance-ref");
        var type = await semantic.CreateTypeElementAsync(
            "EmptyRequiredRef",
            semantic.TypeRoot.GlobalId,
            SemanticElementKind.Type,
            Array.Empty<NodeRef>());

        await semantic.SetDeclaredTypeIdsAsync(instance.GlobalId, [new NodePath()]);
        await semantic.SetRequiredTypeIdsAsync(type.GlobalId, [new NodePath()]);

        Assert.AreEqual(0, (await semantic.ReadSemanticElementAsync(instance.GlobalId))!.TypeIds.Count);
        Assert.AreEqual(0, (await semantic.ReadSemanticElementAsync(type.GlobalId))!.RequiredTypeIds.Count);
    }

    [TestMethod]
    public async Task ReplaceInstance_UpdatesAttributesAndWeakTypesInOneCanonicalRecord()
    {
        var semantic = (ISemanticGraphStorage)Storage;
        var instance = await semantic.CreateInstanceAsync(
            "subject",
            namespacePrefix: null,
            [new NodePath("NodeTypes", "OldMissingType")],
            new Dictionary<string, string> { ["state"] = "old" });

        await semantic.ReplaceInstanceAsync(
            instance.GlobalId,
            [new NodePath("NodeTypes", "NewMissingType")],
            new Dictionary<string, string> { ["state"] = "new" });

        var reread = await Storage.Get(instance.GlobalId);
        Assert.IsNotNull(reread);
        Assert.AreEqual("new", reread.Attributes["state"]);
        CollectionAssert.AreEqual(
            new[] { "NodeTypes/NewMissingType" },
            semantic.GetDeclaredTypeIds(reread)
                .Select(static id => id.ToString())
                .ToArray());
    }

    [TestMethod]
    public async Task TypeRef_IsStoredLocallyAndMayRemainDangling()
    {
        var instance = await Storage.Create("ak-47");
        var semantic = (ISemanticGraphStorage)Storage;
        var missingType = new NodePath("NodeTypes", "MissingWeaponType");

        await semantic.SetDeclaredTypeIdsAsync(instance.GlobalId, [missingType]);

        var reopened = NewStorage();
        var reread = await reopened.Get(instance.GlobalId);
        Assert.IsNotNull(reread);
        CollectionAssert.AreEqual(
            new[] { "NodeTypes/MissingWeaponType" },
            ((ISemanticGraphStorage)reopened).GetDeclaredTypeIds(reread)
                .Select(static id => id.ToString())
                .ToArray());
    }

    [TestMethod]
    public async Task RelationRecord_LocalizesAllNamedParticipantsAndBuildsIncidentIndex()
    {
        var semantic = (ISemanticGraphStorage)Storage;
        var source = await Storage.Create("factory");
        var first = await Storage.Create("rifle-1");
        var second = await Storage.Create("rifle-2");
        var type = await Storage.Create("Shipment", semantic.TypeRoot.GlobalId);

        var relation = await semantic.CreateRelationAsync(
            "shipment-42",
            type.GlobalId,
            [
                new SemanticRelationMember("Source", new InternalId(new NodeLocalId("source-role")), [source.GlobalId]),
                new SemanticRelationMember("Cargo", new InternalId(new NodeLocalId("cargo-role")), [first.GlobalId, second.GlobalId])
            ]);

        var relationPath = Path.Combine(
            StorageOptions.RootPath,
            "instances",
            relation.LocalId,
            "instance.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(relationPath));
        Assert.AreEqual("relation", document.RootElement.GetProperty("kind").GetString());
        Assert.AreEqual(2, document.RootElement.GetProperty("members").GetArrayLength());
        CollectionAssert.AreEquivalent(
            new[] { "rifle-1", "rifle-2" },
            document.RootElement.GetProperty("members")[1]
                .GetProperty("participantIds")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());

        Assert.IsTrue(File.Exists(Path.Combine(
            StorageOptions.RootPath,
            "indexes",
            "incidents",
            first.LocalId,
            "relations.json")));
        var incident = await semantic.FindIncidentRelationsAsync(first.GlobalId).ToArrayAsync();
        Assert.AreEqual(relation.GlobalId, incident.Single().Id);
    }

    [TestMethod]
    public async Task ExplicitRepair_RebuildsDeletedDerivedIncidentIndex()
    {
        var semantic = (ISemanticGraphStorage)Storage;
        var left = await Storage.Create("left");
        var right = await Storage.Create("right");
        var type = await Storage.Create("Connection", semantic.TypeRoot.GlobalId);
        var relation = await semantic.CreateRelationAsync(
            "left-to-right",
            type.GlobalId,
            [
                new SemanticRelationMember("Left", new InternalId(new NodeLocalId("left-role")), [left.GlobalId]),
                new SemanticRelationMember("Right", new InternalId(new NodeLocalId("right-role")), [right.GlobalId])
            ]);

        Directory.Delete(Path.Combine(StorageOptions.RootPath, "indexes"), recursive: true);

        var reopened = NewStorage();
        var reopenedSemantic = (ISemanticGraphStorage)reopened;
        Assert.AreEqual(0, await reopenedSemantic
            .FindIncidentRelationsAsync(left.GlobalId)
            .CountAsync());

        await reopenedSemantic.RepairDerivedIndexesAsync();
        var matches = await reopenedSemantic
            .FindIncidentRelationsAsync(left.GlobalId)
            .ToArrayAsync();
        Assert.AreEqual(relation.GlobalId, matches.Single().Id);
    }

    [TestMethod]
    public async Task MissingIncidentMarker_BlocksDestructiveDeleteUntilExplicitRepair()
    {
        var instance = await Storage.Create("repair-before-delete");
        File.Delete(Path.Combine(
            StorageOptions.RootPath,
            "indexes",
            "incidents",
            instance.LocalId,
            "relations.json"));

        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => Storage.Delete(instance.GlobalId));

        StringAssert.Contains(error.Message, "RepairDerivedIndexesAsync");
        Assert.IsNotNull(await Storage.Get(instance.GlobalId));
        await ((ISemanticGraphStorage)Storage).RepairDerivedIndexesAsync();
        await Storage.Delete(instance.GlobalId);
        Assert.IsNull(await Storage.Get(instance.GlobalId));
    }

    [TestMethod]
    public async Task StaleIncidentEntry_CannotCascadeDeleteUnrelatedCanonicalRelation()
    {
        var semantic = (ISemanticGraphStorage)Storage;
        var victim = await Storage.Create("stale-index-victim");
        var left = await Storage.Create("stale-index-left");
        var right = await Storage.Create("stale-index-right");
        var type = await Storage.Create("StaleIndexRelation", semantic.TypeRoot.GlobalId);
        var relation = await semantic.CreateRelationAsync(
            "unrelated-relation",
            type.GlobalId,
            [
                new SemanticRelationMember("Left", new InternalId(new NodeLocalId("left-role")), [left.GlobalId]),
                new SemanticRelationMember("Right", new InternalId(new NodeLocalId("right-role")), [right.GlobalId])
            ]);
        var indexRoot = Path.Combine(StorageOptions.RootPath, "indexes", "incidents");
        var staleIndex = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(indexRoot, left.LocalId, "relations.json")))!.AsObject();
        staleIndex["participantId"] = victim.GlobalId.ToString();
        await File.WriteAllTextAsync(
            Path.Combine(indexRoot, victim.LocalId, "relations.json"),
            staleIndex.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        Assert.AreEqual(0, await semantic.FindIncidentRelationsAsync(victim.GlobalId).CountAsync());
        await Storage.Delete(victim.GlobalId);

        Assert.IsNotNull(await Storage.Get(relation.GlobalId));
        Assert.IsNotNull(await Storage.Get(left.GlobalId));
        Assert.IsNotNull(await Storage.Get(right.GlobalId));
    }

    [TestMethod]
    public async Task InstanceNamespace_AllowsMissingLocatorPrefixesAndSurfacesNearestRecord()
    {
        var semantic = (ISemanticGraphStorage)Storage;
        var leaf = await Storage.Create("leaf", new NodePath("missing", "branch"));

        Assert.AreEqual("missing/branch/leaf", leaf.GlobalId.ToString());
        Assert.IsNull(await Storage.Get(new NodePath("missing")));
        Assert.IsNull(await Storage.Get(new NodePath("missing", "branch")));
        CollectionAssert.AreEqual(
            new[] { leaf.GlobalId },
            await semantic.EnumerateInstanceNamespaceChildrenAsync()
                .Select(static node => node.GlobalId)
                .ToArrayAsync());

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            Storage.Create("LeafType", new NodePath("NodeTypes", "MissingTypeParent")));
    }

    [TestMethod]
    public async Task TypeNeighbors_DoNotScanOrExposeReverseInstanceBacklinks()
    {
        var semantic = (ISemanticGraphStorage)Storage;
        var type = await Storage.Create("LocalType", semantic.TypeRoot.GlobalId);
        var instance = await Storage.Create("typed-instance");
        await semantic.SetDeclaredTypeIdsAsync(instance.GlobalId, [type.GlobalId]);

        Assert.IsFalse(await type.Nodes.AnyAsync(node => node.GlobalId == instance.GlobalId));
        Assert.IsTrue(await instance.Nodes.AnyAsync(node => node.GlobalId == type.GlobalId));
    }

    [TestMethod]
    public async Task VirtualRelationMembers_UseANameThatCanonicalAddressesReject()
    {
        var semantic = (ISemanticGraphStorage)Storage;
        var participant = await Storage.Create("member-collision-participant");
        var type = await Storage.Create("MemberCollisionRelation", semantic.TypeRoot.GlobalId);
        var relation = await semantic.CreateRelationAsync(
            "member-collision",
            type.GlobalId,
            [new SemanticRelationMember("Subject", new InternalId(new NodeLocalId("subject-role")), [participant.GlobalId])]);

        var memberId = new NodeLocalId("@member-member-collision-1");
        var projectedMember = await Storage.Get(relation.GlobalId, memberId);
        Assert.IsNotNull(projectedMember);
        Assert.AreEqual(memberId, projectedMember.LocalId);
        CollectionAssert.AreEqual(
            new[] { new InternalId(new NodeLocalId("subject-role")) },
            ((ISemanticNodeBacking)projectedMember).DeclaredTypeIds.ToArray());
        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            Storage.Create(memberId, relation.GlobalId));

        // The formerly ambiguous spelling remains available to canonical
        // instances and cannot shadow the virtual '@member-' projection.
        var ordinaryChild = await Storage.Create("member-member-collision-1", relation.GlobalId);
        Assert.AreNotEqual(projectedMember.GlobalId, ordinaryChild.GlobalId);
    }

    [TestMethod]
    public async Task ReplaceRelation_IsAtomicForTypesMembersAndHigherOrderIdentity()
    {
        var semantic = (ISemanticGraphStorage)Storage;
        var first = await Storage.Create("replace-first");
        var second = await Storage.Create("replace-second");
        var replacementParticipant = await Storage.Create("replace-third");
        var oldType = await Storage.Create("OldRelationType", semantic.TypeRoot.GlobalId);
        var newType = await Storage.Create("NewRelationType", semantic.TypeRoot.GlobalId);
        var relation = await semantic.CreateRelationAsync(
            "replace-relation",
            [oldType.GlobalId],
            [
                new SemanticRelationMember("First", new InternalId(new NodeLocalId("first-role")), [first.GlobalId]),
                new SemanticRelationMember("Second", new InternalId(new NodeLocalId("second-role")), [second.GlobalId])
            ],
            new Dictionary<string, string> { ["revision"] = "old" });
        var annotation = await semantic.CreateRelationAsync(
            "replace-annotation",
            oldType.GlobalId,
            [new SemanticRelationMember("Subject", new InternalId(new NodeLocalId("subject-role")), [relation.GlobalId])]);

        await semantic.ReplaceRelationAsync(
            relation.GlobalId,
            [newType.GlobalId],
            [
                new SemanticRelationMember("Current", new InternalId(new NodeLocalId("current-role")), [replacementParticipant.GlobalId]),
                new SemanticRelationMember("Optional", new InternalId(new NodeLocalId("optional-role")), [])
            ],
            new Dictionary<string, string> { ["revision"] = "new" });

        var replaced = await semantic.GetRelationAsync(relation.GlobalId);
        Assert.IsNotNull(replaced);
        CollectionAssert.AreEqual(new[] { newType.GlobalId }, replaced.TypeIds.ToArray());
        Assert.AreEqual(0, replaced.Members.Single(static member => member.Name == "Optional").ParticipantIds.Count);
        Assert.AreEqual(
            replacementParticipant.GlobalId,
            replaced.Members.Single(static member => member.Name == "Current").ParticipantIds.Single());
        Assert.AreEqual(0, await semantic.FindIncidentRelationsAsync(first.GlobalId).CountAsync());
        Assert.AreEqual(relation.GlobalId, (await semantic
            .FindIncidentRelationsAsync(replacementParticipant.GlobalId)
            .ToArrayAsync()).Single().Id);
        Assert.AreEqual(annotation.GlobalId, (await semantic
            .FindIncidentRelationsAsync(relation.GlobalId)
            .ToArrayAsync()).Single().Id);
        Assert.AreEqual("new", (await Storage.Get(relation.GlobalId))!.Attributes["revision"]);
    }

    [TestMethod]
    public async Task ReplaceRelation_RejectsDirectHigherOrderSelfCycle()
    {
        var semantic = (ISemanticGraphStorage)Storage;
        var participant = await Storage.Create("self-cycle-participant");
        var type = await Storage.Create("SelfCycleRelation", semantic.TypeRoot.GlobalId);
        var relation = await semantic.CreateRelationAsync(
            "self-cycle-relation",
            type.GlobalId,
            [new SemanticRelationMember("Subject", new InternalId(new NodeLocalId("subject-role")), [participant.GlobalId])]);

        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            semantic.ReplaceRelationAsync(
                relation.GlobalId,
                [type.GlobalId],
                [new SemanticRelationMember("Subject", new InternalId(new NodeLocalId("subject-role")), [relation.GlobalId])]));

        StringAssert.Contains(error.Message, "dependency cycle");
        Assert.AreEqual(
            participant.GlobalId,
            (await semantic.GetRelationAsync(relation.GlobalId))!.Members.Single().ParticipantIds.Single());
    }

    [TestMethod]
    public async Task ReplaceRelation_RejectsMutualHigherOrderCycle()
    {
        var semantic = (ISemanticGraphStorage)Storage;
        var participant = await Storage.Create("mutual-cycle-participant");
        var type = await Storage.Create("MutualCycleRelation", semantic.TypeRoot.GlobalId);
        var first = await semantic.CreateRelationAsync(
            "mutual-cycle-first",
            type.GlobalId,
            [new SemanticRelationMember("Subject", new InternalId(new NodeLocalId("subject-role")), [participant.GlobalId])]);
        var second = await semantic.CreateRelationAsync(
            "mutual-cycle-second",
            type.GlobalId,
            [new SemanticRelationMember("Subject", new InternalId(new NodeLocalId("subject-role")), [first.GlobalId])]);

        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            semantic.ReplaceRelationAsync(
                first.GlobalId,
                [type.GlobalId],
                [new SemanticRelationMember("Subject", new InternalId(new NodeLocalId("subject-role")), [second.GlobalId])]));

        StringAssert.Contains(error.Message, "dependency cycle");
        Assert.AreEqual(
            participant.GlobalId,
            (await semantic.GetRelationAsync(first.GlobalId))!.Members.Single().ParticipantIds.Single());
    }

    [TestMethod]
    public async Task CanonicalRead_DoesNotAliasDifferentlyCasedPhysicalAddress()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var canonical = await Storage.Create("CaseSensitiveId");

        Assert.IsNull(await Storage.Get(new NodePath("casesensitiveid")));
        Assert.IsNotNull(await Storage.Get(canonical.GlobalId));
    }

    [TestMethod]
    public async Task CanonicalRead_RejectsUnknownOrNonCanonicalKind()
    {
        var instance = await Storage.Create("broken-kind");
        var path = Path.Combine(
            StorageOptions.RootPath,
            StorageOptions.InstancesDirectoryName,
            instance.LocalId,
            SemanticFileGraphStorage.InstanceRecordFileName);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path, Encoding.UTF8))!.AsObject();
        json["kind"] = "Instance";
        await File.WriteAllTextAsync(path, json.ToJsonString(), Encoding.UTF8);

        var error = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => Storage.Get(instance.GlobalId));

        StringAssert.Contains(error.Message, "invalid kind");
    }

    [TestMethod]
    public async Task RelationMayParticipateInHigherOrderRelation()
    {
        var semantic = (ISemanticGraphStorage)Storage;
        var first = await Storage.Create("first");
        var second = await Storage.Create("second");
        var type = await Storage.Create("RelationType", semantic.TypeRoot.GlobalId);
        var relation = await semantic.CreateRelationAsync(
            "first-relation",
            type.GlobalId,
            [new SemanticRelationMember("Items", new InternalId(new NodeLocalId("items-role")), [first.GlobalId, second.GlobalId])]);
        var annotation = await semantic.CreateRelationAsync(
            "annotation",
            type.GlobalId,
            [new SemanticRelationMember("Subject", new InternalId(new NodeLocalId("subject-role")), [relation.GlobalId])]);

        var matches = await semantic.FindIncidentRelationsAsync(relation.GlobalId).ToArrayAsync();
        Assert.AreEqual(annotation.GlobalId, matches.Single().Id);
    }

    [TestMethod]
    public async Task RelationRejectsTypeCatalogParticipant()
    {
        var semantic = (ISemanticGraphStorage)Storage;
        var relationType = await Storage.Create("RelationType", semantic.TypeRoot.GlobalId);

        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            semantic.CreateRelationAsync(
                "invalid-relation",
                relationType.GlobalId,
                [
                    new SemanticRelationMember(
                        "Subject",
                        new InternalId(new NodeLocalId("subject-role")),
                        [relationType.GlobalId])
                ]));

        StringAssert.Contains(error.Message, "outside the instance graph");
        Assert.IsNull(await semantic.GetRelationAsync(new NodePath("invalid-relation")));
    }

    private static IGraphStorage NewStorage() => new SemanticFileGraphStorage(
        Options.Create(StorageOptions),
        new CancellationTokensAccessorMock());
}
