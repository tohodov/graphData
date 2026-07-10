using Abstractions;
using GraphData.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.GraphService;

public sealed class NodeTypes {

    public abstract class EquipmentNodeType : NodeType {
        public string SerialNumber = "";

        internal EquipmentNodeType(NodeBacking state) : base(state) {
        }
    }

    public sealed class RifleNodeType : EquipmentNodeType {
        public string Caliber = "";

        internal RifleNodeType(NodeBacking state) : base(state) {
        }
    }

    public sealed class WeaponNodeType : NodeType {
        public ManufacturerNodeType Manufacturer = null!;

        internal WeaponNodeType(NodeBacking state) : base(state) {
        }
    }

    public sealed class CountryNodeType : NodeType {
        internal CountryNodeType(NodeBacking state) : base(state) {
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

        internal ManufacturerNodeType(NodeBacking state) : base(state) {
        }
    }
    public sealed class EdgeWeaponNodeType : NodeType {
        internal EdgeWeaponNodeType(NodeBacking state) : base(state) {
        }
    }

    public sealed class EdgeManufacturerNodeType : NodeType {
        internal EdgeManufacturerNodeType(NodeBacking state) : base(state) {
        }
    }

    public sealed class ManufacturedByConnectionNodeType : NodeType {
        public EdgeWeaponNodeType Weapon = null!;
        public EdgeManufacturerNodeType Manufacturer = null!;

        internal ManufacturedByConnectionNodeType(NodeBacking state) : base(state) {
        }
    }

    public sealed class ShipmentConnectionNodeType : NodeType {
        public EdgeWeaponNodeType Weapon = null!;
        public Node Counterparty = null!;
        public Node? OptionalWaypoint = null;
        public IReadOnlyCollection<EdgeManufacturerNodeType> Manufacturers = [];
        public string Note = "";

        internal ShipmentConnectionNodeType(NodeBacking state) : base(state) {
        }
    }
}
