import {
  graphElementAttribute,
  nodeRadius,
  projectionCollapsedAttribute,
  projectionColorAttribute,
  projectionDirectedAttribute,
  projectionInfoAttribute,
  projectionLabelVisibleAttribute,
  projectionRankAttribute,
  projectionVisibleAttribute
} from "./graphAttributes.js";

export type GraphTypeSnapshot = {
  globalId?: string;
  localId?: string;
  label?: string;
  color?: string;
  element?: string;
  visible?: boolean;
  collapsed?: boolean;
  infoAttribute?: string;
  labelVisible?: boolean;
  directed?: boolean;
  rank?: number;
  attributes?: Record<string, string>;
};

export class GraphType {
  globalId: string;
  localId: string;
  label: string;
  color: string;
  element: string;
  visible: boolean;
  collapsed: boolean;
  infoAttribute: string;
  labelVisible: boolean;
  directed: boolean;
  rank: number;
  attributes: Record<string, string>;
  constructor(options: GraphTypeSnapshot) {
    this.globalId = options.globalId ?? "";
    this.localId = options.localId ?? "";
    this.label = options.label ?? options.localId ?? "";
    this.color = GraphType.normalizeColor(options.color);
    this.element = options.element ?? "node";
    this.visible = options.visible ?? true;
    this.collapsed = options.collapsed ?? false;
    this.infoAttribute = options.infoAttribute ?? "";
    this.labelVisible = options.labelVisible ?? true;
    this.directed = options.directed ?? false;
    this.rank = options.rank ?? 0;
    this.attributes = { ...(options.attributes ?? {}) };
  }

  static fromNode(node: import("./GraphNode.js").GraphNodeSnapshot, fallbackElement = "node"): GraphType {
    const fallbackRank = fallbackElement === "edge" ? 30 : 50;
    return new GraphType({
      globalId: node.globalId,
      localId: node.localId,
      label: node.attributes?.label || node.localId,
      color: node.attributes?.[projectionColorAttribute] || node.attributes?.color,
      element: node.attributes?.[graphElementAttribute] || fallbackElement,
      visible: String(node.attributes?.[projectionVisibleAttribute] ?? "true").toLowerCase() !== "false",
      collapsed: String(node.attributes?.[projectionCollapsedAttribute] ?? "false").toLowerCase() === "true",
      infoAttribute: node.attributes?.[projectionInfoAttribute] || "",
      labelVisible: String(node.attributes?.[projectionLabelVisibleAttribute] ?? "true").toLowerCase() !== "false",
      directed: String(node.attributes?.[projectionDirectedAttribute] ?? node.attributes?.directed ?? "").toLowerCase() === "true",
      rank: GraphType.readRank(node.attributes?.[projectionRankAttribute] ?? node.attributes?.rank, fallbackRank),
      attributes: node.attributes ?? {}
    });
  }

  static readRank(value: unknown, fallback = 0): number {
    const parsed = Number.parseFloat(String(value));
    return Number.isFinite(parsed) ? Math.max(0, parsed) : fallback;
  }

  static roundRank(value: number): number {
    return Math.round(value * 10) / 10;
  }

  static formatRank(value: number): string {
    return GraphType.roundRank(value).toFixed(1);
  }

  static formatRankInput(value: number): string {
    return Number.isFinite(value) ? String(GraphType.roundRank(value)) : "";
  }

  static rankToRadius(rank: number): number {
    const radius = nodeRadius + (rank - 55) * 0.28;
    return Math.round(Math.max(nodeRadius - 12, Math.min(nodeRadius + 28, radius)));
  }

  static normalizeColor(value: unknown): string {
    if (!value || !/^#[0-9a-f]{6}$/i.test(String(value).trim())) {
      return "";
    }

    return String(value).trim();
  }
}
