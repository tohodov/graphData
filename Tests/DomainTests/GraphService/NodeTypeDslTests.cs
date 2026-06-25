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
