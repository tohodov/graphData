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
