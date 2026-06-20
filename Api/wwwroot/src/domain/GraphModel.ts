import { defaultBasis } from "./graphAttributes.js";
import { GraphEdge } from "./GraphEdge.js";
import { GraphNode, type GraphPoint } from "./GraphNode.js";
import { GraphProjection } from "./GraphProjection.js";

class GraphNodePositionMap {
  private readonly nodes: Map<string, GraphNode>;

  constructor(nodes: Map<string, GraphNode>) {
    this.nodes = nodes;
  }

  get size(): number {
    return [...this.entries()].length;
  }

  get(name: string): GraphPoint | undefined {
    return this.attach(name) ?? undefined;
  }

  has(name: string): boolean {
    return this.attach(name) !== null;
  }

  set(name: string, position: GraphPoint): this {
    const node = this.nodes.get(name);
    if (node) {
      this.setNodePosition(node, position);
    }

    return this;
  }

  delete(name: string): boolean {
    const node = this.nodes.get(name);
    const hadPosition = Boolean(node && this.nodeHasPosition(node));
    if (node) {
      this.clearNodePosition(node);
    }
    return hadPosition;
  }

  clear(): void {
    for (const node of this.nodes.values()) {
      this.clearNodePosition(node);
    }
  }

  *keys(): IterableIterator<string> {
    for (const [name, node] of this.nodes.entries()) {
      if (this.nodeHasPosition(node)) {
        yield name;
      }
    }
  }

  *values(): IterableIterator<GraphPoint> {
    for (const [, position] of this.entries()) {
      yield position;
    }
  }

  *entries(): IterableIterator<[string, GraphPoint]> {
    for (const [name, node] of this.nodes.entries()) {
      const position = this.attach(name);
      if (position) {
        yield [name, position];
      }
    }
  }

  forEach(callback: (value: GraphPoint, key: string, map: GraphNodePositionMap) => void, thisArg?: unknown): void {
    for (const [name, position] of this.entries()) {
      callback.call(thisArg, position, name, this);
    }
  }

  [Symbol.iterator](): IterableIterator<[string, GraphPoint]> {
    return this.entries();
  }

  private attach(name: string): GraphPoint | null {
    const node = this.nodes.get(name);
    return node ? this.nodePosition(node) : null;
  }

  private nodePosition(node: GraphNode): GraphPoint | null {
    const position = (node as any).position;
    return position && Number.isFinite(position.x) && Number.isFinite(position.y)
      ? position
      : null;
  }

  private nodeHasPosition(node: GraphNode): boolean {
    return typeof (node as any).hasPosition === "function"
      ? (node as any).hasPosition()
      : this.nodePosition(node) !== null;
  }

  private setNodePosition(node: GraphNode, position: GraphPoint): void {
    if (typeof (node as any).setPosition === "function") {
      (node as any).setPosition(position);
    } else {
      (node as any).position = { x: position.x, y: position.y };
    }
  }

  private clearNodePosition(node: GraphNode): void {
    if (typeof (node as any).clearPosition === "function") {
      (node as any).clearPosition();
    } else {
      (node as any).position = null;
    }
  }
}

export class GraphModel {
  rootName: string | null;
  _selectedName: string | null;
  selectedNames: Set<string>;
  selectedEdgeKeys: Set<string>;
  loaded: Map<string, GraphNode>;
  parentByNode: Map<string, string>;
  positions: GraphNodePositionMap;
  velocities: Map<string, { x: number; y: number }>;
  view: { x: number; y: number; scale: number };
  dragging: any;
  pointer: any;
  simulationHandle: number | null;
  searchAbort: AbortController | null;
  busy: boolean;
  schema: any;
  primitiveGraphCache: any;
  intermediateGraph: any;
  projectionRevision: number;
  projectionListeners: Set<(event: any) => void>;
  constructor() {
    this.rootName = null;
    this._selectedName = null;
    this.selectedNames = new Set();
    this.selectedEdgeKeys = new Set();
    this.loaded = new Map();
    this.parentByNode = new Map();
    this.positions = new GraphNodePositionMap(this.loaded);
    this.velocities = new Map();
    this.view = { x: 0, y: 0, scale: 1 };
    this.dragging = null;
    this.pointer = null;
    this.simulationHandle = null;
    this.searchAbort = null;
    this.busy = false;
    this.primitiveGraphCache = { nodes: [], edges: [] };
    this.intermediateGraph = { nodes: [], edges: [] };
    this.projectionRevision = 0;
    this.projectionListeners = new Set();
    this.schema = {
      projectionBasis: "typed",
      defaultBasis: { ...defaultBasis },
      basis: { ...defaultBasis },
      systemNodeIds: {},
      baseTypeIds: {},
      nodeTypes: new Map(),
      edgeTypes: new Map()
    };
  }

  applyUiSettings(settings: any): void {
    const basis = GraphModel.readBasis(settings?.basis ?? settings?.systemNodeIds, this.schema.defaultBasis);
    this.schema.defaultBasis = { ...basis };
    this.schema.basis = { ...basis };
    this.schema.systemNodeIds = { ...(settings?.systemNodeIds ?? {}) };
    this.schema.baseTypeIds = { ...(settings?.baseTypeIds ?? {}) };
  }

  defaultBasis(): { nodeTypeRoot: string; edgeTypeRoot: string; relationRoot: string } {
    return this.schema.defaultBasis;
  }

  static readBasis(value: any, fallback: any = defaultBasis): { nodeTypeRoot: string; edgeTypeRoot: string; relationRoot: string } {
    return {
      nodeTypeRoot: String(value?.nodeTypeRoot || fallback?.nodeTypeRoot || ""),
      edgeTypeRoot: String(value?.edgeTypeRoot || fallback?.edgeTypeRoot || ""),
      relationRoot: String(value?.relationRoot || fallback?.relationRoot || "")
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
    this.selectedEdgeKeys.clear();
    if (value) {
      this.selectedNames.add(value);
    }
    this._selectedName = value;
  }

  clearSelection(): void {
    this.selectedNames.clear();
    this.selectedEdgeKeys.clear();
    this._selectedName = null;
  }

  resetGraph() {
    this.rootName = null;
    this.selectedName = null;
    this.loaded.clear();
    this.parentByNode.clear();
    this.positions.clear();
    this.velocities.clear();
    this.rebuildProjection({ reason: "reset" });
  }

  onProjectionRebuilt(listener: (event: any) => void): () => void {
    this.projectionListeners.add(listener);
    return () => this.projectionListeners.delete(listener);
  }

  emitProjectionRebuilt(reason: string): void {
    const event = {
      reason,
      revision: this.projectionRevision,
      primitiveGraph: this.primitiveGraphCache,
      intermediateGraph: this.intermediateGraph
    };
    this.projectionListeners.forEach(listener => listener(event));
  }

  addSelectedName(name: string): void {
    if (!name) {
      return;
    }

    this.selectedNames.add(name);
    this._selectedName = name;
  }

  selectOnlyEdge(edge: GraphEdge | string | any): void {
    const key = this.edgeKey(edge);
    this.clearSelection();
    if (key) {
      this.selectedEdgeKeys.add(key);
    }
  }

  addSelectedEdge(edge: GraphEdge | string | any): void {
    const key = this.edgeKey(edge);
    if (key) {
      this.selectedEdgeKeys.add(key);
    }
  }

  toggleSelectedEdge(edge: GraphEdge | string | any): boolean {
    const key = this.edgeKey(edge);
    if (!key) {
      return false;
    }

    if (this.selectedEdgeKeys.has(key)) {
      this.selectedEdgeKeys.delete(key);
      return false;
    }

    this.selectedEdgeKeys.add(key);
    return true;
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

  removeSelectedEdge(edge: GraphEdge | string | any): void {
    const key = this.edgeKey(edge);
    if (key) {
      this.selectedEdgeKeys.delete(key);
    }
  }

  removeSelectedEdgesConnectedTo(name: string): void {
    for (const key of [...this.selectedEdgeKeys]) {
      const edge = this.findEdgeObjects(key)[0];
      if (!edge || edge.sourceGlobalId === name || edge.targetGlobalId === name) {
        this.selectedEdgeKeys.delete(key);
      }
    }
  }

  isSelectedEdge(edge: GraphEdge | string | any): boolean {
    const key = this.edgeKey(edge);
    return Boolean(key && this.selectedEdgeKeys.has(key));
  }

  selectElements(nodeNames: Iterable<string> = [], edgeKeys: Iterable<string> = [], options: any = {}): void {
    if (options.append !== true) {
      this.clearSelection();
    }

    let lastNode: string | null = null;
    for (const name of nodeNames) {
      if (!name) {
        continue;
      }

      this.selectedNames.add(name);
      lastNode = name;
    }

    for (const key of edgeKeys) {
      if (key) {
        this.selectedEdgeKeys.add(key);
      }
    }

    this._selectedName = lastNode ?? this._selectedName ?? this.selectedNames.values().next().value ?? null;
  }

  hasNode(globalId: string): boolean {
    return this.loaded.has(globalId);
  }

  isNodeVisible(globalId: string): boolean {
    const node = this.loaded.get(globalId);
    return Boolean(node && node.showed !== false);
  }

  visibleNodeCount(): number {
    return [...this.loaded.values()].filter(node => node.showed !== false).length;
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
      if (node.showed === false) {
        continue;
      }

      nodes.set(node.name, node.toViewNode());
      node.edges.forEach(edge => {
        if (!edges.has(edge.key)) {
          edges.set(edge.key, edge.toViewEdge(this.edgeEndpointNodes(edge)));
        }
      });
    }

    this.primitiveGraphCache = { nodes: [...nodes.values()], edges: [...edges.values()] };
    return this.primitiveGraphCache;
  }

  visibleGraph(): any {
    return this.withEdgeControls(this.rebuildProjection());
  }

  projectedGraph(physical: any = this.physicalGraph()): any {
    return new GraphProjection(this).project(physical);
  }

  rebuildProjection(options: any = {}): any {
    const physical = this.physicalGraph();
    this.intermediateGraph = this.projectedGraph(physical);
    this.projectionRevision += 1;
    if (options.emit === true) {
      this.emitProjectionRebuilt(options.reason ?? "projection");
    }
    return this.intermediateGraph;
  }

  withEdgeControls(graph: any): any {
    return {
      ...graph,
      edges: (graph.edges ?? []).map(edge => ({
        ...edge,
        controls: this.edgeControls(edge)
      }))
    };
  }

  edgeControls(edge: GraphEdge | any): any[] {
    const normalized = this.edgeWithEndpointNodes(edge);
    return normalized.controls({
      isCollapsed: this.isEdgeCollapsed(normalized),
      displayName: globalId => this.displayName(globalId)
    });
  }

  edgeEndpointControl(edge: GraphEdge | any, anchorName: string): any | null {
    const normalized = this.edgeWithEndpointNodes(edge);
    return normalized.endpointControl(anchorName, {
      isCollapsed: this.isEdgeCollapsed(normalized),
      displayName: globalId => this.displayName(globalId)
    });
  }

  edgeWithEndpointNodes(edge: GraphEdge | any): GraphEdge {
    const normalized = GraphEdge.from(edge);
    return GraphEdge.from(normalized.toViewEdge(this.edgeEndpointNodes(normalized)));
  }

  edgeEndpointNodes(edge: GraphEdge | any): Record<string, unknown> {
    const normalized = GraphEdge.from(edge);
    return {
      sourceNode: this.loaded.get(normalized.sourceGlobalId) ?? null,
      targetNode: this.loaded.get(normalized.targetGlobalId) ?? null,
      sourcePositioned: this.positions.has(normalized.sourceGlobalId),
      targetPositioned: this.positions.has(normalized.targetGlobalId)
    };
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
    if (typeof edge === "string") {
      return edge;
    }

    if (edge?.key) {
      return edge.key;
    }

    const normalized = GraphEdge.from(edge);
    return normalized.sourceGlobalId && normalized.targetGlobalId ? normalized.key : "";
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

  treeChildNameForEdge(edge: GraphEdge | any, anchorName: string): string | null {
    const normalized = GraphEdge.from(edge);
    const otherName = normalized.otherEndpoint(anchorName);
    return this.parentByNode.get(otherName) === anchorName
      ? otherName
      : this.parentByNode.get(anchorName) === otherName && anchorName !== this.rootName
        ? anchorName
        : null;
  }

  formatProjectedNodeName(node: any, nodeType: any): string {
    if (!nodeType?.infoAttribute) {
      return node.displayName;
    }

    const value = node.attributes?.[nodeType.infoAttribute];
    return value ? node.displayName + " · " + value : node.displayName;
  }
}
