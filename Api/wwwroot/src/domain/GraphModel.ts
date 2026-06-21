import { defaultBasis } from "./graphAttributes.js";
import { GraphEdge } from "./GraphEdge.js";
import { GraphNode, type GraphPoint, type GraphNodeSnapshot } from "./GraphNode.js";
import { GraphProjection } from "./GraphProjection.js";
import type { GraphType } from "./GraphType.js";
import type { GraphEdgeSnapshot, GraphEdgeControlSnapshot } from "./GraphEdge.js";

interface PositionableNode extends GraphNode {
  position?: GraphPoint | null;
  hasPosition?: () => boolean;
  setPosition?: (p: GraphPoint) => void;
  clearPosition?: () => void;
}

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
    const position = (node as PositionableNode).position;
    return position && Number.isFinite(position.x) && Number.isFinite(position.y)
      ? position
      : null;
  }

  private nodeHasPosition(node: GraphNode): boolean {
    return typeof (node as PositionableNode).hasPosition === "function"
      ? (node as PositionableNode).hasPosition!()
      : this.nodePosition(node) !== null;
  }

  private setNodePosition(node: GraphNode, position: GraphPoint): void {
    if (typeof (node as PositionableNode).setPosition === "function") {
      (node as PositionableNode).setPosition!(position);
    } else {
      (node as PositionableNode).position = { x: position.x, y: position.y };
    }
  }

  private clearNodePosition(node: GraphNode): void {
    if (typeof (node as PositionableNode).clearPosition === "function") {
      (node as PositionableNode).clearPosition!();
    } else {
      (node as PositionableNode).position = null;
    }
  }
}

type PrimitiveGraphChange = {
  kind: string;
  reason?: string;
  name?: string;
  node?: GraphNode;
  changes?: PrimitiveGraphChange[];
};

class EventedGraphNodeMap extends Map<string, GraphNode> {
  private readonly owner: GraphModel;
  private batchDepth = 0;
  private batchedChanges: PrimitiveGraphChange[] = [];
  private batchReason = "primitive-graph-change";

  constructor(owner: GraphModel) {
    super();
    this.owner = owner;
  }

  set(name: string, node: GraphNode | GraphNodeSnapshot | null | undefined): this {
    const rich = GraphNode.from(node);
    super.set(name, rich);
    this.changed({ kind: "node-upsert", reason: "node-upsert", name, node: rich });
    return this;
  }

  delete(name: string): boolean {
    const removed = super.delete(name);
    if (removed) {
      this.changed({ kind: "node-delete", reason: "node-delete", name });
    }
    return removed;
  }

  clear(): void {
    super.clear();
    this.changed({ kind: "cache-clear", reason: "cache-clear" });
  }

  batch<T>(reason: string, action: () => T): T {
    const previousReason = this.batchReason;
    this.batchDepth += 1;
    this.batchReason = reason || previousReason;
    try {
      return action();
    } finally {
      this.batchDepth -= 1;
      if (this.batchDepth === 0) {
        const changes = this.batchedChanges;
        this.batchedChanges = [];
        const batchReason = this.batchReason;
        this.batchReason = previousReason;
        if (changes.length > 0) {
          this.owner.recordPrimitiveGraphChange({
            kind: "batch",
            reason: batchReason,
            changes
          });
        }
      } else {
        this.batchReason = previousReason;
      }
    }
  }

  manual(change: PrimitiveGraphChange): void {
    this.changed(change);
  }

  private changed(change: PrimitiveGraphChange): void {
    if (this.batchDepth > 0) {
      this.batchedChanges.push(change);
      return;
    }

    this.owner.recordPrimitiveGraphChange(change);
  }
}

export type PrimitiveGraphChangeEvent = PrimitiveGraphChange & { primitiveRevision: number; };
export type ProjectedGraphNode = import("./GraphNode.js").GraphNodeSnapshot & {
  viewRank?: number;
  viewRadius?: number;
  viewRankReason?: string;
  typeGlobalId?: string;
  typeLabel?: string;
  typeRank?: number;
  color?: string;
  displayName?: string;
};

export type ProjectedGraphEdge = {
  key?: string;
  sourceGlobalId: string;
  targetGlobalId: string;
  sourceLocalId?: string;
  targetLocalId?: string;
  relationGlobalId?: string;
  typeGlobalId?: string | null;
  label?: string;
  color?: string;
  directed?: boolean;
  typeRank?: number;
  projected?: boolean;
  collapsed?: boolean;
  viewRank?: number;
  viewRankReason?: string;
  selected?: boolean;
};

export type ProjectedGraph = {
  nodes: ProjectedGraphNode[];
  edges: ProjectedGraphEdge[];
};
export type ProjectionRebuiltEvent = {
  reason: string;
  revision: number;
  primitiveRevision: number;
  changes: PrimitiveGraphChange[];
  projectionGraph: ProjectedGraph;
  intermediateGraph: ProjectedGraph;
};
export type PointerState = { x: number; y: number; startX?: number; startY?: number; node?: GraphNode; element?: unknown; };
export type GraphSchema = {
  defaultBasis: { nodeTypeRoot: string; edgeTypeRoot: string; relationRoot: string };
  basis: { nodeTypeRoot: string; edgeTypeRoot: string; relationRoot: string };
  systemNodeIds: Record<string, string>;
  baseTypeIds: Record<string, string>;
  nodeTypes: Map<string, GraphType>;
  edgeTypes: Map<string, GraphType>;
};

export class GraphModel {
  rootName: string | null;
  _selectedName: string | null;
  selectedNames: Set<string>;
  selectedEdgeKeys: Set<string>;
  loaded: EventedGraphNodeMap;
  parentByNode: Map<string, string>;
  positions: GraphNodePositionMap;
  velocities: Map<string, { x: number; y: number }>;
  view: { x: number; y: number; scale: number };
  dragging: PointerState | null;
  pointer: PointerState | null;
  simulationHandle: number | null;
  searchAbort: AbortController | null;
  busy: boolean;
  schema: GraphSchema;
  intermediateGraph: ProjectedGraph;
  projectionDirty: boolean;
  projectionRevision: number;
  primitiveRevision: number;
  primitiveListeners: Set<(event: PrimitiveGraphChangeEvent) => void>;
  projectionListeners: Set<(event: ProjectionRebuiltEvent) => void>;
  queuedProjectionPromise: Promise<ProjectedGraph> | null;
  queuedProjectionReason: string;
  queuedProjectionChanges: PrimitiveGraphChange[];
  constructor() {
    this.rootName = null;
    this._selectedName = null;
    this.selectedNames = new Set();
    this.selectedEdgeKeys = new Set();
    this.loaded = new EventedGraphNodeMap(this);
    this.parentByNode = new Map();
    this.positions = new GraphNodePositionMap(this.loaded);
    this.velocities = new Map();
    this.view = { x: 0, y: 0, scale: 1 };
    this.dragging = null;
    this.pointer = null;
    this.simulationHandle = null;
    this.searchAbort = null;
    this.busy = false;
    this.intermediateGraph = { nodes: [], edges: [] };
    this.projectionDirty = true;
    this.projectionRevision = 0;
    this.primitiveRevision = 0;
    this.primitiveListeners = new Set();
    this.projectionListeners = new Set();
    this.queuedProjectionPromise = null;
    this.queuedProjectionReason = "projection";
    this.queuedProjectionChanges = [];
    this.schema = {
      defaultBasis: { ...defaultBasis },
      basis: { ...defaultBasis },
      systemNodeIds: {},
      baseTypeIds: {},
      nodeTypes: new Map(),
      edgeTypes: new Map()
    };
  }

  applyUiSettings(settings: Record<string, unknown> | null | undefined): void {
    const basis = GraphModel.readBasis(settings?.basis ?? settings?.systemNodeIds, this.schema.defaultBasis);
    this.schema.defaultBasis = { ...basis };
    this.schema.basis = { ...basis };
    this.schema.systemNodeIds = { ...(settings?.systemNodeIds ?? {}) };
    this.schema.baseTypeIds = { ...(settings?.baseTypeIds ?? {}) };
  }

  defaultBasis(): { nodeTypeRoot: string; edgeTypeRoot: string; relationRoot: string } {
    return this.schema.defaultBasis;
  }

  static readBasis(value: Record<string, unknown> | null | undefined, fallback: Record<string, unknown> | null | undefined = defaultBasis as Record<string, unknown>): { nodeTypeRoot: string; edgeTypeRoot: string; relationRoot: string } {
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
    this.batchPrimitiveChanges("reset", () => {
      this.rootName = null;
      this.selectedName = null;
      this.loaded.clear();
      this.parentByNode.clear();
      this.positions.clear();
      this.velocities.clear();
      this.intermediateGraph = { nodes: [], edges: [] };
      this.projectionDirty = true;
    });
  }

  onPrimitiveGraphChanged(listener: (event: PrimitiveGraphChangeEvent) => void): () => void {
    this.primitiveListeners.add(listener);
    return () => this.primitiveListeners.delete(listener);
  }

  onProjectionRebuilt(listener: (event: ProjectionRebuiltEvent) => void): () => void {
    this.projectionListeners.add(listener);
    return () => this.projectionListeners.delete(listener);
  }

  recordPrimitiveGraphChange(change: PrimitiveGraphChange): void {
    this.primitiveRevision += 1;
    this.projectionDirty = true;
    const event = {
      ...change,
      reason: change.reason ?? change.kind,
      primitiveRevision: this.primitiveRevision
    };
    this.primitiveListeners.forEach(listener => listener(event));
    this.queuedProjectionChanges.push(change);
    void this.requestProjectionRebuild(event.reason);
  }

  notifyPrimitiveChanged(reason: string, extra: Record<string, unknown> = {}): void {
    this.loaded.manual({ kind: "manual-change", reason, ...extra });
  }

  batchPrimitiveChanges<T>(reason: string, action: () => T): T {
    return this.loaded.batch(reason, action);
  }

  async requestProjectionRebuild(reason = "projection"): Promise<ProjectedGraph> {
    this.queuedProjectionReason = reason;
    this.projectionDirty = true;
    if (this.queuedProjectionPromise) {
      return this.queuedProjectionPromise;
    }

    this.queuedProjectionPromise = new Promise(resolve => {
      this.scheduleAsync(() => {
        const changes = this.queuedProjectionChanges;
        this.queuedProjectionChanges = [];
        const graph = this.projectionDirty
          ? this.rebuildProjectionNow({ emit: false, reason: this.queuedProjectionReason })
          : this.intermediateGraph;
        this.emitProjectionRebuilt(this.queuedProjectionReason, changes);
        this.queuedProjectionPromise = null;
        resolve(graph);
      });
    });
    return this.queuedProjectionPromise;
  }

  async whenProjectionSettled(): Promise<ProjectedGraph> {
    return this.queuedProjectionPromise ?? this.intermediateGraph;
  }

  private scheduleAsync(callback: () => void): void {
    if (typeof queueMicrotask === "function") {
      queueMicrotask(callback);
      return;
    }

    void Promise.resolve().then(callback);
  }

  emitProjectionRebuilt(reason: string, changes: PrimitiveGraphChange[] = []): void {
    const event = {
      reason,
      revision: this.projectionRevision,
      primitiveRevision: this.primitiveRevision,
      changes,
      projectionGraph: this.intermediateGraph,
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

  selectOnlyEdge(edge: GraphEdge | string | { key?: string }): void {
    const key = this.edgeKey(edge);
    this.clearSelection();
    if (key) {
      this.selectedEdgeKeys.add(key);
    }
  }

  addSelectedEdge(edge: GraphEdge | string | { key?: string }): void {
    const key = this.edgeKey(edge);
    if (key) {
      this.selectedEdgeKeys.add(key);
    }
  }

  toggleSelectedEdge(edge: GraphEdge | string | { key?: string }): boolean {
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

  removeSelectedEdge(edge: GraphEdge | string | { key?: string }): void {
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

  isSelectedEdge(edge: GraphEdge | string | { key?: string }): boolean {
    const key = this.edgeKey(edge);
    return Boolean(key && this.selectedEdgeKeys.has(key));
  }

  selectElements(nodeNames: Iterable<string> = [], edgeKeys: Iterable<string> = [], options: { append?: boolean } = {}): void {
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

  visibleGraph(): ProjectedGraph {
    if (this.projectionDirty) {
      this.rebuildProjectionNow({ emit: false, reason: "visible-graph" });
    }

    return this.withEdgeControls(this.intermediateGraph);
  }

  rebuildProjection(options: { emit?: boolean; reason?: string } = {}): ProjectedGraph {
    return this.rebuildProjectionNow(options);
  }

  rebuildProjectionNow(options: { emit?: boolean; reason?: string } = {}): ProjectedGraph {
    this.intermediateGraph = new GraphProjection(this).projectFromCache();
    this.projectionDirty = false;
    this.projectionRevision += 1;
    if (options.emit === true) {
      this.emitProjectionRebuilt(options.reason ?? "projection");
    }
    return this.intermediateGraph;
  }

  *primitiveNodeViews(): IterableIterator<Record<string, unknown>> {
    for (const node of this.loaded.values()) {
      yield node.toViewNode();
    }
  }

  *primitiveEdgeViews(): IterableIterator<Record<string, unknown>> {
    const emitted = new Set<string>();
    for (const node of this.loaded.values()) {
      for (const edge of node.edges ?? []) {
        const normalized = GraphEdge.from(edge);
        if (!emitted.has(normalized.key)) {
          emitted.add(normalized.key);
          yield normalized.toViewEdge(this.edgeEndpointNodes(normalized));
        }
      }
    }
  }

  primitiveNodeCount(): number {
    return this.loaded.size;
  }

  primitiveEdgeCount(): number {
    const keys = new Set<string>();
    for (const node of this.loaded.values()) {
      for (const edge of node.edges ?? []) {
        keys.add(GraphEdge.from(edge).key);
      }
    }

    return keys.size;
  }

  withEdgeControls(graph: ProjectedGraph): ProjectedGraph {
    return {
      ...graph,
      edges: (graph.edges ?? []).map(edge => ({
        ...edge,
        controls: this.edgeControls(edge)
      }))
    };
  }

  edgeControls(edge: GraphEdge | GraphEdgeSnapshot): GraphEdgeControlSnapshot[] {
    const normalized = this.edgeWithEndpointNodes(edge);
    return normalized.controls({
      isCollapsed: this.isEdgeCollapsed(normalized),
      displayName: globalId => this.displayName(globalId)
    });
  }

  edgeEndpointControl(edge: GraphEdge | GraphEdgeSnapshot, anchorName: string): GraphEdgeControlSnapshot | null {
    const normalized = this.edgeWithEndpointNodes(edge);
    return normalized.endpointControl(anchorName, {
      isCollapsed: this.isEdgeCollapsed(normalized),
      displayName: globalId => this.displayName(globalId)
    });
  }

  edgeWithEndpointNodes(edge: GraphEdge | GraphEdgeSnapshot): GraphEdge {
    const normalized = GraphEdge.from(edge);
    return GraphEdge.from(normalized.toViewEdge(this.edgeEndpointNodes(normalized)));
  }

  edgeEndpointNodes(edge: GraphEdge | GraphEdgeSnapshot): Record<string, unknown> {
    const normalized = GraphEdge.from(edge);
    return {
      sourceNode: this.loaded.get(normalized.sourceGlobalId) ?? null,
      targetNode: this.loaded.get(normalized.targetGlobalId) ?? null,
      sourcePositioned: this.positions.has(normalized.sourceGlobalId),
      targetPositioned: this.positions.has(normalized.targetGlobalId)
    };
  }

  collapseEdge(edge: GraphEdge | string | { key?: string; collapsed?: boolean; relationGlobalId?: string }): void {
    const key = this.edgeKey(edge);
    if (!key) {
      return;
    }

    const matches = this.findEdgeObjects(key);
    if (matches.length === 0 && typeof edge !== "string") {
      edge.collapsed = true;
      this.setProjectedRelationCollapsed(edge, true);
      this.notifyPrimitiveChanged("edge-collapse", { key });
      return;
    }

    matches.forEach(match => {
      match.collapsed = true;
    });
    this.setProjectedRelationCollapsed(edge, true);
    this.notifyPrimitiveChanged("edge-collapse", { key });
  }

  expandEdge(edge: GraphEdge | string | { key?: string; collapsed?: boolean; relationGlobalId?: string }): void {
    const key = this.edgeKey(edge);
    if (!key) {
      return;
    }

    const matches = this.findEdgeObjects(key);
    if (matches.length === 0 && typeof edge !== "string") {
      edge.collapsed = false;
      this.setProjectedRelationCollapsed(edge, false);
      this.notifyPrimitiveChanged("edge-expand", { key });
      return;
    }

    matches.forEach(match => {
      match.collapsed = false;
    });
    this.setProjectedRelationCollapsed(edge, false);
    this.notifyPrimitiveChanged("edge-expand", { key });
  }

  isEdgeCollapsed(edge: GraphEdge | string | { key?: string; collapsed?: boolean; relationGlobalId?: string }): boolean {
    if (typeof edge !== "string" && edge?.collapsed === true) {
      return true;
    }

    const relationGlobalId = this.projectedRelationGlobalId(edge);
    if (relationGlobalId) {
      return Boolean((this.loaded.get(relationGlobalId) as Record<string, unknown>)?.collapsed);
    }

    const key = this.edgeKey(edge);
    return Boolean(key && this.findEdgeObjects(key).some(match => match.collapsed));
  }

  edgeKey(edge: GraphEdge | string | { key?: string; sourceGlobalId?: string; targetGlobalId?: string; }): string {
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

  setProjectedRelationCollapsed(edge: string | { key?: string; relationGlobalId?: string }, collapsed: boolean): void {
    const relationGlobalId = this.projectedRelationGlobalId(edge);
    if (!relationGlobalId) {
      return;
    }

    const relation = this.loaded.get(relationGlobalId) as Record<string, unknown>;
    if (relation) {
      relation.collapsed = collapsed;
    }
  }

  projectedRelationGlobalId(edge: GraphEdge | string | { key?: string; relationGlobalId?: string }): string | null {
    if (typeof edge === "string") {
      return edge.startsWith("projected:") ? edge.slice("projected:".length) : null;
    }

    return edge?.relationGlobalId && String(edge.key ?? "").startsWith("projected:")
      ? edge.relationGlobalId
      : null;
  }

  rankGraph(graph: ProjectedGraph): ProjectedGraph {
    return new GraphProjection(this).rank(graph);
  }

  discoverRelationInstances(physical: ProjectedGraph | null = null): GraphNode[] {
    return physical
      ? new GraphProjection(this).relations(physical)
      : new GraphProjection(this).relationsFromCache();
  }

  getPortEndpoint(portGlobalId: string, edgesByNode: Map<string, GraphEdge[]>, relationGlobalId: string): string | null {
    return new GraphProjection(this).portEndpoint(portGlobalId, edgesByNode, relationGlobalId);
  }

  nodeTypeAssignments(physical: ProjectedGraph | null = null): Map<string, string[]> {
    return physical
      ? new GraphProjection(this).nodeTypeAssignments(physical)
      : new GraphProjection(this).nodeTypeAssignmentsFromCache();
  }

  isSchemaRoot(globalId: string): boolean {
    return globalId === this.basis.nodeTypeRoot
      || globalId === this.basis.edgeTypeRoot
      || globalId === this.basis.relationRoot;
  }

  treeChildNameForEdge(edge: GraphEdge | GraphEdgeSnapshot, anchorName: string): string | null {
    const normalized = GraphEdge.from(edge);
    const otherName = normalized.otherEndpoint(anchorName);
    return this.parentByNode.get(otherName) === anchorName
      ? otherName
      : this.parentByNode.get(anchorName) === otherName && anchorName !== this.rootName
        ? anchorName
        : null;
  }

  formatProjectedNodeName(node: ProjectedGraphNode | import("./GraphNode.js").GraphNode, nodeType: GraphType | Record<string, unknown>): string {
    if (!nodeType?.infoAttribute) {
      return node.displayName;
    }

    const value = node.attributes?.[nodeType.infoAttribute];
    return value ? node.displayName + " · " + value : node.displayName;
  }
}
