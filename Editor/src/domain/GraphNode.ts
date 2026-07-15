import { GraphEdge, type GraphEdgeSnapshot } from "./GraphEdge.js";
import { GraphId } from "./GraphId.js";

export type GraphPoint = { x: number; y: number };

export type GraphRelationMemberSnapshot = {
  name?: string;
  memberTypeId?: string;
  participantIds?: string[];
};

export type GraphRelationMember = {
  name: string;
  memberTypeId: string;
  participantIds: string[];
};

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
  semanticKind?: string;
  typeIds?: string[];
  relationMembers?: GraphRelationMemberSnapshot[];
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
  semanticKind: string;
  typeIds: string[];
  relationMembers: GraphRelationMember[];
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
    semanticKind = "",
    typeIds = [],
    relationMembers = [],
    edges = [],
    nodes = [],
    collapsed = false,
    showed,
    position = null
  }: GraphNodeSnapshot) {
    this.internalId = internalId ?? globalId ?? name ?? "";
    this.path = path ?? internalId ?? globalId ?? name ?? "";
    this.name = this.internalId;
    this.localId = localId ?? GraphId.localId(this.internalId);
    this.displayName = displayName ?? this.localId;
    this.attributes = { ...(attributes ?? {}) };
    this.semanticKind = semanticKind;
    this.typeIds = [...typeIds];
    this.relationMembers = relationMembers.map(member => ({
      name: member.name ?? "",
      memberTypeId: member.memberTypeId ?? "",
      participantIds: [...(member.participantIds ?? [])]
    }));
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
    this.semanticKind = next.semanticKind;
    this.typeIds = [...next.typeIds];
    this.relationMembers = next.relationMembers.map(member => ({
      name: member.name,
      memberTypeId: member.memberTypeId,
      participantIds: [...member.participantIds]
    }));
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
      semanticKind: this.semanticKind,
      typeIds: [...this.typeIds],
      relationMembers: this.relationMembers.map(member => ({
        name: member.name,
        memberTypeId: member.memberTypeId,
        participantIds: [...member.participantIds]
      })),
      collapsed: this.collapsed,
      showed: this.showed,
      position: this.position ? { ...this.position } : null,
      ...extra
    };
  }
}
