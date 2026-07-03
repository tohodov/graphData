import type { GraphNode } from "./GraphNode.js";

export type GraphEdgeSnapshot = {
  kind?: string | null;
  attributes?: Record<string, string>;
  node1InternalId?: string;
  sourceGlobalId?: string;
  node2InternalId?: string;
  targetGlobalId?: string;
  node1LocalId?: string | null;
  sourceLocalId?: string | null;
  node2LocalId?: string | null;
  targetLocalId?: string | null;
  neighborLocalId?: string | null;
  neighborLocalIdsByAnchor?: Record<string, string>;
  relationGlobalId?: string | null;
  typeGlobalId?: string | null;
  label?: string;
  color?: string;
  directed?: boolean;
  projected?: boolean;
  typeRank?: number | null;
  viewRank?: number | null;
  viewRankReason?: string;
  collapsed?: boolean;
  controlAnchorGlobalId?: string | null;
  controlAngle?: number | null;
  controlAnglesByAnchor?: Record<string, number>;
  node1?: GraphNode | null;
  sourceNode?: GraphNode | null;
  node2?: GraphNode | null;
  targetNode?: GraphNode | null;
  node1Positioned?: boolean;
  node2Positioned?: boolean;
};

export type GraphEdgeControlKind = "expand" | "collapse";
export type GraphEdgeControlAction = "load-neighbor" | "expand-edge" | "collapse-edge";

export type GraphEdgeControlSnapshot = {
  key?: string;
  action?: GraphEdgeControlAction;
  kind?: GraphEdgeControlKind;
  text?: string;
  title?: string;
  anchorName?: string;
  otherName?: string;
  neighborLocalId?: string | null;
  angle?: number | null;
};

export type GraphEdgeControlContext = {
  isCollapsed: boolean;
  displayName(globalId: string): string;
};

class GraphEdgeControl {
  key: string;
  action: GraphEdgeControlAction;
  kind: GraphEdgeControlKind;
  text: string;
  title: string;
  anchorName: string;
  otherName: string;
  neighborLocalId: string | null;
  angle: number | null;

  constructor({
    key = "",
    action = "collapse-edge",
    kind = "collapse",
    text = "-",
    title = "",
    anchorName = "",
    otherName = "",
    neighborLocalId = null,
    angle = null
  }: GraphEdgeControlSnapshot) {
    this.key = key;
    this.action = action;
    this.kind = kind;
    this.text = text;
    this.title = title;
    this.anchorName = anchorName;
    this.otherName = otherName;
    this.neighborLocalId = neighborLocalId;
    this.angle = Number.isFinite(angle) ? angle : null;
  }

  static loadNeighbor(edge: GraphEdge, anchorName: string, otherName: string, otherLabel: string): GraphEdgeControl {
    return new GraphEdgeControl({
      key: GraphEdgeControl.keyFor(edge, anchorName, "load-neighbor"),
      action: "load-neighbor",
      kind: "expand",
      text: "+",
      title: `Развернуть ${otherLabel}`,
      anchorName,
      otherName,
      neighborLocalId: edge.neighborLocalIdFor(anchorName),
      angle: edge.controlAngleFor(anchorName)
    });
  }

  static expandEdge(edge: GraphEdge, anchorName: string, otherName: string, otherLabel: string): GraphEdgeControl {
    return new GraphEdgeControl({
      key: GraphEdgeControl.keyFor(edge, anchorName, "expand-edge"),
      action: "expand-edge",
      kind: "expand",
      text: "+",
      title: `Развернуть связь с ${otherLabel}`,
      anchorName,
      otherName
    });
  }

  static collapseEdge(edge: GraphEdge, anchorName: string, otherName: string, otherLabel: string): GraphEdgeControl {
    return new GraphEdgeControl({
      key: GraphEdgeControl.keyFor(edge, anchorName, "collapse-edge"),
      action: "collapse-edge",
      kind: "collapse",
      text: "-",
      title: `Свернуть связь с ${otherLabel}`,
      anchorName,
      otherName
    });
  }

  private static keyFor(edge: GraphEdge, anchorName: string, action: GraphEdgeControlAction): string {
    return `${edge.key}\0${anchorName}\0${action}`;
  }
}

export class GraphEdge {
  kind: string | null;
  attributes: Record<string, string>;
  node1InternalId: string;
  node2InternalId: string;
  node1LocalId: string | null;
  node2LocalId: string | null;
  neighborLocalId: string | null;
  neighborLocalIdsByAnchor: Record<string, string>;
  relationGlobalId: string | null;
  typeGlobalId: string | null;
  label: string;
  color: string;
  directed: boolean;
  projected: boolean;
  typeRank: number | null;
  viewRank: number | null;
  viewRankReason: string;
  collapsed: boolean;
  controlAnchorGlobalId: string | null;
  controlAngle: number | null;
  controlAnglesByAnchor: Record<string, number>;
  node1: GraphNode | null;
  node2: GraphNode | null;
  node1Positioned: boolean;
  node2Positioned: boolean;
  key: string;
  constructor({
    kind = null,
    attributes = {},
    node1InternalId = "",
    sourceGlobalId = "",
    node2InternalId = "",
    targetGlobalId = "",
    node1LocalId = null,
    sourceLocalId = null,
    node2LocalId = null,
    targetLocalId = null,
    neighborLocalId = null,
    neighborLocalIdsByAnchor = {},
    relationGlobalId = null,
    typeGlobalId = null,
    label = "",
    color = "",
    directed = false,
    projected = false,
    typeRank = null,
    viewRank = null,
    viewRankReason = "",
    collapsed = false,
    controlAnchorGlobalId = null,
    controlAngle = null,
    controlAnglesByAnchor = {},
    node1 = null,
    sourceNode = null,
    node2 = null,
    targetNode = null,
    node1Positioned = false,
    node2Positioned = false
  }: GraphEdgeSnapshot) {
    this.kind = kind ?? null;
    this.attributes = { ...(attributes ?? {}) };
    this.node1InternalId = node1InternalId || sourceGlobalId;
    this.node2InternalId = node2InternalId || targetGlobalId;
    this.node1LocalId = node1LocalId || sourceLocalId;
    this.node2LocalId = node2LocalId || targetLocalId;
    this.neighborLocalId = neighborLocalId;
    this.neighborLocalIdsByAnchor = { ...(neighborLocalIdsByAnchor ?? {}) };
    this.relationGlobalId = relationGlobalId;
    this.typeGlobalId = typeGlobalId;
    this.label = label;
    this.color = color;
    this.directed = directed;
    this.projected = projected;
    this.typeRank = typeRank;
    this.viewRank = viewRank;
    this.viewRankReason = viewRankReason;
    this.collapsed = Boolean(collapsed);
    this.controlAnchorGlobalId = controlAnchorGlobalId;
    this.controlAngle = Number.isFinite(controlAngle) ? controlAngle : null;
    this.controlAnglesByAnchor = { ...(controlAnglesByAnchor ?? {}) };
    if (controlAnchorGlobalId && this.controlAngle !== null) {
      this.controlAnglesByAnchor[controlAnchorGlobalId] = this.controlAngle;
    }
    this.node1 = node1 ?? sourceNode ?? null;
    this.node2 = node2 ?? targetNode ?? null;
    this.node1Positioned = Boolean(node1Positioned);
    this.node2Positioned = Boolean(node2Positioned);
    this.key = GraphEdge.keyFor(this.node1InternalId, this.node2InternalId, relationGlobalId ?? typeGlobalId ?? kind ?? "");
  }

  static fromApi(edge: GraphEdgeSnapshot | null | undefined): GraphEdge {
    return new GraphEdge(edge ?? {});
  }

  static from(edge: GraphEdge | GraphEdgeSnapshot | null | undefined): GraphEdge {
    return edge instanceof GraphEdge ? edge : new GraphEdge(edge ?? {});
  }

  static keyFor(a: string, b: string, discriminator = ""): string {
    const endpoints = String(a).localeCompare(String(b), "ru") < 0
      ? String(a) + "\u0000" + String(b)
      : String(b) + "\u0000" + String(a);
    return discriminator ? endpoints + "\u0000" + discriminator : endpoints;
  }

  static mergeMany(left: Array<GraphEdge | GraphEdgeSnapshot> = [], right: Array<GraphEdge | GraphEdgeSnapshot> = []): GraphEdge[] {
    const edges = new Map();
    [...left, ...right].forEach(edge => {
      const normalized = GraphEdge.from(edge);
      if (normalized.node1InternalId && normalized.node2InternalId) {
        const existing = edges.get(normalized.key);
        if (existing?.collapsed && !normalized.collapsed) {
          normalized.collapsed = true;
        }
        if (existing && normalized.controlAngle === null && existing.controlAngle !== null) {
          normalized.controlAnchorGlobalId = existing.controlAnchorGlobalId;
          normalized.controlAngle = existing.controlAngle;
        }
        if (existing) {
          normalized.controlAnglesByAnchor = {
            ...existing.controlAnglesByAnchor,
            ...normalized.controlAnglesByAnchor
          };
          normalized.neighborLocalIdsByAnchor = {
            ...existing.neighborLocalIdsByAnchor,
            ...normalized.neighborLocalIdsByAnchor
          };
        }
        edges.set(normalized.key, normalized);
      }
    });
    return [...edges.values()];
  }

  connects(path: string): boolean {
    return this.node1InternalId === path || this.node2InternalId === path;
    
  }

  otherEndpoint(anchorPath: string): string {
    return this.node1InternalId === anchorPath ? this.node2InternalId : this.node1InternalId;
    
  }

  endpointDisplayName(path: string, displayName: (path: string) => string): string {
    if (this.node1InternalId === path) {
      return this.node1LocalId ?? displayName(path);
    }
    if (this.node2InternalId === path) {
      return this.node2LocalId ?? displayName(path);
    }
    return displayName(path);
  }

neighborLocalIdFor(anchorPath: string): string | null {
    const mapped = this.neighborLocalIdsByAnchor[anchorPath];
    if (mapped) {
      return mapped;
    }
    if (this.neighborLocalId) {
      return this.neighborLocalId;
    }
    return this.node1InternalId === anchorPath ? this.node2LocalId : this.node1LocalId;
  }

  setNeighborLocalIdFor(anchorPath: string, neighborLocalId: string | null): void {
    if (!anchorPath || !neighborLocalId || !this.connects(anchorPath)) {
      return;
    }

    this.neighborLocalIdsByAnchor[anchorPath] = neighborLocalId;
  }

endpointNode(path: string): GraphNode | null {
    if (this.node1InternalId === path) return this.node1;
    if (this.node2InternalId === path) return this.node2;
    return null;
  }

  endpointLoaded(path: string): boolean {
    return this.endpointNode(path) !== null;
  }

  endpointShowed(path: string): boolean {
    const node = this.endpointNode(path);
    return Boolean(node && node.showed === true);
  }

endpointPositioned(path: string): boolean {
    if (this.node1InternalId === path) return this.node1Positioned;
    if (this.node2InternalId === path) return this.node2Positioned;
    return false;
  }

  setControlAngle(anchorInternalId: string, angle: number | null): void {
    if (!anchorInternalId) {
      return;
    }

    if (Number.isFinite(angle)) {
      const finiteAngle = angle as number;
      this.controlAnglesByAnchor[anchorInternalId] = finiteAngle;
      this.controlAnchorGlobalId = anchorInternalId;
      this.controlAngle = finiteAngle;
    } else {
      delete this.controlAnglesByAnchor[anchorInternalId];
      if (this.controlAnchorGlobalId === anchorInternalId) {
        this.controlAnchorGlobalId = null;
        this.controlAngle = null;
      }
    }
  }

  clearControlAngle(): void {
    this.controlAnchorGlobalId = null;
    this.controlAngle = null;
    this.controlAnglesByAnchor = {};
  }

  controlAngleFor(anchorInternalId: string): number | null {
    const mapped = this.controlAnglesByAnchor[anchorInternalId];
    if (Number.isFinite(mapped)) {
      return mapped;
    }

    return this.controlAnchorGlobalId === anchorInternalId && Number.isFinite(this.controlAngle)
      ? this.controlAngle
      : null;
  }

  controls(context: GraphEdgeControlContext): GraphEdgeControlSnapshot[] {
    return [this.node1InternalId, this.node2InternalId]
      .map(anchorName => this.endpointControl(anchorName, context))
      .filter((control): control is GraphEdgeControl => control !== null);
  }

  endpointControl(anchorName: string, context: GraphEdgeControlContext): GraphEdgeControl | null {
    if (!anchorName || !this.connects(anchorName)) {
      return null;
    }

    const otherName = this.otherEndpoint(anchorName);
    const anchorShowed = this.endpointShowed(anchorName);
    const otherShowed = this.endpointShowed(otherName);
    const otherLabel = this.endpointDisplayName(otherName, context.displayName);

    if (anchorShowed && !otherShowed) {
      return GraphEdgeControl.loadNeighbor(this, anchorName, otherName, otherLabel);
    }

    if (anchorShowed && context.isCollapsed) {
      return GraphEdgeControl.expandEdge(this, anchorName, otherName, otherLabel);
    }

    if (anchorShowed && otherShowed) {
      return GraphEdgeControl.collapseEdge(this, anchorName, otherName, otherLabel);
    }

    return null;
  }

  toViewEdge(extra: Record<string, unknown> = {}): Record<string, unknown> {
    return {
      key: this.key,
      kind: this.kind,
      attributes: { ...this.attributes },
      node1InternalId: this.node1InternalId,
      node2InternalId: this.node2InternalId,
      node1LocalId: this.node1LocalId,
      node2LocalId: this.node2LocalId,
      neighborLocalId: this.neighborLocalId,
      neighborLocalIdsByAnchor: { ...this.neighborLocalIdsByAnchor },
      relationGlobalId: this.relationGlobalId,
      typeGlobalId: this.typeGlobalId,
      label: this.label,
      color: this.color,
      directed: this.directed,
      projected: this.projected,
      typeRank: this.typeRank,
      viewRank: this.viewRank,
      viewRankReason: this.viewRankReason,
      collapsed: this.collapsed,
      controlAnchorGlobalId: this.controlAnchorGlobalId,
      controlAngle: this.controlAngle,
      controlAnglesByAnchor: { ...this.controlAnglesByAnchor },
      node1: this.node1,
      node2: this.node2,
      node1Positioned: this.node1Positioned,
      node2Positioned: this.node2Positioned,
      ...extra
    };
  }
}
