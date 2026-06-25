using Abstractions;
using GraphData.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.GraphService;

public sealed class NodeTypeDslTests : StorageTests {

    public static void AssertField(
        NodeTypeDefinition definition,
        string name,
        NodeFieldValueKind kind,
        Type clrType,
        NodeSlotCardinality cardinality,
        InternalId? nodeTypeId = null,
        bool isCollection = false) {
        var field = definition.Fields.Single(value => value.Name == name);
        Assert.AreEqual(kind, field.ValueKind);
        Assert.AreEqual(clrType, field.ClrType);
        Assert.AreEqual(cardinality, field.Cardinality);
        Assert.AreEqual(nodeTypeId, field.NodeTypeId);
        Assert.AreEqual(isCollection, field.IsCollection);
    }

    private static async Task CreatePathAsync(IGraphStorage storage, InternalId id) {
        var segments = id.ToArray();
        NodePath? parent = null;
        for (var index = 0; index < segments.Length; index++) {
            var current = new InternalId(segments.Take(index + 1));
            var existing = await storage.Get(current);
            if (existing.Status == ServiceResultStatus.NotFound) {
                var create = await storage.Create(segments[index], parent);
                Assert.AreEqual(ServiceResultStatus.Ok, create.Status, create.Error);
            }

            parent = current;
        }
    }

    private static async Task<InternalId> GetNodeTypeIdAsync<TNodeType>(GraphData.Core.Services.GraphService graph)
        where TNodeType : NodeType {
        var result = await graph.GetNodeTypeDefinitionAsync<TNodeType>();
        Assert.AreEqual(ServiceResultStatus.Ok, result.Status, result.Error);
        return result.Value!.Type.GlobalId;
    }

    public sealed class WeaponNodeType : NodeType {
        public ManufacturerNodeType Manufacturer = null!;

        internal WeaponNodeType(NodeState state) : base(state) {
        }
    }

    public sealed class CountryNodeType : NodeType {
        internal CountryNodeType(NodeState state) : base(state) {
        }
    }

    public sealed class ManufacturerNodeType : NodeType {
        public CountryNodeType Country = null!;
        public ManufacturerNodeType? ParentCompany = null;
        public IReadOnlyCollection<WeaponNodeType> ProducedWeapons = [];
        public Node Headquarters = null!;
        public Node? ArchiveNode = null;
        public string LegalName = "";
        public int FoundedYear = 0;
        public bool IsActive = false;
        public decimal? AnnualRevenueUsd = null;
        public string? Website = null;
        public IReadOnlyCollection<string> Aliases = [];

        internal ManufacturerNodeType(NodeState state) : base(state) {
        }
    }
}
