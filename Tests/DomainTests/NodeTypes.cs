using Abstractions;
using GraphData.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.GraphService;

public sealed class NodeTypes {

    public abstract class EquipmentNodeType : NodeType {
        public string SerialNumber = "";

        internal EquipmentNodeType(CarrierNodeBacking state) : base(state) {
        }
    }

    public sealed class RifleNodeType : EquipmentNodeType {
        public string Caliber = "";

        internal RifleNodeType(CarrierNodeBacking state) : base(state) {
        }
    }

    public sealed class WeaponNodeType : NodeType {
        public ManufacturerNodeType Manufacturer = null!;

        internal WeaponNodeType(CarrierNodeBacking state) : base(state) {
        }
    }

    public sealed class CountryNodeType : NodeType {
        internal CountryNodeType(CarrierNodeBacking state) : base(state) {
        }
    }

    public sealed class ManufacturerNodeType : NodeType {
        public CountryNodeType Country = null!;
        public ManufacturerNodeType? ParentCompany = null;
        public IReadOnlyCollection<WeaponNodeType> ProducedWeapons = [];
        public CarrierNode Headquarters = null!;
        public CarrierNode? ArchiveNode = null;
        public string LegalName = "";
        public int FoundedYear = 0;
        public bool IsActive = false;
        public decimal? AnnualRevenueUsd = null;
        public string? Website = null;
        public IReadOnlyCollection<string> Aliases = [];

        internal ManufacturerNodeType(CarrierNodeBacking state) : base(state) {
        }
    }
    public sealed class EdgeWeaponNodeType : NodeType {
        internal EdgeWeaponNodeType(CarrierNodeBacking state) : base(state) {
        }
    }

    public sealed class EdgeManufacturerNodeType : NodeType {
        internal EdgeManufacturerNodeType(CarrierNodeBacking state) : base(state) {
        }
    }

    public sealed class ManufacturedByEdge : CarrierEdge {
        public EdgeWeaponNodeType Weapon = null!;
        public EdgeManufacturerNodeType Manufacturer = null!;

        internal ManufacturedByEdge(CarrierEdgeBacking state) : base(state) {
        }
    }

    public sealed class ShipmentEdge : CarrierEdge {
        public EdgeWeaponNodeType Weapon = null!;
        public CarrierNode Counterparty = null!;
        public CarrierNode? OptionalWaypoint = null;
        public IReadOnlyCollection<EdgeManufacturerNodeType> Manufacturers = [];
        public string Note = "";

        internal ShipmentEdge(CarrierEdgeBacking state) : base(state) {
        }
    }
}
