import { graphElementAttribute, graphKindAttribute, graphTypeNameAttribute } from "./graphAttributes.js";
import { GraphEdge, type GraphEdgeSnapshot } from "./GraphEdge.js";
import { GraphId } from "./GraphId.js";

export type GraphPoint = { x: number; y: number };

function normalizePoint(value: GraphPoint | null | undefined): GraphPoint | null {
  return value && Number.isFinite(value.x) && Number.isFinite(value.y)
    ? { x: value.x, y: value.y }
    : null;
}

export type GraphNodeSnapshot = {
  internalId?: string;
  path?: string;
  name?: string;
  localId?: string;
  displayName?: string;
  attributes?: Record<string, string>;
  edges?: Array<GraphEdge | GraphEdgeSnapshot>;
  nodes?: Array<GraphNode | GraphNodeSnapshot>;
  collapsed?: boolean;
  showed?: boolean | undefined;
  position?: GraphPoint | null;
  // Backward compatibility
  globalId?: string;
};

export class GraphNode {
  internalId: string;
  path: string;
  name: string;
  localId: string;
  displayName: string;
  attributes: Record<string, string>;
  edges: GraphEdge[];
  nodes: GraphNode[];
  collapsed: boolean;
  showed: boolean | undefined;
  position: GraphPoint | null;
  constructor({
    internalId,
    path,
    globalId,
    name,
    localId,
    displayName,
    attributes = {},
    edges = [],
    nodes = [],
    collapsed = false,
    showed,
    position = null
  }: GraphNodeSnapshot) {
    this.internalId = internalId ?? globalId ?? name ?? "";
    this.path = path ?? globalId ?? name ?? "";
    this.name = this.path;
    this.localId = localId ?? GraphId.localId(this.path);
    this.displayName = displayName ?? this.localId;
    this.attributes = { ...(attributes ?? {}) };
    this.edges = edges.map(edge => GraphEdge.from(edge));
    this.nodes = nodes.map(node => GraphNode.from(node));
    this.collapsed = Boolean(collapsed);
    this.showed = showed;
    this.position = normalizePoint(position);
  }

  static fromApi(node: GraphNodeSnapshot | null | undefined): GraphNode {
    return new GraphNode(node ?? {});
  }

  static from(node: GraphNode | GraphNodeSnapshot | null | undefined): GraphNode {
    return node instanceof GraphNode ? node : new GraphNode(node ?? {});
  }

  merge(expansion: GraphNode | GraphNodeSnapshot | null | undefined): GraphNode {
    const next = GraphNode.from(expansion);
    this.internalId = next.internalId;
    this.path = next.path;
    this.name = next.name;
    this.localId = next.localId;
    this.displayName = next.displayName;
    this.attributes = { ...next.attributes };
    this.edges = GraphEdge.mergeMany(this.edges, next.edges);
    // TODO: GraphNode.mergeMany(this.nodes, next.nodes)? For now just replace or concat?
    this.nodes = next.nodes.length > 0 ? next.nodes : this.nodes;
    this.collapsed = this.collapsed || next.collapsed;
    this.showed = this.showed === true || next.showed === true ? true : (this.showed === false || next.showed === false ? false : undefined);
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

  assignedTypePath(): string {
    return this.attribute(graphTypeNameAttribute) ?? "";
  }

  isSchemaRoot(basis: { nodeTypeRoot: string; edgeTypeRoot: string; relationRoot: string }): boolean {
    return this.path === basis.nodeTypeRoot
      || this.path === basis.edgeTypeRoot
      || this.path === basis.relationRoot;
  }

  isRelationInstance(basis: { relationRoot: string }): boolean {
    return this.hasKind("edge-instance")
      || (this.attribute(graphElementAttribute) === "edge" && Boolean(this.attribute(graphTypeNameAttribute)));
  }

  isChildOf(parentPath: string): boolean {
    return GraphId.isChildOf(this.path, parentPath);
  }

  edgeTo(path: string): GraphEdge | null {
    return this.edges.find(edge => edge.connects(path)) ?? null;
  }

  neighborIds(): string[] {
    return this.edges.map(edge => edge.otherEndpoint(this.path));
  }

  toViewNode(extra: Record<string, unknown> = {}): Record<string, unknown> {
    return {
      name: this.name,
      internalId: this.internalId,
      path: this.path,
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
