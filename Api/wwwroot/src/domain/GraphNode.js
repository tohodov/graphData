import { graphElementAttribute, graphKindAttribute, graphTypeNameAttribute } from "./graphAttributes.js";
import { GraphEdge } from "./GraphEdge.js";
import { GraphId } from "./GraphId.js";

export class GraphNode {
  constructor({ globalId, name, localId, displayName, attributes = {}, edges = [], collapsed = false }) {
    this.globalId = globalId ?? name;
    this.name = this.globalId;
    this.localId = localId ?? GraphId.localId(this.globalId);
    this.displayName = displayName ?? this.localId;
    this.attributes = { ...(attributes ?? {}) };
    this.edges = edges.map(edge => GraphEdge.from(edge));
    this.collapsed = Boolean(collapsed);
  }

  static fromApi(node) {
    return new GraphNode(node ?? {});
  }

  static from(node) {
    return node instanceof GraphNode ? node : new GraphNode(node ?? {});
  }

  merge(expansion) {
    const next = GraphNode.from(expansion);
    this.globalId = next.globalId;
    this.name = next.name;
    this.localId = next.localId;
    this.displayName = next.displayName;
    this.attributes = { ...next.attributes };
    this.edges = GraphEdge.mergeMany(this.edges, next.edges);
    this.collapsed = this.collapsed || next.collapsed;
    return this;
  }

  withAttributes(attributes) {
    this.attributes = { ...(attributes ?? {}) };
    return this;
  }

  attribute(key) {
    return this.attributes?.[key];
  }

  hasKind(kind) {
    return this.attribute(graphKindAttribute) === kind;
  }

  graphElement(fallback = "node") {
    return this.attribute(graphElementAttribute) ?? fallback;
  }

  assignedTypeGlobalId() {
    return this.attribute(graphTypeNameAttribute) ?? "";
  }

  isSchemaRoot(basis) {
    return this.globalId === basis.nodeTypeRoot
      || this.globalId === basis.edgeTypeRoot
      || this.globalId === basis.relationRoot;
  }

  isRelationInstance(basis) {
    if (this.hasKind("edge-instance")) {
      return true;
    }

    return this.attribute(graphElementAttribute) === "edge"
      && GraphId.isChildOf(this.name, basis.relationRoot)
      && !this.name.slice(basis.relationRoot.length + 1).includes("/");
  }

  isChildOf(parentGlobalId) {
    return GraphId.isChildOf(this.globalId, parentGlobalId);
  }

  edgeTo(globalId) {
    return this.edges.find(edge => edge.connects(globalId)) ?? null;
  }

  neighborIds() {
    return this.edges.map(edge => edge.otherEndpoint(this.globalId));
  }

  toViewNode(extra = {}) {
    return {
      name: this.name,
      globalId: this.globalId,
      localId: this.localId,
      displayName: this.displayName,
      attributes: { ...this.attributes },
      collapsed: this.collapsed,
      ...extra
    };
  }
}
