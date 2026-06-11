import {
  graphElementAttribute,
  projectionColorAttribute,
  projectionDirectedAttribute,
  projectionInfoAttribute,
  projectionLabelVisibleAttribute,
  projectionRankAttribute,
  projectionVisibleAttribute
} from "./graphAttributes.js";

export class GraphType {
  constructor(options) {
    this.globalId = options.globalId;
    this.localId = options.localId;
    this.label = options.label ?? options.localId;
    this.color = GraphType.normalizeColor(options.color);
    this.element = options.element ?? "node";
    this.visible = options.visible ?? true;
    this.infoAttribute = options.infoAttribute ?? "";
    this.labelVisible = options.labelVisible ?? true;
    this.directed = options.directed ?? false;
    this.rank = options.rank ?? 0;
    this.attributes = { ...(options.attributes ?? {}) };
  }

  static fromNode(node, fallbackElement = "node") {
    const fallbackRank = fallbackElement === "edge" ? 30 : 50;
    return new GraphType({
      globalId: node.globalId,
      localId: node.localId,
      label: node.attributes?.label || node.localId,
      color: node.attributes?.[projectionColorAttribute] || node.attributes?.color,
      element: node.attributes?.[graphElementAttribute] || fallbackElement,
      visible: String(node.attributes?.[projectionVisibleAttribute] ?? "true").toLowerCase() !== "false",
      infoAttribute: node.attributes?.[projectionInfoAttribute] || "",
      labelVisible: String(node.attributes?.[projectionLabelVisibleAttribute] ?? "true").toLowerCase() !== "false",
      directed: String(node.attributes?.[projectionDirectedAttribute] ?? node.attributes?.directed ?? "").toLowerCase() === "true",
      rank: GraphType.readRank(node.attributes?.[projectionRankAttribute] ?? node.attributes?.rank, fallbackRank),
      attributes: node.attributes ?? {}
    });
  }

  static readRank(value, fallback = 0) {
    const parsed = Number.parseFloat(String(value));
    return Number.isFinite(parsed) ? Math.max(0, parsed) : fallback;
  }

  static roundRank(value) {
    return Math.round(value * 10) / 10;
  }

  static formatRank(value) {
    return GraphType.roundRank(value).toFixed(1);
  }

  static formatRankInput(value) {
    return Number.isFinite(value) ? String(GraphType.roundRank(value)) : "";
  }

  static rankToRadius(rank) {
    return Math.round(Math.max(28, Math.min(48, 34 + (rank - 55) * 0.14)));
  }

  static normalizeColor(value) {
    if (!value || !/^#[0-9a-f]{6}$/i.test(String(value).trim())) {
      return "";
    }

    return String(value).trim();
  }
}
