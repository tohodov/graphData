import type { GraphNode } from "./GraphNode.js";

export type GraphEdgeSnapshot = {
  sourceGlobalId?: string;
  targetGlobalId?: string;
  sourceLocalId?: string | null;
  targetLocalId?: string | null;
  neighborLocalId?: string | null;
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
  sourceNode?: GraphNode | null;
  targetNode?: GraphNode | null;
  sourcePositioned?: boolean;
  targetPositioned?: boolean;
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
  sourceGlobalId: string;
  targetGlobalId: string;
  sourceLocalId: string | null;
  targetLocalId: string | null;
  neighborLocalId: string | null;
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
  sourceNode: GraphNode | null;
  targetNode: GraphNode | null;
  sourcePositioned: boolean;
  targetPositioned: boolean;
  key: string;
  constructor({
    sourceGlobalId = "",
    targetGlobalId = "",
    sourceLocalId = null,
    targetLocalId = null,
    neighborLocalId = null,
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
    sourceNode = null,
    targetNode = null,
    sourcePositioned = false,
    targetPositioned = false
  }: GraphEdgeSnapshot) {
    this.sourceGlobalId = sourceGlobalId;
    this.targetGlobalId = targetGlobalId;
    this.sourceLocalId = sourceLocalId;
    this.targetLocalId = targetLocalId;
    this.neighborLocalId = neighborLocalId;
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
    this.sourceNode = sourceNode ?? null;
    this.targetNode = targetNode ?? null;
    this.sourcePositioned = Boolean(sourcePositioned);
    this.targetPositioned = Boolean(targetPositioned);
    this.key = GraphEdge.keyFor(sourceGlobalId, targetGlobalId, relationGlobalId ?? typeGlobalId ?? "");
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
      if (normalized.sourceGlobalId && normalized.targetGlobalId) {
        const existing = edges.get(normalized.key);
        if (existing?.collapsed && !normalized.collapsed) {
          normalized.collapsed = true;
        }
        if (existing && normalized.controlAngle === null && existing.controlAngle !== null) {
          normalized.controlAnchorGlobalId = existing.controlAnchorGlobalId;
          normalized.controlAngle = existing.controlAngle;
        }
        edges.set(normalized.key, normalized);
      }
    });
    return [...edges.values()];
  }

  connects(globalId: string): boolean {
    return this.sourceGlobalId === globalId || this.targetGlobalId === globalId;
  }

  otherEndpoint(anchorGlobalId: string): string {
    return this.sourceGlobalId === anchorGlobalId ? this.targetGlobalId : this.sourceGlobalId;
  }

  endpointDisplayName(globalId: string, displayName: (globalId: string) => string): string {
    if (this.sourceGlobalId === globalId) {
      return this.sourceLocalId ?? displayName(globalId);
    }
    if (this.targetGlobalId === globalId) {
      return this.targetLocalId ?? displayName(globalId);
    }
    return displayName(globalId);
  }

  neighborLocalIdFor(anchorGlobalId: string): string | null {
    if (this.neighborLocalId) {
      return this.neighborLocalId;
    }

    return this.sourceGlobalId === anchorGlobalId ? this.targetLocalId : this.sourceLocalId;
  }

  endpointNode(globalId: string): GraphNode | null {
    if (this.sourceGlobalId === globalId) {
      return this.sourceNode;
    }
    if (this.targetGlobalId === globalId) {
      return this.targetNode;
    }
    return null;
  }

  endpointLoaded(globalId: string): boolean {
    return this.endpointNode(globalId) !== null;
  }

  endpointShowed(globalId: string): boolean {
    const node = this.endpointNode(globalId);
    return Boolean(node && node.showed !== false);
  }

  endpointPositioned(globalId: string): boolean {
    if (this.sourceGlobalId === globalId) {
      return this.sourcePositioned;
    }
    if (this.targetGlobalId === globalId) {
      return this.targetPositioned;
    }
    return false;
  }

  setControlAngle(anchorGlobalId: string, angle: number | null): void {
    this.controlAnchorGlobalId = anchorGlobalId;
    this.controlAngle = Number.isFinite(angle) ? angle : null;
  }

  clearControlAngle(): void {
    this.controlAnchorGlobalId = null;
    this.controlAngle = null;
  }

  controlAngleFor(anchorGlobalId: string): number | null {
    return this.controlAnchorGlobalId === anchorGlobalId && Number.isFinite(this.controlAngle)
      ? this.controlAngle
      : null;
  }

  controls(context: GraphEdgeControlContext): GraphEdgeControlSnapshot[] {
    return [this.sourceGlobalId, this.targetGlobalId]
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
      sourceGlobalId: this.sourceGlobalId,
      targetGlobalId: this.targetGlobalId,
      sourceLocalId: this.sourceLocalId,
      targetLocalId: this.targetLocalId,
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
      sourceNode: this.sourceNode,
      targetNode: this.targetNode,
      sourcePositioned: this.sourcePositioned,
      targetPositioned: this.targetPositioned,
      ...extra
    };
  }
}
