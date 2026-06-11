import { defaultBasis } from "./graphAttributes.js";
import { GraphEdge } from "./GraphEdge.js";
import { GraphNode } from "./GraphNode.js";
import { GraphProjection } from "./GraphProjection.js";

export class GraphModel {
  rootName: string | null;
  selectedName: string | null;
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
    this.selectedName = null;
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

  resetGraph() {
    this.rootName = null;
    this.selectedName = null;
    this.loaded.clear();
    this.parentByNode.clear();
    this.positions.clear();
    this.velocities.clear();
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
