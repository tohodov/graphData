import { defaultBasis } from "./graphAttributes.js";
import { GraphEdge } from "./GraphEdge.js";
import { GraphNode } from "./GraphNode.js";
import { GraphProjection } from "./GraphProjection.js";

export class GraphModel {
  rootName: string | null;
  _selectedName: string | null;
  selectedNames: Set<string>;
  loaded: Map<string, GraphNode>;
  parentByNode: Map<string, string>;
  positions: Map<string, { x: number; y: number }>;
  velocities: Map<string, { x: number; y: number }>;
  view: { x: number; y: number; scale: number };
  dragging: any;
  pointer: any;
  simulationHandle: number | null;
  searchAbort: AbortController | null;
  busy: boolean;
  schema: any;
  constructor() {
    this.rootName = null;
    this._selectedName = null;
    this.selectedNames = new Set();
    this.loaded = new Map();
    this.parentByNode = new Map();
    this.positions = new Map();
    this.velocities = new Map();
    this.view = { x: 0, y: 0, scale: 1 };
    this.dragging = null;
    this.pointer = null;
    this.simulationHandle = null;
    this.searchAbort = null;
    this.busy = false;
    this.schema = {
      projectionBasis: "empty",
      basis: { ...defaultBasis },
      nodeTypes: new Map(),
      edgeTypes: new Map()
    };
  }

  get basis() {
    return this.schema.basis;
  }

  get selectedName(): string | null {
    return this._selectedName;
  }

  set selectedName(value: string | null) {
    this.selectedNames.clear();
    if (value) {
      this.selectedNames.add(value);
    }
    this._selectedName = value;
  }

  resetGraph() {
    this.rootName = null;
    this.selectedName = null;
    this.loaded.clear();
    this.parentByNode.clear();
    this.positions.clear();
    this.velocities.clear();
  }

  addSelectedName(name: string): void {
    if (!name) {
      return;
    }

    this.selectedNames.add(name);
    this._selectedName = name;
  }

  toggleSelectedName(name: string): boolean {
    if (!name) {
      return false;
    }

    if (this.selectedNames.has(name)) {
      this.selectedNames.delete(name);
      if (this._selectedName === name) {
        this._selectedName = this.selectedNames.values().next().value ?? null;
      }

      return false;
    }

    this.selectedNames.add(name);
    this._selectedName = name;
    return true;
  }

  removeSelectedName(name: string): void {
    this.selectedNames.delete(name);
    if (this._selectedName === name) {
      this._selectedName = this.selectedNames.values().next().value ?? null;
    }
  }

  isSelectedName(name: string): boolean {
    return this.selectedNames.has(name);
  }

  hasNode(globalId: string): boolean {
    return this.loaded.has(globalId);
  }

  node(globalId: string): GraphNode | null {
    return this.loaded.get(globalId) ?? null;
  }

  putNode(node: GraphNode | unknown): GraphNode {
    const rich = GraphNode.from(node);
    this.loaded.set(rich.name, rich);
    return rich;
  }

  displayName(globalId: string): string {
    return this.loaded.get(globalId)?.displayName ?? globalId;
  }

  physicalGraph(): any {
    const nodes = new Map();
    const edges = new Map();

    for (const node of this.loaded.values()) {
      nodes.set(node.name, node.toViewNode());
      node.edges.forEach(edge => {
        if (!edges.has(edge.key)) {
          edges.set(edge.key, edge.toViewEdge());
        }
      });
    }

    return { nodes: [...nodes.values()], edges: [...edges.values()] };
  }

  visibleGraph(): any {
    const physical = this.physicalGraph();
    return this.schema.projectionBasis === "empty" ? physical : this.projectedGraph(physical);
  }

  projectedGraph(physical: any = this.physicalGraph()): any {
    return new GraphProjection(this).project(physical);
  }

  collapseEdge(edge: GraphEdge | string | any): void {
    const key = this.edgeKey(edge);
    if (!key) {
      return;
    }

    const matches = this.findEdgeObjects(key);
    if (matches.length === 0 && typeof edge !== "string") {
      edge.collapsed = true;
      this.setProjectedRelationCollapsed(edge, true);
      return;
    }

    matches.forEach(match => {
      match.collapsed = true;
    });
    this.setProjectedRelationCollapsed(edge, true);
  }

  expandEdge(edge: GraphEdge | string | any): void {
    const key = this.edgeKey(edge);
    if (!key) {
      return;
    }

    const matches = this.findEdgeObjects(key);
    if (matches.length === 0 && typeof edge !== "string") {
      edge.collapsed = false;
      this.setProjectedRelationCollapsed(edge, false);
      return;
    }

    matches.forEach(match => {
      match.collapsed = false;
    });
    this.setProjectedRelationCollapsed(edge, false);
  }

  isEdgeCollapsed(edge: GraphEdge | string | any): boolean {
    if (typeof edge !== "string" && edge?.collapsed === true) {
      return true;
    }

    const relationGlobalId = this.projectedRelationGlobalId(edge);
    if (relationGlobalId) {
      return Boolean((this.loaded.get(relationGlobalId) as any)?.collapsed);
    }

    const key = this.edgeKey(edge);
    return Boolean(key && this.findEdgeObjects(key).some(match => match.collapsed));
  }

  edgeKey(edge: GraphEdge | string | any): string {
    return typeof edge === "string" ? edge : edge?.key ?? GraphEdge.from(edge).key;
  }

  findEdgeObjects(key: string): GraphEdge[] {
    const matches: GraphEdge[] = [];
    for (const node of this.loaded.values()) {
      for (const edge of node.edges ?? []) {
        if (edge.key === key) {
          matches.push(edge);
        }
      }
    }

    return matches;
  }

  setProjectedRelationCollapsed(edge: any, collapsed: boolean): void {
    const relationGlobalId = this.projectedRelationGlobalId(edge);
    if (!relationGlobalId) {
      return;
    }

    const relation = this.loaded.get(relationGlobalId) as any;
    if (relation) {
      relation.collapsed = collapsed;
    }
  }

  projectedRelationGlobalId(edge: GraphEdge | string | any): string | null {
    if (typeof edge === "string") {
      return edge.startsWith("projected:") ? edge.slice("projected:".length) : null;
    }

    return edge?.relationGlobalId && String(edge.key ?? "").startsWith("projected:")
      ? edge.relationGlobalId
      : null;
  }

  rankGraph(graph: any): any {
    return new GraphProjection(this).rank(graph);
  }

  discoverRelationInstances(physical: any = this.physicalGraph()): any[] {
    return new GraphProjection(this).relations(physical);
  }

  getPortEndpoint(portGlobalId: string, edgesByNode: Map<string, any[]>, relationGlobalId: string): string | null {
    return new GraphProjection(this).portEndpoint(portGlobalId, edgesByNode, relationGlobalId);
  }

  nodeTypeAssignments(physical: any = this.physicalGraph()): Map<string, any> {
    return new GraphProjection(this).nodeTypeAssignments(physical);
  }

  isSchemaRoot(globalId: string): boolean {
    return globalId === this.basis.nodeTypeRoot
      || globalId === this.basis.edgeTypeRoot
      || globalId === this.basis.relationRoot;
  }

  formatProjectedNodeName(node: any, nodeType: any): string {
    if (!nodeType?.infoAttribute) {
      return node.displayName;
    }

    const value = node.attributes?.[nodeType.infoAttribute];
    return value ? node.displayName + " · " + value : node.displayName;
  }
}
