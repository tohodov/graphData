import { defaultBasis } from "./graphAttributes.js";
import { GraphEdge } from "./GraphEdge.js";
import { GraphNode } from "./GraphNode.js";
import { GraphProjection } from "./GraphProjection.js";

export class GraphModel {
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

  hasNode(globalId) {
    return this.loaded.has(globalId);
  }

  node(globalId) {
    return this.loaded.get(globalId) ?? null;
  }

  putNode(node) {
    const rich = GraphNode.from(node);
    this.loaded.set(rich.name, rich);
    return rich;
  }

  displayName(globalId) {
    return this.loaded.get(globalId)?.displayName ?? globalId;
  }

  physicalGraph() {
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

  visibleGraph() {
    const physical = this.physicalGraph();
    return this.schema.projectionBasis === "empty" ? physical : this.projectedGraph(physical);
  }

  projectedGraph(physical = this.physicalGraph()) {
    return new GraphProjection(this).project(physical);
  }

  rankGraph(graph) {
    return new GraphProjection(this).rank(graph);
  }

  discoverRelationInstances(physical = this.physicalGraph()) {
    return new GraphProjection(this).relations(physical);
  }

  getPortEndpoint(portGlobalId, edgesByNode, relationGlobalId) {
    return new GraphProjection(this).portEndpoint(portGlobalId, edgesByNode, relationGlobalId);
  }

  nodeTypeAssignments(physical = this.physicalGraph()) {
    return new GraphProjection(this).nodeTypeAssignments(physical);
  }

  isSchemaRoot(globalId) {
    return globalId === this.basis.nodeTypeRoot
      || globalId === this.basis.edgeTypeRoot
      || globalId === this.basis.relationRoot;
  }

  formatProjectedNodeName(node, nodeType) {
    if (!nodeType?.infoAttribute) {
      return node.displayName;
    }

    const value = node.attributes?.[nodeType.infoAttribute];
    return value ? node.displayName + " · " + value : node.displayName;
  }
}
