export class GraphEdge {
  constructor({
    sourceGlobalId,
    targetGlobalId,
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
    viewRankReason = ""
  }) {
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
    this.key = GraphEdge.keyFor(sourceGlobalId, targetGlobalId, relationGlobalId ?? typeGlobalId ?? "");
  }

  static fromApi(edge) {
    return new GraphEdge(edge ?? {});
  }

  static from(edge) {
    return edge instanceof GraphEdge ? edge : new GraphEdge(edge ?? {});
  }

  static keyFor(a, b, discriminator = "") {
    const endpoints = String(a).localeCompare(String(b), "ru") < 0
      ? String(a) + "\u0000" + String(b)
      : String(b) + "\u0000" + String(a);
    return discriminator ? endpoints + "\u0000" + discriminator : endpoints;
  }

  static mergeMany(left = [], right = []) {
    const edges = new Map();
    [...left, ...right].forEach(edge => {
      const normalized = GraphEdge.from(edge);
      if (normalized.sourceGlobalId && normalized.targetGlobalId) {
        edges.set(normalized.key, normalized);
      }
    });
    return [...edges.values()];
  }

  connects(globalId) {
    return this.sourceGlobalId === globalId || this.targetGlobalId === globalId;
  }

  otherEndpoint(anchorGlobalId) {
    return this.sourceGlobalId === anchorGlobalId ? this.targetGlobalId : this.sourceGlobalId;
  }

  endpointDisplayName(globalId, displayName) {
    if (this.sourceGlobalId === globalId) {
      return this.sourceLocalId ?? displayName(globalId);
    }
    if (this.targetGlobalId === globalId) {
      return this.targetLocalId ?? displayName(globalId);
    }
    return displayName(globalId);
  }

  neighborLocalIdFor(anchorGlobalId) {
    if (this.neighborLocalId) {
      return this.neighborLocalId;
    }

    return this.sourceGlobalId === anchorGlobalId ? this.targetLocalId : this.sourceLocalId;
  }

  toViewEdge(extra = {}) {
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
      ...extra
    };
  }
}
