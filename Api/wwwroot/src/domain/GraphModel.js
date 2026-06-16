import { defaultBasis } from "./graphAttributes.js";
import { GraphEdge } from "./GraphEdge.js";
import { GraphNode } from "./GraphNode.js";
import { GraphProjection } from "./GraphProjection.js";

export class GraphModel {
  constructor() {
    this.rootName = null;
    this._selectedName = null;
    this.selectedNames = new Set();
    this.loaded = new Map();
    this.parentByNode = new Map();
    this.collapsedEdges = new Set();
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

  get selectedName() {
    return this._selectedName;
  }

  set selectedName(value) {
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
    this.collapsedEdges.clear();
    this.positions.clear();
    this.velocities.clear();
  }

  addSelectedName(name) {
    if (!name) {
      return;
    }

    this.selectedNames.add(name);
    this._selectedName = name;
  }

  toggleSelectedName(name) {
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

  removeSelectedName(name) {
    this.selectedNames.delete(name);
    if (this._selectedName === name) {
      this._selectedName = this.selectedNames.values().next().value ?? null;
    }
  }

  isSelectedName(name) {
    return this.selectedNames.has(name);
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
          edges.set(edge.key, edge.toViewEdge({
            collapsed: this.isEdgeCollapsed(edge)
          }));
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

  collapseEdge(edge) {
    const key = this.edgeKey(edge);
    if (key) {
      this.collapsedEdges.add(key);
    }
  }

  expandEdge(edge) {
    const key = this.edgeKey(edge);
    if (key) {
      this.collapsedEdges.delete(key);
    }
  }

  isEdgeCollapsed(edge) {
    const key = this.edgeKey(edge);
    return Boolean(key && this.collapsedEdges.has(key));
  }

  edgeKey(edge) {
    return typeof edge === "string" ? edge : GraphEdge.from(edge).key;
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
