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
  frontierAnchorGlobalId?: string | null;
  frontierAngle?: number | null;
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
  isEndpointLoaded(globalId: string): boolean;
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
      angle: edge.frontierAngleFor(anchorName)
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
  frontierAnchorGlobalId: string | null;
  frontierAngle: number | null;
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
    frontierAnchorGlobalId = null,
    frontierAngle = null
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
    this.frontierAnchorGlobalId = frontierAnchorGlobalId;
    this.frontierAngle = Number.isFinite(frontierAngle) ? frontierAngle : null;
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
        if (existing && normalized.frontierAngle === null && existing.frontierAngle !== null) {
          normalized.frontierAnchorGlobalId = existing.frontierAnchorGlobalId;
          normalized.frontierAngle = existing.frontierAngle;
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

  setFrontierAngle(anchorGlobalId: string, angle: number | null): void {
    this.frontierAnchorGlobalId = anchorGlobalId;
    this.frontierAngle = Number.isFinite(angle) ? angle : null;
  }

  clearFrontierAngle(): void {
    this.frontierAnchorGlobalId = null;
    this.frontierAngle = null;
  }

  frontierAngleFor(anchorGlobalId: string): number | null {
    return this.frontierAnchorGlobalId === anchorGlobalId && Number.isFinite(this.frontierAngle)
      ? this.frontierAngle
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
    const anchorLoaded = context.isEndpointLoaded(anchorName);
    const otherLoaded = context.isEndpointLoaded(otherName);
    const otherLabel = this.endpointDisplayName(otherName, context.displayName);

    if (anchorLoaded && !otherLoaded) {
      return GraphEdgeControl.loadNeighbor(this, anchorName, otherName, otherLabel);
    }

    if (anchorLoaded && context.isCollapsed) {
      return GraphEdgeControl.expandEdge(this, anchorName, otherName, otherLabel);
    }

    if (anchorLoaded && otherLoaded) {
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
      frontierAnchorGlobalId: this.frontierAnchorGlobalId,
      frontierAngle: this.frontierAngle,
      ...extra
    };
  }
}
