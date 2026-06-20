import { graphElementAttribute, graphKindAttribute, graphTypeNameAttribute } from "./graphAttributes.js";
import { GraphEdge } from "./GraphEdge.js";
import { GraphId } from "./GraphId.js";

export type GraphPoint = { x: number; y: number };

function normalizePoint(value: GraphPoint | null | undefined): GraphPoint | null {
  return value && Number.isFinite(value.x) && Number.isFinite(value.y)
    ? { x: value.x, y: value.y }
    : null;
}

export class GraphNode {
  globalId: string;
  name: string;
  localId: string;
  displayName: string;
  attributes: Record<string, string>;
  edges: GraphEdge[];
  collapsed: boolean;
  showed: boolean;
  position: GraphPoint | null;
  constructor({
    globalId,
    name,
    localId,
    displayName,
    attributes = {},
    edges = [],
    collapsed = false,
    showed = true,
    position = null
  }: {
    globalId?: string;
    name?: string;
    localId?: string;
    displayName?: string;
    attributes?: Record<string, string>;
    edges?: any[];
    collapsed?: boolean;
    showed?: boolean;
    position?: GraphPoint | null;
  }) {
    this.globalId = globalId ?? name;
    this.name = this.globalId;
    this.localId = localId ?? GraphId.localId(this.globalId);
    this.displayName = displayName ?? this.localId;
    this.attributes = { ...(attributes ?? {}) };
    this.edges = edges.map(edge => GraphEdge.from(edge));
    this.collapsed = Boolean(collapsed);
    this.showed = showed !== false;
    this.position = normalizePoint(position);
  }

  static fromApi(node: any): GraphNode {
    return new GraphNode(node ?? {});
  }

  static from(node: GraphNode | any): GraphNode {
    return node instanceof GraphNode ? node : new GraphNode(node ?? {});
  }

  merge(expansion: GraphNode | any): GraphNode {
    const next = GraphNode.from(expansion);
    this.globalId = next.globalId;
    this.name = next.name;
    this.localId = next.localId;
    this.displayName = next.displayName;
    this.attributes = { ...next.attributes };
    this.edges = GraphEdge.mergeMany(this.edges, next.edges);
    this.collapsed = this.collapsed || next.collapsed;
    this.showed = this.showed || next.showed;
    this.position = next.position ?? this.position;
    return this;
  }

  hasPosition(): boolean {
    return this.position !== null;
  }

  setPosition(position: GraphPoint): GraphNode {
    this.position = normalizePoint(position);
    return this;
  }

  clearPosition(): GraphNode {
    this.position = null;
    return this;
  }

  withAttributes(attributes: Record<string, string>): GraphNode {
    this.attributes = { ...(attributes ?? {}) };
    return this;
  }

  attribute(key: string): string | undefined {
    return this.attributes?.[key];
  }

  hasKind(kind: string): boolean {
    return this.attribute(graphKindAttribute) === kind;
  }

  graphElement(fallback = "node"): string {
    return this.attribute(graphElementAttribute) ?? fallback;
  }

  assignedTypeGlobalId(): string {
    return this.attribute(graphTypeNameAttribute) ?? "";
  }

  isSchemaRoot(basis: { nodeTypeRoot: string; edgeTypeRoot: string; relationRoot: string }): boolean {
    return this.globalId === basis.nodeTypeRoot
      || this.globalId === basis.edgeTypeRoot
      || this.globalId === basis.relationRoot;
  }

  isRelationInstance(basis: { relationRoot: string }): boolean {
    return this.hasKind("edge-instance")
      || (this.attribute(graphElementAttribute) === "edge" && Boolean(this.attribute(graphTypeNameAttribute)));
  }

  isChildOf(parentGlobalId: string): boolean {
    return GraphId.isChildOf(this.globalId, parentGlobalId);
  }

  edgeTo(globalId: string): GraphEdge | null {
    return this.edges.find(edge => edge.connects(globalId)) ?? null;
  }

  neighborIds(): string[] {
    return this.edges.map(edge => edge.otherEndpoint(this.globalId));
  }

  toViewNode(extra: Record<string, unknown> = {}): Record<string, unknown> {
    return {
      name: this.name,
      globalId: this.globalId,
      localId: this.localId,
      displayName: this.displayName,
      attributes: { ...this.attributes },
      collapsed: this.collapsed,
      showed: this.showed,
      position: this.position ? { ...this.position } : null,
      ...extra
    };
  }
}
