import {
  graphElementAttribute,
  graphKindAttribute,
  projectionCollapsedAttribute,
  projectionColorAttribute,
  projectionDirectedAttribute,
  projectionInfoAttribute,
  projectionLabelVisibleAttribute,
  projectionRankAttribute,
  projectionVisibleAttribute,
  nodeRadius
} from "./domain/graphAttributes.js";
import { GraphApi } from "./infrastructure/GraphApi.js";
import { WebGpuGraphCanvas } from "./ui/WebGpuGraphCanvas.js";
import { GraphEdge } from "./domain/GraphEdge.js";
import { GraphId } from "./domain/GraphId.js";
import { GraphModel } from "./domain/GraphModel.js";
import { GraphNode } from "./domain/GraphNode.js";
import { GraphType } from "./domain/GraphType.js";

type GraphViewerDependencies = {
  document: Document;
  window: Window;
  api?: GraphApi;
};

export class GraphViewer {
  [key: string]: any;

  document: Document;
  window: Window;
  api: GraphApi;
  graph: GraphModel;
  canvas: WebGpuGraphCanvas;

  constructor({ document, window, api = new GraphApi(window.fetch.bind(window)) }: GraphViewerDependencies) {
    this.document = document;
    this.window = window;
    this.api = api;
    this.graph = new GraphModel();

    this.graphSurface = this.requireElement("#graph");
    this.labelLayer = this.requireElement("#graph-label-layer");
    this.selectionOverlay = this.requireElement("#selection-overlay");
    this.selectionOverlayToggle = this.requireElement("#selection-overlay-toggle");
    this.selectionSummary = this.requireElement("#selection-summary");
    this.selectionList = this.requireElement("#selection-list");
    this.selectionOverlayCollapsed = false;
    this.expandedSelectionKeys = new Set();
    this.expandedBasisRuleKeys = new Set();
    this.gpuWarning = this.document.querySelector("#gpu-warning");
    this.fitButton = this.requireElement("#fit-button");
    this.resetButton = this.requireElement("#reset-button");
    this.rendererSelect = this.requireElement("#renderer-select");
    this.mobileMenuToggle = this.requireElement("#mobile-menu-toggle");
    this.mobileMenuClose = this.requireElement("#mobile-menu-close");
    this.statusOutput = this.requireElement("#status");
    this.emptyState = this.requireElement("#empty-state");
    this.emptyTitle = this.requireElement("#empty-state .empty-title");
    this.clearSelectionButton = this.requireElement("#clear-selection-button");
    this.deleteSelectedNodesButton = this.requireElement("#delete-selected-nodes-button");
    this.createNodeForm = this.requireElement("#create-node-form");
    this.createNodeName = this.requireElement("#create-node-name");
    this.createNodeParent = this.requireElement("#create-node-parent");
    this.createNodeType = this.requireElement("#create-node-type");
    this.connectForm = this.requireElement("#connect-form");
    this.connectTargetName = this.requireElement("#connect-target-name");
    this.connectEdgeType = this.requireElement("#connect-edge-type");
    this.connectEdgeName = this.requireElement("#connect-edge-name");
    this.searchForm = this.requireElement("#search-form");
    this.searchQueryJson = this.requireElement("#search-query-json");
    this.searchSubmitButton = this.requireElement("#search-submit-button");
    this.searchStopButton = this.requireElement("#search-stop-button");
    this.searchResults = this.requireElement("#search-results");
    this.subgraphForm = this.requireElement("#subgraph-form");
    this.subgraphResults = this.requireElement("#subgraph-results");
    this.basisNodeInput = this.requireElement("#basis-node");
    this.nodeTypeRootInput = this.requireElement("#node-type-root");
    this.edgeTypeRootInput = this.requireElement("#edge-type-root");
    this.relationRootInput = this.requireElement("#relation-root");
    this.loadBasisButton = this.requireElement("#load-basis-button");
    this.ensureBasisButton = this.requireElement("#ensure-basis-button");
    this.refreshTypesButton = this.requireElement("#refresh-types-button");
    this.loadRelationsButton = this.requireElement("#load-relations-button");
    this.projectionSummary = this.requireElement("#projection-summary");
    this.nodeTypeList = this.requireElement("#node-type-list");
    this.assignNodeType = this.requireElement("#assign-node-type");
    this.assignNodeTypeButton = this.requireElement("#assign-node-type-button");
    this.assignEdgeType = this.requireElement("#assign-edge-type");
    this.assignEdgeTypeButton = this.requireElement("#assign-edge-type-button");
    this.edgeTypeList = this.requireElement("#edge-type-list");
    this.typedEdgeList = this.requireElement("#typed-edge-list");
    this.canvas = new WebGpuGraphCanvas({
      document: this.document,
      window: this.window,
      canvas: this.graphSurface,
      rendererSelect: this.rendererSelect,
      labelLayer: this.labelLayer,
      emptyState: this.emptyState,
      gpuWarning: this.gpuWarning,
      buildGraph: () => this.graph.visibleGraph(),
      positions: this.graph.positions,
      velocities: this.graph.velocities,
      view: this.graph.view,
      isNodeSelected: name => this.graph.isSelectedName(name),
      isEdgeSelected: edge => this.graph.isSelectedEdge(edge),
      selectOnlyNode: name => {
        this.graph.selectedName = name;
        this.render();
      },
      addNodeToSelection: name => {
        this.graph.addSelectedName(name);
        this.render();
      },
      toggleNodeSelection: name => {
        this.graph.toggleSelectedName(name);
        this.render();
      },
      selectOnlyEdge: edge => {
        this.graph.selectOnlyEdge(edge);
        this.render();
      },
      addEdgeToSelection: edge => {
        this.graph.addSelectedEdge(edge);
        this.render();
      },
      toggleEdgeSelection: edge => {
        this.graph.toggleSelectedEdge(edge);
        this.render();
      },
      selectGraphElements: (nodeNames, edgeKeys, options) => {
        this.graph.selectElements(nodeNames, edgeKeys, options);
        this.render();
      },
      activateEdgeControl: (edge, control) => this.handleEdgeControl(edge, control),
      syncEdgeAngles: () => this.refreshEdgeAngles(),
      renderInspector: graph => this.renderInspector(graph),
      formatRank: value => GraphType.formatRank(value)
    });
  }

  async start() {
    this.bindTabs();
    this.bindToolbar();
    this.bindSelectionOverlay();
    this.bindMobileMenu();
    this.bindNodeForms();
    this.bindSearch();
    this.bindSubgraph();
    this.bindProjection();
    this.bindGraphSurface();

    await this.loadUiSettings();
    this.setSearchQueryTemplate("all");
    this.syncBasisInputs();
    this.renderTypeControls();
    this.render();

    const params = new URLSearchParams(this.window.location.search);
    const initialGlobalId = params.get("globalId");
    if (initialGlobalId) {
      void this.loadRoot(initialGlobalId);
    } else {
      void this.loadGraphRoots();
    }
  }

  async loadUiSettings() {
    try {
      const settings = await this.apiJson("/api/ui/settings");
      this.graph.applyUiSettings(settings);
    } catch (error) {
      this.setStatus(`Не удалось загрузить настройки UI: ${error.message}`);
    }

  }

  requireElement(selector) {
    const element = this.document.querySelector(selector);
    if (!element) {
      throw new Error(`GraphData UI element not found: ${selector}`);
    }

    return element;
  }

  bindTabs() {
    this.document.querySelectorAll<HTMLElement>(".tab-button").forEach(button => {
      button.addEventListener("click", () => this.setActiveTab(button.dataset.tab));
    });
  }

  bindToolbar() {
    this.fitButton.addEventListener("click", () => {
      this.fitView();
      this.runSimulation(40, () => this.fitView());
    });
    this.resetButton.addEventListener("click", () => {
      this.graph.resetGraph();
      this.expandedSelectionKeys.clear();
      this.render();
      this.setStatus("");
    });
  }

  bindSelectionOverlay() {
    this.selectionOverlayToggle.addEventListener("click", () => {
      this.selectionOverlayCollapsed = !this.selectionOverlayCollapsed;
      this.render();
    });
  }

  bindMobileMenu() {
    this.mobileMenuToggle.addEventListener("click", () => this.setMobileMenuOpen(true));
    this.mobileMenuClose.addEventListener("click", () => this.setMobileMenuOpen(false));
    this.document.addEventListener("keydown", event => {
      if (event.key === "Escape" && this.document.body.classList.contains("menu-open")) {
        this.setMobileMenuOpen(false);
      }
    });

    const desktopQuery = this.window.matchMedia("(min-width: 981px)");
    desktopQuery.addEventListener("change", event => {
      if (event.matches) {
        this.setMobileMenuOpen(false);
      }
    });
  }

  setMobileMenuOpen(isOpen) {
    this.document.body.classList.toggle("menu-open", isOpen);
    this.mobileMenuToggle.setAttribute("aria-expanded", String(isOpen));
    if (isOpen) {
      this.mobileMenuClose.focus();
    } else if (this.document.activeElement === this.mobileMenuClose) {
      this.mobileMenuToggle.focus();
    }
  }

  bindNodeForms() {
    this.clearSelectionButton.addEventListener("click", () => {
      this.graph.clearSelection();
      this.expandedSelectionKeys.clear();
      this.render();
    });
    this.deleteSelectedNodesButton.addEventListener("click", () => void this.deleteSelectedNodes());
    this.assignNodeType.addEventListener("change", () => this.updateEditorState());
    this.assignEdgeType.addEventListener("change", () => this.updateEditorState());
    this.connectTargetName.addEventListener("input", () => this.updateEditorState());

    this.createNodeForm.addEventListener("submit", event => {
      event.preventDefault();
      const name = this.createNodeName.value.trim();
      if (!name) {
        this.setStatus("Введите LocalId нового узла");
        return;
      }

      void this.createNode(name, {
        parentGlobalId: this.createNodeParent.value.trim(),
        typeGlobalId: this.createNodeType.value
      });
    });

    this.connectForm.addEventListener("submit", event => {
      event.preventDefault();
      const selectedNodeNames = this.selectedNodeNames();
      const sourceGlobalId = selectedNodeNames.length === 1 ? selectedNodeNames[0] : null;
      const targetGlobalId = this.connectTargetName.value.trim();
      if (!sourceGlobalId || !targetGlobalId) {
        this.setStatus("Выберите ровно один узел и укажите цель связи");
        return;
      }

      void this.connectNodes(sourceGlobalId, targetGlobalId, {
        typeGlobalId: this.connectEdgeType.value,
        relationLocalId: this.connectEdgeName.value.trim()
      });
    });
  }

  bindSearch() {
    this.document.querySelectorAll<HTMLElement>("[data-query-template]").forEach(button => {
      button.addEventListener("click", () => this.setSearchQueryTemplate(button.dataset.queryTemplate));
    });

    this.searchForm.addEventListener("submit", event => {
      event.preventDefault();
      void this.searchNodes();
    });

    this.searchStopButton.addEventListener("click", () => {
      this.graph.searchAbort?.abort();
    });
  }

  bindSubgraph() {
    this.subgraphForm.addEventListener("submit", event => {
      event.preventDefault();
      void this.loadSubgraph();
    });
  }

  bindProjection() {
    this.loadBasisButton.addEventListener("click", () => void this.loadBasis());
    this.ensureBasisButton.addEventListener("click", () => void this.ensureDefaultBasis());
    this.refreshTypesButton.addEventListener("click", () => void this.refreshTypes());
    this.loadRelationsButton.addEventListener("click", () => void this.loadRelationInstances());
    this.assignNodeTypeButton.addEventListener("click", () => void this.assignSelectedNodeType());
    this.assignEdgeTypeButton.addEventListener("click", () => void this.assignSelectedEdgeType());
  }


  async loadRoot(name) {
  if (!name) {
    this.setStatus("Введите GlobalId узла");
    return;
  }

  this.graph.rootName = name;
  this.graph.selectedName = name;
  this.graph.loaded.clear();
  this.graph.parentByNode.clear();
  this.graph.positions.clear();
  this.graph.velocities.clear();
  await this.loadNode(name, null);
  const url = new URL(this.window.location.href);
  url.searchParams.set("globalId", this.graph.rootName ?? name);
  this.window.history.replaceState({}, "", url);
  this.fitView();

  }

  async loadGraphRoots() {
  this.setBusy(true);
  this.setEmptyState("Загрузка корней...");
  try {
    const response = await this.loadSubgraphForRoots([], 0);
    const nodes = response.nodes ?? [];
    this.loadSubgraphIntoViewer(response, [], { selectRoot: false });
    this.renderSubgraphResults(response);
    this.renderTypeControls();
    this.setEmptyState(nodes.length === 0 ? "Корневые узлы не найдены" : "Узел не выбран");
    this.setStatus("");
  } catch (error) {
    this.graph.resetGraph();
    this.setEmptyState(error.message || "Не удалось загрузить корневые узлы");
    this.render();
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  }

  async loadNode(name, fromName, options: any = {}) {
  const select = options.select ?? true;
  this.setBusy(true);
  try {
    const expansion = this.normalizeNodeResponse(await this.apiJson(`/api/graph/nodes?${this.toGlobalIdQuery(name)}`));
    this.storeNodeExpansion(expansion, fromName, { select });

    this.render();
    this.renderTypeControls();
    this.setStatus(`Развернуто узлов: ${this.graph.visibleNodeCount()}`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  }

  async loadNeighbor(anchorName, neighborLocalId) {
  if (!anchorName || !neighborLocalId) {
    this.setStatus("Не удалось определить соседа для раскрытия");
    return;
  }

  this.setBusy(true);
  try {
    const expansion = this.normalizeNodeResponse(await this.apiJson(
      `/api/graph/nodes/${encodeURIComponent(anchorName)}/neighbor/${encodeURIComponent(neighborLocalId)}`));
    const alreadyLoaded = this.graph.loaded.has(expansion.name);
    this.storeNodeExpansion(expansion, anchorName, { select: false, showed: false });
    this.revealLoadedNode(expansion.name, anchorName, { select: true });

    if (alreadyLoaded) {
      this.setStatus(`Узел "${expansion.displayName}" уже был загружен, связь добавлена`);
    }
  } catch (error) {
    this.setStatus(this.formatNeighborError(error, neighborLocalId));
  } finally {
    this.setBusy(false);
  }

  }

  storeNodeExpansion(expansion, fromName, options: any = {}) {
  const select = options.select ?? true;
  const incoming = GraphNode.from(expansion);
  const existing = this.graph.loaded.get(incoming.name);
  const shouldShow = options.showed !== false
    || Boolean(existing && existing.showed !== false && this.graph.positions.has(incoming.name));
  incoming.showed = shouldShow;
  const stored = existing ? this.mergeNodeResponses(existing, incoming) : incoming;
  stored.showed = shouldShow;
  this.graph.loaded.set(incoming.name, stored);

  if (!fromName && this.graph.rootName && !this.graph.loaded.has(this.graph.rootName)) {
    this.graph.rootName = incoming.name;
  }

  if (select) {
    this.graph.selectedName = incoming.name;
  }

  if (stored.showed === false) {
    this.graph.positions.delete(incoming.name);
    this.graph.velocities.delete(incoming.name);
  } else {
    this.seedPosition(incoming.name, fromName, 0, this.edgeAngleFromAnchor(fromName, incoming.name));
  }

  if (stored.showed !== false && fromName && fromName !== incoming.name && !this.graph.parentByNode.has(incoming.name)) {
    this.graph.parentByNode.set(incoming.name, fromName);
  }

  this.refreshEdgeAngles();

  return stored;

  }

  mergeNodeResponses(existing, expansion) {
  return existing.merge(expansion);

  }

  mergeEdges(left = [], right = []) {
  return GraphEdge.mergeMany(left, right);

  }

  async createNode(name, options: any = {}) {
  const parentGlobalId = options.parentGlobalId || null;
  const typeGlobalId = options.typeGlobalId || "";
  this.setBusy(true);
  try {
    const created = await this.createGraphNode(name, parentGlobalId);
    if (typeGlobalId) {
      await this.assignGraphNodeType(created.globalId, typeGlobalId);
    }
    this.createNodeName.value = "";
    this.createNodeParent.value = "";
    this.graph.rootName = this.graph.rootName ?? created.name;
    this.graph.selectedName = created.name;
    const seedFromName = this.graph.rootName === created.name ? null : this.graph.rootName;
    const seedIndex = this.graph.visibleNodeCount();
    this.graph.loaded.set(created.name, created);
    this.seedPosition(created.name, seedFromName, seedIndex);
    if (typeGlobalId) {
      const expanded = this.normalizeNodeResponse(await this.apiJson(`/api/graph/nodes?${this.toGlobalIdQuery(created.globalId)}`));
      this.storeNodeExpansion(expanded, null, { select: true });
    }
    this.render();
    this.renderTypeControls();
    this.setActiveTab("operations");
    this.setStatus(`Создан узел "${created.displayName}"`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  }

  async createGraphNode(localId, parentGlobalId = null, attributes = null) {
  return this.normalizeNodeResponse(await this.apiJson("/api/graph/nodes", {
    method: "POST",
    body: JSON.stringify({
      localId,
      parentGlobalId: parentGlobalId ? this.parseGlobalId(parentGlobalId) : null,
      attributes
    })
  }));

  }

  async saveNodeAttributes(nodeName, attributes) {
  if (!nodeName || !this.graph.loaded.has(nodeName)) {
    this.setStatus("Узел не загружен");
    return;
  }

  this.setBusy(true);
  try {
    await this.updateGraphNodeAttributes(nodeName, attributes);
    await this.refreshLoadedNodeForEditing(nodeName);
    this.render();
    this.renderTypeControls();
    this.setStatus(`Сохранен узел "${this.displayName(nodeName)}"`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  }

  async deleteSelectedNodes() {
  const nodeNames = this.selectedNodeNames();
  if (nodeNames.length === 0) {
    this.setStatus("Нет выбранных узлов для удаления");
    return;
  }

  this.setBusy(true);
  try {
    for (const nodeName of nodeNames) {
      await this.apiJson(`/api/graph/nodes?${this.toGlobalIdQuery(nodeName)}`, {
        method: "DELETE",
        expectJson: false
      });
      this.removeLocalNode(nodeName, false);
      this.expandedSelectionKeys.delete(this.selectionKeyForNode(nodeName));
    }
    this.render();
    this.renderTypeControls();
    this.setStatus(`Удалено узлов: ${nodeNames.length}`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  }

  async connectNodes(sourceGlobalId, targetGlobalId, options: any = {}) {
  const typeGlobalId = options.typeGlobalId || "";
  this.setBusy(true);
  try {
    if (typeGlobalId) {
      const subgraph = await this.changeGraphEdgeType({
        sourceGlobalId,
        targetGlobalId,
        relationGlobalId: null
      }, typeGlobalId, { relationLocalId: options.relationLocalId || "" });
      this.mergeSubgraphIntoViewer(subgraph, { select: false });
      this.graph.selectedName = sourceGlobalId;
      this.selectReturnedRelationEdges(subgraph);
    } else {
      await this.connectGraphNodes(sourceGlobalId, targetGlobalId);
      await this.loadNode(sourceGlobalId, null, { select: true });
    }
    this.connectTargetName.value = "";
    this.connectEdgeName.value = "";
    this.render();
    this.renderTypeControls();
    this.setStatus(`Связаны "${this.displayName(sourceGlobalId)}" и "${this.displayName(targetGlobalId)}"`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  }

  async connectGraphNodes(sourceGlobalId, targetGlobalId) {
  await this.apiJson("/api/graph/connections", {
    method: "POST",
    body: JSON.stringify({
      sourceGlobalId: this.parseGlobalId(sourceGlobalId),
      targetGlobalId: this.parseGlobalId(targetGlobalId)
    }),
    expectJson: false
  });

  }

  async loadBasis() {
  const basisName = this.basisNodeInput.value.trim();
  if (!basisName) {
    this.readBasisInputs();
    await this.refreshTypes();
    this.render();
    return;
  }

  this.setBusy(true);
  try {
    const basisNode = this.normalizeNodeResponse(await this.apiJson(`/api/graph/nodes?${this.toGlobalIdQuery(basisName)}`));
    const defaultBasis = this.graph.defaultBasis();
    this.graph.schema.basis = {
      nodeTypeRoot: basisNode.attributes?.nodeTypeRoot || defaultBasis.nodeTypeRoot,
      edgeTypeRoot: basisNode.attributes?.edgeTypeRoot || defaultBasis.edgeTypeRoot,
      relationRoot: basisNode.attributes?.relationRoot || defaultBasis.relationRoot
    };
    this.syncBasisInputs();
    this.storeNodeExpansion(basisNode, null, { select: false });
    await this.refreshTypes({ preserveBusy: true });
    this.render();
    this.setStatus(`Базис загружен: ${basisName}`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  }

  async ensureDefaultBasis() {
  this.setBusy(true);
  try {
    this.readBasisInputs();
    const basis = this.getBasis();
    await this.ensurePath(basis.nodeTypeRoot, { [graphKindAttribute]: "type-root", [graphElementAttribute]: "node" });

    await this.upsertGraphType(basis.nodeTypeRoot, "Type", "Type", "#334155", "node", false, 90);
    await this.upsertGraphType(basis.nodeTypeRoot, "Instance", "Instance", "#0f766e", "node", false, 70);
    await this.upsertGraphType(basis.nodeTypeRoot, "Relation", "Relation", "#7c2d12", "edge", true, 60);
    await this.upsertGraphType(basis.nodeTypeRoot, "Endpoint", "Endpoint", "#0f766e", "node", false, 45);
    await this.upsertGraphType(basis.nodeTypeRoot, "Port", "Port", "#0f766e", "node", false, 45);

    const basisName = this.basisNodeInput.value.trim();
    if (basisName) {
      const segments = this.parseGlobalId(basisName);
      const localId = segments[segments.length - 1];
      const parent = segments.length > 1 ? segments.slice(0, -1).join("/") : null;
      if (parent) {
        await this.ensurePath(parent);
      }
      await this.createGraphNode(localId, parent, {
        [graphKindAttribute]: "basis",
        nodeTypeRoot: basis.nodeTypeRoot,
        edgeTypeRoot: basis.edgeTypeRoot,
        relationRoot: basis.relationRoot
      });
    }

    await this.refreshTypes({ preserveBusy: true });
    this.render();
    this.setStatus("Базовый базис создан или обновлен");
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  }

  async refreshTypes(options: any = {}) {
  if (!options.preserveBusy) {
    this.setBusy(true);
  }

  try {
    this.readBasisInputs();
    const roots = this.basisTypeRoots();
    this.graph.schema.nodeTypes = new Map();
    this.graph.schema.edgeTypes = new Map();

    if (roots.length === 0) {
      this.rebuildProjectionFromBasis("basis-types-cleared");
      this.renderTypeControls();
      this.setStatus("Корень типов не задан");
      return;
    }

    const subgraphs = await Promise.all(roots.map(root => this.loadSubgraphForRoots([root], 2)));
    const basisGraph = this.collectBasisGraph(subgraphs);
    this.storeBasisGraphNodes(basisGraph);

    for (const node of basisGraph.nodes.values()) {
      const element = this.typeElementFromBasisNode(node, basisGraph.edgePairs);
      if (!element) {
        continue;
      }

      if (element === "edge") {
        this.graph.schema.edgeTypes.set(node.globalId, GraphType.fromNode(node, "edge"));
      } else {
        this.graph.schema.nodeTypes.set(node.globalId, GraphType.fromNode(node, "node"));
      }
    }

    this.rebuildProjectionFromBasis("basis-types-loaded");
    this.renderTypeControls();
    this.setStatus(`Типы из корня: ${this.graph.schema.nodeTypes.size} узлов, ${this.graph.schema.edgeTypes.size} связей`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    if (!options.preserveBusy) {
      this.setBusy(false);
    }
  }

  }

  basisTypeRoots(): string[] {
  const basis = this.getBasis();
  return [basis.nodeTypeRoot, basis.edgeTypeRoot]
    .map(root => String(root ?? "").trim())
    .filter((root, index, roots) => root && roots.indexOf(root) === index);

  }

  collectBasisGraph(subgraphs) {
  const nodes = new Map();
  const edgePairs = new Set();
  const addEdgePair = edge => {
    const normalized = this.normalizeEdgeResponse(edge);
    if (normalized.sourceGlobalId && normalized.targetGlobalId) {
      edgePairs.add(GraphEdge.keyFor(normalized.sourceGlobalId, normalized.targetGlobalId));
    }
  };

  subgraphs.forEach(subgraph => {
    (subgraph.nodes ?? []).forEach(nodeResponse => {
      const node = this.normalizeNodeResponse(nodeResponse);
      nodes.set(node.globalId, node);
      (node.edges ?? []).forEach(edge => addEdgePair(edge));
    });
    (subgraph.edges ?? []).forEach(edge => addEdgePair(edge));
  });

  return { nodes, edgePairs };

  }

  storeBasisGraphNodes(basisGraph) {
  for (const node of basisGraph.nodes.values()) {
    const existing = this.graph.loaded.get(node.name);
    const stored = existing ? this.mergeNodeResponses(existing, node) : GraphNode.from(node);
    this.graph.loaded.set(stored.name, stored);
    if (stored.showed !== false && !this.graph.positions.has(stored.name) && !this.graph.isSchemaRoot(stored.name)) {
      this.seedPosition(stored.name, this.graph.rootName, this.graph.visibleNodeCount());
    }
  }

  this.refreshEdgeAngles();

  }

  typeElementFromBasisNode(node, edgePairs): "node" | "edge" | null {
  const globalId = node.globalId;
  if (!globalId || this.graph.isSchemaRoot(globalId) || this.isSystemTypeRoot(globalId)) {
    return null;
  }

  const baseTypeIds = this.graph.schema.baseTypeIds ?? {};
  const element = node.attributes?.[graphElementAttribute];
  if (element === "edge" || element === "relation") {
    return "edge";
  }
  if (element === "node") {
    return "node";
  }

  if (globalId === baseTypeIds.edgeType || globalId === baseTypeIds.relation) {
    return "edge";
  }
  if (this.hasBasisEdge(edgePairs, globalId, baseTypeIds.edgeType)
      || this.hasBasisEdge(edgePairs, globalId, baseTypeIds.relation)) {
    return "edge";
  }
  if (globalId === baseTypeIds.nodeType || this.hasBasisEdge(edgePairs, globalId, baseTypeIds.nodeType)) {
    return "node";
  }

  return this.isDirectBasisType(globalId) ? "node" : null;

  }

  hasBasisEdge(edgePairs, left, right) {
  return Boolean(left && right && edgePairs.has(GraphEdge.keyFor(left, right)));

  }

  isDirectBasisType(globalId) {
  return this.basisTypeRoots().some(root => {
    const rootSegments = root.split("/").filter(Boolean);
    const segments = String(globalId).split("/").filter(Boolean);
    return segments.length === rootSegments.length + 1
      && rootSegments.every((segment, index) => segment === segments[index]);
  });

  }

  isSystemTypeRoot(globalId) {
  const systemIds = this.graph.schema.systemNodeIds ?? {};
  return globalId === systemIds.typeRoot
    || globalId === systemIds.graphDataRoot
    || globalId === systemIds.nodeTypeRoot
    || globalId === systemIds.edgeTypeRoot
    || globalId === systemIds.relationRoot;

  }

  rebuildProjectionFromBasis(reason) {
  this.graph.rebuildProjection({ emit: true, reason });
  this.dispatchProjectionRebuilt(reason);
  this.render();
  this.renderProjectionSummary();

  }

  dispatchProjectionRebuilt(reason) {
  const CustomEventCtor = (this.window as any)?.CustomEvent;
  if (typeof CustomEventCtor !== "function") {
    return;
  }

  this.document.dispatchEvent(new CustomEventCtor("graph-projection-rebuilt", {
    detail: {
      reason,
      revision: this.graph.projectionRevision,
      primitiveGraph: this.graph.primitiveGraphCache,
      intermediateGraph: this.graph.intermediateGraph
    }
  }));

  }

  async loadRelationInstances() {
  this.setBusy(true);
  try {
    this.readBasisInputs();
    const matches = await this.searchNodeMatches({
      return: ["n"],
      where: {
        kind: "attribute",
        node: this.variableSelector("n"),
        key: graphKindAttribute,
        operator: "equals",
        value: "edge-instance"
      },
      limit: 500
    });
    const relationIds = matches
      .map(match => this.normalizeNodeResponse(match.node).globalId);

    for (const relationId of relationIds) {
      const subgraph = await this.loadSubgraphForRoots([relationId], 2);
      this.mergeSubgraphIntoViewer(subgraph, { select: false });
    }

    this.rebuildProjectionFromBasis("relation-instances-loaded");
    this.renderTypeControls();
    this.setStatus(`Инстансы связей загружены: ${relationIds.length}`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  }

  async assignSelectedNodeType() {
  const nodeNames = this.selectedNodeNames();
  const typeGlobalId = this.assignNodeType.value;
  if (nodeNames.length === 0 || !typeGlobalId) {
    this.setStatus("Выберите узлы и тип узла");
    return;
  }

  await this.assignNodeTypeToNodes(nodeNames, typeGlobalId);

  }

  async assignNodeTypeToNodes(nodeNames, typeGlobalId) {
  if (!nodeNames?.length || !typeGlobalId) {
    this.setStatus("Выберите узлы и тип узла");
    return;
  }

  this.setBusy(true);
  try {
    for (const nodeName of nodeNames) {
      const subgraph = await this.assignGraphNodeType(nodeName, typeGlobalId);
      this.mergeSubgraphIntoViewer(subgraph, { select: false });
    }
    this.render();
    this.renderTypeControls();
    this.setStatus(`Тип ${this.displayName(typeGlobalId)} назначен узлам: ${nodeNames.length}`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  }

  async assignGraphNodeType(internalId, typeGlobalId) {
  return this.apiJson("/api/graph/nodes/type", {
    method: "PUT",
    body: JSON.stringify({
      internalId: this.parseGlobalId(internalId),
      typeGlobalId: this.parseGlobalId(typeGlobalId)
    })
  });

  }

  async assignSelectedEdgeType() {
  const edges = this.selectedEdgeObjects();
  const typeGlobalId = this.assignEdgeType.value;
  if (edges.length === 0 || !typeGlobalId) {
    this.setStatus("Выберите связи и тип связи");
    return;
  }

  await this.changeEdgeTypeForEdges(edges, typeGlobalId);

  }

  async changeEdgeTypeForEdges(edges, typeGlobalId) {
  if (!edges?.length || !typeGlobalId) {
    this.setStatus("Выберите связи и тип связи");
    return;
  }

  const nextEdgeKeys = [];
  this.setBusy(true);
  try {
    for (const edge of edges) {
      const subgraph = await this.changeGraphEdgeType(edge, typeGlobalId);
      this.graph.removeSelectedEdge(edge.key);
      this.removeLocalEdge(edge.sourceGlobalId, edge.targetGlobalId);
      if (edge.relationGlobalId) {
        this.removeLocalRelationSubgraph(edge.relationGlobalId);
      }
      this.mergeSubgraphIntoViewer(subgraph, { select: false });
      nextEdgeKeys.push(...this.projectedEdgeKeysFromSubgraph(subgraph));
    }
    nextEdgeKeys.forEach(key => this.graph.selectedEdgeKeys.add(key));
    this.render();
    this.renderTypeControls();
    this.setStatus(`Тип ${this.displayName(typeGlobalId)} назначен связям: ${edges.length}`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  }

  async changeGraphEdgeType(edge, typeGlobalId, options: any = {}) {
  const relationRoot = this.getBasis().relationRoot?.trim();
  return this.apiJson("/api/graph/edges/type", {
    method: "PUT",
    body: JSON.stringify({
      relationGlobalId: edge.relationGlobalId ? this.parseGlobalId(edge.relationGlobalId) : null,
      sourceGlobalId: edge.sourceGlobalId ? this.parseGlobalId(edge.sourceGlobalId) : null,
      targetGlobalId: edge.targetGlobalId ? this.parseGlobalId(edge.targetGlobalId) : null,
      typeGlobalId: this.parseGlobalId(typeGlobalId),
      relationParentGlobalId: relationRoot ? this.parseGlobalId(relationRoot) : null,
      relationLocalId: options.relationLocalId || null
    })
  });

  }

  async upsertGraphType(rootGlobalId, localId, label, color, element, directed = false, rank = element === "node" ? 50 : 30) {
  const attrs: any = {
    [graphKindAttribute]: "type",
    [graphElementAttribute]: element,
    label,
    color,
    [projectionRankAttribute]: String(rank)
  };
  if (element === "edge") {
    attrs.directed = directed ? "true" : "false";
  }
  return this.createGraphNode(localId, rootGlobalId, attrs);

  }

  async ensurePath(globalId, leafAttributes = null) {
  const segments = this.parseGlobalId(globalId);
  let parent = null;
  let current = "";
  for (let index = 0; index < segments.length; index += 1) {
    const segment = segments[index];
    current = current ? `${current}/${segment}` : segment;
    const attrs = index === segments.length - 1 ? leafAttributes : null;
    await this.createGraphNode(segment, parent, attrs);
    parent = current;
  }

  }

  async searchNodeMatches(query) {
  const response = await this.api.fetch("/api/graph/search/nodes", {
    method: "POST",
    headers: {
      "Accept": "application/x-ndjson",
      "Content-Type": "application/json"
    },
    body: JSON.stringify(query)
  });

  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `HTTP ${response.status}`);
  }

  const matches = [];
  await this.readNdjsonStream(response, match => matches.push(match));
  return matches;

  }

  setSearchQueryTemplate(name) {
  const currentName = this.graph.selectedName || this.graph.rootName || "node-name";
  const templates = {
    all: {
      return: ["n"],
      where: {
        kind: "node",
        node: this.variableSelector("n")
      },
      limit: 50
    },
    text: {
      return: ["n"],
      where: {
        kind: "text",
        node: this.variableSelector("n"),
        value: "sample"
      },
      limit: 50
    },
    connected: {
      return: ["n"],
      where: {
        kind: "connected",
        left: this.variableSelector("n"),
        right: this.literalSelector(currentName || "node-name")
      },
      limit: 50
    },
    neighborById: {
      return: ["n", "x"],
      where: {
        kind: "all",
        expressions: [
          {
            kind: "connected",
            left: this.variableSelector("n"),
            right: this.variableSelector("x")
          },
          {
            kind: "attribute",
            node: this.variableSelector("x"),
            key: "id",
            operator: "equals",
            value: "Y"
          }
        ]
      },
      limit: 50
    },
    neighborConnected: {
      return: ["n", "x", "z"],
      where: {
        kind: "all",
        expressions: [
          {
            kind: "connected",
            left: this.variableSelector("n"),
            right: this.variableSelector("x")
          },
          {
            kind: "connected",
            left: this.variableSelector("x"),
            right: this.variableSelector("z")
          }
        ]
      },
      limit: 50
    },
    isolated: {
      return: ["x"],
      where: {
        kind: "all",
        expressions: [
          {
            kind: "node",
            node: this.variableSelector("x")
          },
          {
            kind: "not",
            expression: {
              kind: "exists",
              variables: ["y"],
              expression: {
                kind: "connected",
                left: this.variableSelector("x"),
                right: this.variableSelector("y")
              }
            }
          }
        ]
      },
      limit: 50
    }
  };

  this.searchQueryJson.value = JSON.stringify(templates[name] ?? templates.all, null, 2);

  }

  variableSelector(name) {
  return { kind: "var", name };

  }

  literalSelector(name) {
  return { kind: "literal", name };

  }

  async searchNodes() {
  let query;
  try {
    query = JSON.parse(this.searchQueryJson.value.trim());
  } catch (error) {
    this.setStatus(`JSON: ${error.message}`);
    return;
  }

  this.graph.searchAbort?.abort();
  const controller = new AbortController();
  this.graph.searchAbort = controller;
  this.setSearchStreaming(true);
  this.renderSearchResults([]);

  let count = 0;
  try {
    const response = await this.api.fetch("/api/graph/search/nodes", {
      method: "POST",
      headers: {
        "Accept": "application/x-ndjson",
        "Content-Type": "application/json"
      },
      body: JSON.stringify(query),
      signal: controller.signal
    });

    if (!response.ok) {
      const text = await response.text();
      throw new Error(text || `HTTP ${response.status}`);
    }

    await this.readNdjsonStream(response, match => {
      count += 1;
      this.appendSearchResult(match);
      this.setStatus(`Найдено решений: ${count}`);
    });

    this.setStatus(`Найдено решений: ${count}`);
  } catch (error) {
    if (error.name === "AbortError") {
      this.setStatus(`Поиск остановлен: ${count}`);
    } else {
      this.setStatus(error.message);
    }
  } finally {
    if (this.graph.searchAbort === controller) {
      this.graph.searchAbort = null;
      this.setSearchStreaming(false);
    }
  }

  }

  async readNdjsonStream(response, onItem) {
  if (!response.body) {
    this.parseNdjsonLines(await response.text(), onItem);
    return;
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  while (true) {
    const { value, done } = await reader.read();
    if (done) {
      break;
    }

    buffer += decoder.decode(value, { stream: true });
    const lines = buffer.split("\n");
    buffer = lines.pop() ?? "";
    this.parseNdjsonLines(lines.join("\n"), onItem);
  }

  buffer += decoder.decode();
  this.parseNdjsonLines(buffer, onItem);

  }

  parseNdjsonLines(text, onItem) {
  text
    .split("\n")
    .map(line => line.trim())
    .filter(Boolean)
    .forEach(line => onItem(JSON.parse(line)));

  }

  setSearchStreaming(value) {
  this.searchSubmitButton.disabled = value;
  this.searchStopButton.disabled = !value;

  }

  async loadSubgraph() {
  const roots = this.parseCsv((this.document.querySelector("#subgraph-roots") as HTMLInputElement).value);
  this.setBusy(true);
  try {
    const response = await this.apiJson("/api/graph/subgraph", {
      method: "POST",
      body: JSON.stringify({
        globalIds: roots.map(root => this.parseGlobalId(root)),
        maxDepth: this.readNumber("#subgraph-depth", 1),
        includeDisconnectedRoots: (this.document.querySelector("#subgraph-include-disconnected") as HTMLInputElement).checked
      })
    });
    this.loadSubgraphIntoViewer(response, roots);
    this.renderSubgraphResults(response);
    this.renderTypeControls();
    this.setStatus(`Подграф: ${(response.nodes ?? []).length} узлов, ${(response.edges ?? []).length} ребер`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  }

  loadSubgraphIntoViewer(response, roots, options: any = {}) {
  this.graph.loaded.clear();
  this.graph.parentByNode.clear();
  this.graph.positions.clear();
  this.graph.velocities.clear();
  const nodes = (response.nodes ?? []).map(node => this.normalizeNodeResponse(node));
  const edges = (response.edges ?? []).map(edge => this.normalizeEdgeResponse(edge));
  const edgesByNode = new Map();
  edges.forEach(edge => {
    [edge.sourceGlobalId, edge.targetGlobalId].forEach(name => {
      if (!edgesByNode.has(name)) {
        edgesByNode.set(name, []);
      }
      edgesByNode.get(name).push(edge);
    });
  });
  this.graph.rootName = roots[0] ?? nodes[0]?.name ?? null;
  this.graph.selectedName = options.selectRoot === false ? null : this.graph.rootName;

  nodes.forEach((node, index) => {
    this.graph.loaded.set(node.name, GraphNode.from({
      ...node,
      edges: this.mergeEdges(node.edges, edgesByNode.get(node.name) ?? [])
    }));
    this.seedSubgraphPosition(node.name, index, nodes.length);
  });

  this.refreshEdgeAngles();

  this.render();
  this.renderTypeControls();
  this.fitView();

  }

  mergeSubgraphIntoViewer(response, options: any = {}) {
  const select = options.select ?? false;
  const nodes = (response.nodes ?? []).map(node => this.normalizeNodeResponse(node));
  const edges = (response.edges ?? []).map(edge => this.normalizeEdgeResponse(edge));
  const edgesByNode = new Map();

  edges.forEach(edge => {
    [edge.sourceGlobalId, edge.targetGlobalId].forEach(name => {
      if (!edgesByNode.has(name)) {
        edgesByNode.set(name, []);
      }
      edgesByNode.get(name).push(edge);
    });
  });

  nodes.forEach((node, index) => {
    const expansion = GraphNode.from({
      ...node,
      edges: this.mergeEdges(node.edges, edgesByNode.get(node.name) ?? [])
    });
    this.storeNodeExpansion(expansion, options.fromName ?? null, {
      select: select && index === 0,
      showed: options.showed ?? true
    });
  });
  this.renderTypeControls();

  }

  removeLocalRelationSubgraph(relationId) {
  [...this.graph.loaded.keys()]
    .filter(name => name === relationId || GraphId.isChildOf(name, relationId))
    .sort((left, right) => right.length - left.length)
    .forEach(name => this.removeLocalNode(name, false, true));

  }

  removeLocalEdge(sourceGlobalId, targetGlobalId) {
  if (!sourceGlobalId || !targetGlobalId) {
    return;
  }

  const key = GraphEdge.keyFor(sourceGlobalId, targetGlobalId);
  for (const node of this.graph.loaded.values()) {
    node.edges = (node.edges ?? []).filter(edge => edge.key !== key);
  }
  this.graph.removeSelectedEdge(key);
  this.refreshEdgeAngles();

  }

  selectReturnedRelationEdges(response) {
  this.projectedEdgeKeysFromSubgraph(response).forEach(key => this.graph.selectedEdgeKeys.add(key));

  }

  projectedEdgeKeysFromSubgraph(response): string[] {
  return this.relationIdsFromSubgraph(response).map(relationId => `projected:${relationId}`);

  }

  relationIdsFromSubgraph(response): string[] {
  const relationIds = new Set<string>();
  const edgeTypeIds = new Set(this.graph.schema.edgeTypes.keys());
  for (const edge of response.edges ?? []) {
    if (edgeTypeIds.has(edge.sourceGlobalId) && !this.graph.isSchemaRoot(edge.targetGlobalId)) {
      relationIds.add(edge.targetGlobalId);
    } else if (edgeTypeIds.has(edge.targetGlobalId) && !this.graph.isSchemaRoot(edge.sourceGlobalId)) {
      relationIds.add(edge.sourceGlobalId);
    }
  }

  for (const relationId of (response.nodes ?? [])
    .map(node => this.normalizeNodeResponse(node))
    .filter(node => node.attributes?.[graphKindAttribute] === "edge-instance")
    .map(node => node.globalId)) {
    relationIds.add(relationId);
  }

  return [...relationIds];

  }

  async loadSubgraphForRoots(roots, maxDepth = 1) {
  return this.apiJson("/api/graph/subgraph", {
    method: "POST",
    body: JSON.stringify({
      globalIds: roots.map(root => this.parseGlobalId(root)),
      maxDepth
    })
  });

  }

  collapseTreeBranch(name) {
  if (!name || name === this.graph.rootName || !this.graph.loaded.has(name)) {
    return;
  }

  const fallbackSelection = this.graph.parentByNode.get(name) ?? this.graph.rootName;
  const removed = new Set();
  const visit = current => {
    removed.add(current);
    for (const [child, parent] of this.graph.parentByNode.entries()) {
      if (parent === current) {
        visit(child);
      }
    }
  };

  visit(name);
  removed.forEach(nodeName => this.removeLocalNode(nodeName, false, false));
  this.graph.selectedName = fallbackSelection;
  this.stopSimulation();
  this.render();
  this.setStatus(`Развернуто узлов: ${this.graph.visibleNodeCount()}`);

  }

  removeLocalNode(name, selectFallback = true, pruneEdges = true) {
  const wasSelected = this.graph.selectedName === name;
  const node = this.graph.loaded.get(name);
  if (pruneEdges) {
    this.graph.loaded.delete(name);
  } else if (node) {
    node.showed = false;
  }
  this.graph.removeSelectedName?.(name);
  this.graph.removeSelectedEdgesConnectedTo?.(name);
  if (pruneEdges) {
    this.graph.positions.delete(name);
    this.graph.velocities.delete(name);
  }
  this.graph.parentByNode.delete(name);
  for (const [child, parent] of [...this.graph.parentByNode.entries()]) {
    if (parent === name) {
      this.graph.parentByNode.delete(child);
    }
  }

  if (pruneEdges) {
    for (const expansion of this.graph.loaded.values()) {
      expansion.edges = (expansion.edges ?? [])
        .filter(edge => edge.sourceGlobalId !== name && edge.targetGlobalId !== name);
    }
  }
  this.refreshEdgeAngles();

  if (selectFallback && wasSelected) {
    this.graph.selectedName = this.graph.loaded.keys().next().value ?? null;
    this.graph.rootName = this.graph.selectedName;
  }

  }

  handleEndpointClick(edge, anchorName) {
  const control = this.graph.edgeEndpointControl(edge, anchorName);
  if (control) {
    this.handleEdgeControl(edge, control);
  }

  }

  handleEdgeControl(edge, control) {
  if (!control?.anchorName) {
    return;
  }

  const anchorName = control.anchorName;
  const otherName = control.otherName ?? GraphEdge.from(edge).otherEndpoint(anchorName);

  if (control.action === "load-neighbor") {
    if (otherName && this.graph.hasNode(otherName)) {
      this.revealLoadedNode(otherName, anchorName, { select: true });
      return;
    }

    this.loadNeighbor(anchorName, control.neighborLocalId ?? this.edgeNeighborLocalId(edge, anchorName));
    return;
  }

  if (control.action === "expand-edge") {
    this.stopSimulation();
    this.graph.expandEdge(edge);
    this.render();
    this.setStatus(`Развернута связь "${this.displayName(anchorName)}" - "${this.displayName(otherName)}"`);
    return;
  }

  if (control.action === "collapse-edge") {
    this.collapseEdge(edge, anchorName);
  }

  }

  revealLoadedNode(name, fromName, options: any = {}) {
  const node = this.graph.loaded.get(name);
  if (!node) {
    return false;
  }

  this.stopSimulation();
  this.storeNodeExpansion(node, fromName, {
    select: options.select ?? true,
    showed: true
  });
  this.render();
  this.renderTypeControls();
  this.setStatus(`Развернуто узлов: ${this.graph.visibleNodeCount()}`);
  return true;

  }

  collapseEdge(edge, anchorName) {
  const otherName = edge.sourceGlobalId === anchorName ? edge.targetGlobalId : edge.sourceGlobalId;
  const childName = this.graph.treeChildNameForEdge(edge, anchorName);

  if (childName) {
    this.collapseTreeBranch(childName);
    return;
  }

  this.graph.collapseEdge(edge);
  this.stopSimulation();
  this.render();
  this.setStatus(`Свернута связь "${this.displayName(anchorName)}" - "${this.displayName(otherName)}"`);

  }

  bindGraphSurface() {
    this.canvas.bindGraphSurface();
  }

  render() {
    this.canvas.render();
  }

  runSimulation(frames, onComplete = null) {
    this.canvas.runSimulation(frames, onComplete);
  }

  stopSimulation() {
    this.canvas?.stopSimulation?.();
  }

  fitView() {
    this.canvas.fitView();
  }

  applyView() {
    this.canvas.applyView();
  }

  screenToGraph(x, y) {
    return this.canvas.screenToGraph(x, y);
  }

  renderInspector(graph) {
  this.renderSelectionOverlay(graph);
  this.renderOperationPanel(graph);

  }

  renderOperationPanel(graph) {
  this.updateEditorState(graph);

  }

  renderSelectionOverlay(graph) {
  this.pruneSelectionToGraph(graph);
  const selectedNodes: any[] = this.selectedNodeObjects(graph)
    .sort((left, right) => this.displayName(left.name).localeCompare(this.displayName(right.name), "ru"));
  const selectedEdges: any[] = this.selectedEdgeObjects(graph)
    .sort((left, right) => this.edgeSelectionTitle(left).localeCompare(this.edgeSelectionTitle(right), "ru"));
  const total = selectedNodes.length + selectedEdges.length;

  this.selectionOverlay.hidden = total === 0;
  this.selectionOverlay.classList.toggle("collapsed", this.selectionOverlayCollapsed);
  this.selectionOverlayToggle.setAttribute("aria-expanded", String(!this.selectionOverlayCollapsed));
  this.selectionOverlayToggle.title = this.selectionOverlayCollapsed
    ? "Развернуть список выбранных элементов"
    : "Свернуть список выбранных элементов";
  this.selectionList.replaceChildren();
  this.selectionSummary.textContent = total === 0
    ? "Выбрано: 0"
    : `Выбрано: ${this.formatSelectionCount(selectedNodes.length, selectedEdges.length)}`;

  selectedNodes.forEach(node => {
    this.selectionList.append(this.createSelectionRow({
      key: this.selectionKeyForNode(node.name),
      kind: "Узел",
      title: node.displayName ?? node.name,
      subtitle: node.globalId ?? node.name,
      body: () => this.createNodeSelectionDetails(node),
      remove: () => {
        this.graph.removeSelectedName(node.name);
        this.expandedSelectionKeys.delete(this.selectionKeyForNode(node.name));
        this.render();
      }
    }));
  });

  selectedEdges.forEach(edge => {
    this.selectionList.append(this.createSelectionRow({
      key: this.selectionKeyForEdge(edge.key),
      kind: "Связь",
      title: this.edgeSelectionTitle(edge),
      subtitle: this.edgeSelectionSubtitle(edge),
      body: () => this.createEdgeSelectionDetails(edge),
      onExpand: () => {
        if (this.edgeEditableNodeId(edge) && !this.graph.loaded.has(this.edgeEditableNodeId(edge))) {
          void this.ensureEdgeRelationLoaded(edge);
        }
      },
      remove: () => {
        this.graph.removeSelectedEdge(edge.key);
        this.expandedSelectionKeys.delete(this.selectionKeyForEdge(edge.key));
        this.render();
      }
    }));
  });

  }

  createSelectionRow({ key, kind, title, subtitle, body, remove, onExpand = null }) {
  const expanded = this.expandedSelectionKeys.has(key);
  const row = this.document.createElement("div");
  row.className = `selection-row${expanded ? " expanded" : ""}`;

  const header = this.document.createElement("div");
  header.className = "selection-row-header";
  header.tabIndex = 0;
  header.setAttribute("role", "button");
  header.setAttribute("aria-expanded", String(expanded));
  const toggle = () => {
    if (expanded) {
      this.expandedSelectionKeys.delete(key);
    } else {
      this.expandedSelectionKeys.add(key);
      onExpand?.();
    }
    this.render();
  };
  header.addEventListener("click", toggle);
  header.addEventListener("keydown", event => {
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      toggle();
    }
  });

  const badge = this.document.createElement("span");
  badge.className = "selection-kind";
  badge.textContent = kind;

  const text = this.document.createElement("div");
  text.className = "selection-row-text";
  const titleElement = this.document.createElement("strong");
  titleElement.textContent = title;
  const subtitleElement = this.document.createElement("span");
  subtitleElement.textContent = subtitle;
  text.append(titleElement, subtitleElement);

  const removeButton = this.document.createElement("button");
  removeButton.type = "button";
  removeButton.className = "selection-remove-button";
  removeButton.textContent = "×";
  removeButton.title = "Снять выделение";
  removeButton.setAttribute("aria-label", `Снять выделение: ${title}`);
  removeButton.addEventListener("click", event => {
    event.preventDefault();
    event.stopPropagation();
    remove();
  });

  header.append(badge, text, removeButton);
  row.append(header);
  if (expanded) {
    row.append(body());
  }
  return row;

  }

  createNodeSelectionDetails(node) {
  const details = this.document.createElement("div");
  details.className = "selection-details";
  details.append(this.createDetailsList([
    ["GlobalId", node.globalId ?? node.name],
    ["Rank", node.viewRank === undefined ? "-" : GraphType.formatRank(node.viewRank)],
    ["Причина rank", node.viewRankReason || "-"]
  ]));

  const editor = this.createAttributeEditor(node.attributes ?? {});
  details.append(this.createDetailsSection("Атрибуты", editor.element, [
    this.createCompactButton("Добавить", () => editor.addRow("", "")),
    this.createCompactButton("Сохранить", () => void this.saveNodeAttributes(node.name, editor.read()))
  ]));

  const typeSelect = this.document.createElement("select");
  this.renderTypeSelect(typeSelect, this.graph.schema.nodeTypes, "Не менять тип");
  details.append(this.createDetailsSection("Тип узла", typeSelect, [
    this.createCompactButton("Назначить", () => void this.assignNodeTypeToNodes([node.name], typeSelect.value))
  ]));

  return details;

  }

  createEdgeSelectionDetails(edge) {
  const details = this.document.createElement("div");
  details.className = "selection-details";
  const relationId = this.edgeEditableNodeId(edge);
  details.append(this.createDetailsList([
    ["Источник", this.edgeEndpointDisplayName(edge, edge.sourceGlobalId)],
    ["Цель", this.edgeEndpointDisplayName(edge, edge.targetGlobalId)],
    ["Тип", edge.typeGlobalId ? this.displayName(edge.typeGlobalId) : "физическая связь"],
    ["Инстанс связи", relationId || "-"],
    ["Rank", edge.viewRank === undefined ? "-" : GraphType.formatRank(edge.viewRank)]
  ]));

  if (!relationId) {
    const note = this.document.createElement("div");
    note.className = "selection-note";
    note.textContent = "У физической связи нет relation-узла, поэтому атрибуты пока доступны только для чтения.";
    details.append(note);
    return details;
  }

  const relation = this.graph.loaded.get(relationId);
  if (!relation) {
    const note = this.document.createElement("div");
    note.className = "selection-note";
    note.textContent = "Атрибуты инстанса связи загружаются...";
    details.append(note);
    return details;
  }

  const editor = this.createAttributeEditor(relation.attributes ?? {});
  details.append(this.createDetailsSection("Атрибуты связи", editor.element, [
    this.createCompactButton("Добавить", () => editor.addRow("", "")),
    this.createCompactButton("Сохранить", () => void this.saveNodeAttributes(relationId, editor.read()))
  ]));
  return details;

  }

  createDetailsList(items) {
  const list = this.document.createElement("div");
  list.className = "selection-details-list";
  items.forEach(([label, value]) => {
    const row = this.document.createElement("div");
    const labelElement = this.document.createElement("span");
    labelElement.textContent = label;
    const valueElement = this.document.createElement("strong");
    valueElement.textContent = value;
    valueElement.title = value;
    row.append(labelElement, valueElement);
    list.append(row);
  });
  return list;

  }

  createDetailsSection(title, content, actions = []) {
  const section = this.document.createElement("div");
  section.className = "selection-details-section";
  const header = this.document.createElement("div");
  header.className = "section-heading";
  const titleElement = this.document.createElement("span");
  titleElement.className = "eyebrow";
  titleElement.textContent = title;
  const actionRow = this.document.createElement("div");
  actionRow.className = "button-row";
  actions.forEach(action => actionRow.append(action));
  header.append(titleElement, actionRow);
  section.append(header, content);
  return section;

  }

  createCompactButton(text, action, disabled = false) {
  const button = this.document.createElement("button");
  button.type = "button";
  button.className = "compact-button";
  button.textContent = text;
  button.disabled = Boolean(disabled);
  button.addEventListener("click", event => {
    event.preventDefault();
    event.stopPropagation();
    action();
  });
  return button;

  }

  edgeSelectionTitle(edge) {
  const source = this.edgeEndpointDisplayName(edge, edge.sourceGlobalId);
  const target = this.edgeEndpointDisplayName(edge, edge.targetGlobalId);
  return edge.directed ? `${source} -> ${target}` : `${source} - ${target}`;

  }

  edgeSelectionSubtitle(edge) {
  if (edge.label) {
    return edge.label;
  }

  if (edge.relationGlobalId) {
    return this.displayName(edge.relationGlobalId);
  }

  if (edge.typeGlobalId) {
    return this.displayName(edge.typeGlobalId);
  }

  return edge.projected ? "проекция связи" : "физическая связь";

  }

  formatSelectionCount(nodeCount, edgeCount) {
  const parts: string[] = [];
  if (nodeCount > 0) {
    parts.push(`${nodeCount} ${this.pluralRu(nodeCount, "узел", "узла", "узлов")}`);
  }
  if (edgeCount > 0) {
    parts.push(`${edgeCount} ${this.pluralRu(edgeCount, "связь", "связи", "связей")}`);
  }
  return parts.join(", ");

  }

  pluralRu(count, one, few, many) {
  const mod10 = count % 10;
  const mod100 = count % 100;
  if (mod10 === 1 && mod100 !== 11) {
    return one;
  }
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) {
    return few;
  }
  return many;

  }

  selectedNodeObjects(graph = null): any[] {
  const nodes: any[] = graph?.nodes ?? [...this.graph.loaded.values()].filter(node => node.showed !== false);
  const nodesByName = new Map<string, any>(nodes.map(node => [node.name, node]));
  return [...this.graph.selectedNames]
    .map(name => nodesByName.get(name))
    .filter(Boolean);

  }

  selectedNodeNames(graph = null): string[] {
  return this.selectedNodeObjects(graph).map(node => node.name);

  }

  selectedEdgeObjects(graph = null): any[] {
  const source = graph ?? this.graph.visibleGraph();
  const edges: any[] = source.edges ?? [];
  const edgesByKey = new Map<string, any>(edges.map(edge => [edge.key, edge]));
  return [...this.graph.selectedEdgeKeys]
    .map(key => edgesByKey.get(key))
    .filter(Boolean);

  }

  pruneSelectionToGraph(graph) {
  const nodeNames = new Set((graph.nodes ?? []).map(node => node.name));
  const edgeKeys = new Set((graph.edges ?? []).map(edge => edge.key));

  for (const name of [...this.graph.selectedNames]) {
    if (!nodeNames.has(name)) {
      this.graph.removeSelectedName(name);
      this.expandedSelectionKeys.delete(this.selectionKeyForNode(name));
    }
  }

  for (const key of [...this.graph.selectedEdgeKeys]) {
    if (!edgeKeys.has(key)) {
      this.graph.removeSelectedEdge(key);
      this.expandedSelectionKeys.delete(this.selectionKeyForEdge(key));
    }
  }

  const liveSelectionKeys = new Set([
    ...[...this.graph.selectedNames].map(name => this.selectionKeyForNode(name)),
    ...[...this.graph.selectedEdgeKeys].map(key => this.selectionKeyForEdge(key))
  ]);
  for (const key of [...this.expandedSelectionKeys]) {
    if (!liveSelectionKeys.has(key)) {
      this.expandedSelectionKeys.delete(key);
    }
  }

  }

  selectionKeyForNode(name) {
  return `node:${name}`;

  }

  selectionKeyForEdge(key) {
  return `edge:${key}`;

  }

  edgeEditableNodeId(edge) {
  return edge?.relationGlobalId || null;

  }

  async ensureEdgeRelationLoaded(edge) {
  const relationId = this.edgeEditableNodeId(edge);
  if (!relationId || this.graph.loaded.has(relationId)) {
    return;
  }

  try {
    await this.refreshLoadedNodeForEditing(relationId);
    this.render();
  } catch (error) {
    this.setStatus(error.message);
  }

  }

  async refreshLoadedNodeForEditing(globalId) {
  const expansion = this.normalizeNodeResponse(await this.apiJson(`/api/graph/nodes?${this.toGlobalIdQuery(globalId)}`));
  const incoming = GraphNode.from(expansion);
  const existing = this.graph.loaded.get(incoming.name);
  const showed = existing ? existing.showed !== false : false;
  const stored = existing ? this.mergeNodeResponses(existing, incoming) : incoming;
  stored.showed = showed;
  this.graph.loaded.set(incoming.name, stored);
  if (stored.showed === false) {
    this.graph.positions.delete(incoming.name);
    this.graph.velocities.delete(incoming.name);
  }
  this.refreshEdgeAngles();
  return stored;

  }

  createAttributeEditor(attributes) {
  const container = this.document.createElement("div");
  container.className = "attribute-editor";
  const addRow = (key, value) => this.addAttributeRow(container, key, value);
  const entries = Object.entries(attributes);
  if (entries.length === 0) {
    addRow("", "");
  } else {
    entries.forEach(([key, value]) => addRow(key, value));
  }

  return {
    element: container,
    addRow,
    read: () => this.readAttributeEditor(container)
  };

  }

  addAttributeRow(container, key, value) {
  const row = this.document.createElement("div");
  row.className = "attribute-row";
  const keyInput = this.document.createElement("input");
  keyInput.className = "attribute-key";
  keyInput.placeholder = "Ключ";
  keyInput.value = key;
  const valueInput = this.document.createElement("input");
  valueInput.className = "attribute-value";
  valueInput.placeholder = "Значение";
  valueInput.value = value;
  const removeButton = this.document.createElement("button");
  removeButton.type = "button";
  removeButton.className = "icon-button";
  removeButton.textContent = "×";
  removeButton.addEventListener("click", () => row.remove());
  row.append(keyInput, valueInput, removeButton);
  container.append(row);

  }

  readAttributeEditor(container) {
  const attributes = {};
  container.querySelectorAll(".attribute-row").forEach(row => {
    const key = row.querySelector(".attribute-key").value.trim();
    const value = row.querySelector(".attribute-value").value;
    if (key) {
      attributes[key] = value;
    }
  });
  return attributes;

  }

  renderSearchResults(matches) {
  this.searchResults.replaceChildren();
  matches.forEach(match => this.appendSearchResult(match));

  }

  appendSearchResult(match) {
  const node = this.normalizeNodeResponse(match.node);
  const button = this.document.createElement("button");
  button.type = "button";
  button.className = "result-row";
  button.innerHTML = `<strong></strong><span></span>`;
  button.querySelector("strong").textContent = node.displayName;
  const bindings = Object.entries(match.bindings ?? {})
    .map(([variable, binding]) => {
      const normalized = this.normalizeNodeResponse(binding);
      return `${variable}=${normalized.displayName}`;
    })
    .join(" · ");
  button.querySelector("span").textContent = bindings || `score ${match.score} ${match.matchedBy?.join(" ") ?? ""}`;
  button.title = node.globalId;
  button.addEventListener("click", () => {
    this.loadRoot(node.globalId);
    this.setActiveTab("operations");
  });
  this.searchResults.append(button);

  }

  renderSubgraphResults(response) {
  this.subgraphResults.replaceChildren();
  const nodes = (response.nodes ?? []).map(node => this.normalizeNodeResponse(node));
  const edges = response.edges ?? [];
  const summary = this.document.createElement("div");
  summary.className = "result-summary";
  summary.textContent = `${nodes.length} узлов, ${edges.length} ребер`;
  this.subgraphResults.append(summary);
  nodes.forEach(node => {
    const button = this.document.createElement("button");
    button.type = "button";
    button.className = "result-row";
    button.textContent = node.displayName;
    button.title = node.globalId;
    button.addEventListener("click", () => {
      this.graph.selectedName = node.name;
      this.setActiveTab("operations");
      this.render();
    });
    this.subgraphResults.append(button);
  });

  }

  renderTypeControls() {
  this.renderTypeSelect(this.createNodeType, this.graph.schema.nodeTypes, "Без типа");
  this.renderTypeSelect(this.assignNodeType, this.graph.schema.nodeTypes, "Не менять тип");
  this.renderTypeSelect(this.assignEdgeType, this.graph.schema.edgeTypes, "Не менять тип");
  this.renderTypeSelect(this.connectEdgeType, this.graph.schema.edgeTypes, "Физическая связь");
  this.renderTypeList(this.nodeTypeList, "Типы узлов", this.graph.schema.nodeTypes, "node");
  this.renderTypeList(this.edgeTypeList, "Типы связей", this.graph.schema.edgeTypes, "edge");
  this.renderRelationList();
  this.renderProjectionSummary();

  }

  renderTypeSelect(select, types, emptyLabel = "Выберите тип") {
  const current = select.value;
  select.replaceChildren();
  const empty = this.document.createElement("option");
  empty.value = "";
  empty.textContent = types.size === 0 ? "Типы не загружены" : emptyLabel;
  select.append(empty);
  [...types.values()]
    .sort((a, b) => a.label.localeCompare(b.label, "ru"))
    .forEach(type => {
      const option = this.document.createElement("option");
      option.value = type.globalId;
      option.textContent = type.label;
      select.append(option);
    });
  if (types.has(current)) {
    select.value = current;
  }

  }

  renderTypeList(container, title, types, element) {
  container.replaceChildren();
  const summary = this.document.createElement("div");
  summary.className = "result-summary";
  summary.textContent = `${title}: ${types.size}`;
  container.append(summary);
  if (types.size === 0) {
    return;
  }

  [...types.values()]
    .sort((a, b) => a.label.localeCompare(b.label, "ru"))
    .forEach(type => {
      const ruleKey = this.basisRuleKey(element, type.globalId);
      const expanded = this.expandedBasisRuleKeys.has(ruleKey);
      const row = this.document.createElement("div");
      row.className = "basis-rule";
      row.classList.toggle("expanded", expanded);
      row.title = type.globalId;

      const header = this.document.createElement("div");
      header.className = "type-row";
      const swatch = this.document.createElement("span");
      swatch.className = "type-swatch";
      swatch.style.background = type.color || "#9daab2";
      const toggle = this.document.createElement("button");
      toggle.type = "button";
      toggle.className = "result-row type-open-button basis-rule-toggle";
      toggle.textContent = `${expanded ? "[-]" : "[+]"} ${type.directed ? `${type.label} ->` : type.label}`;
      toggle.title = expanded ? "Свернуть настройки" : "Раскрыть настройки";
      toggle.setAttribute("aria-expanded", String(expanded));
      toggle.addEventListener("click", () => {
        if (expanded) {
          this.expandedBasisRuleKeys.delete(ruleKey);
        } else {
          this.expandedBasisRuleKeys.add(ruleKey);
        }
        this.renderTypeControls();
      });
      const open = this.document.createElement("button");
      open.type = "button";
      open.className = "compact-button";
      open.textContent = "Открыть";
      open.title = type.globalId;
      open.addEventListener("click", async event => {
        event.preventDefault();
        event.stopPropagation();
        await this.ensureNodeLoaded(type.globalId);
        this.revealLoadedNode(type.globalId, this.graph.rootName, { select: false });
        this.graph.selectedName = type.globalId;
        this.render();
        this.setActiveTab("operations");
      });
      header.append(swatch, toggle, open);

      const rules = this.document.createElement("div");
      rules.className = "basis-rule-grid";
      rules.hidden = !expanded;
      const visible = this.createCheckboxRule("Показывать", type.visible);
      const collapsed = this.createCheckboxRule("Сворачивать", type.collapsed !== false);
      const color = this.createTextRule("Цвет", type.color || "", "#0f766e");
      const rank = this.createTextRule("Ранг", GraphType.formatRankInput(type.rank), element === "node" ? "50" : "30");
      rules.append(visible.label, collapsed.label, color.label, rank.label);

      const extraControls: any = {};
      if (element === "node") {
        extraControls.info = this.createTextRule("Инфо атрибут", type.infoAttribute || "", "например: status");
        rules.append(extraControls.info.label);
      } else {
        extraControls.directed = this.createCheckboxRule("Стрелка", type.directed);
        extraControls.labelVisible = this.createCheckboxRule("Подпись", type.labelVisible);
        rules.append(extraControls.directed.label, extraControls.labelVisible.label);
      }

      const readRules = () => ({
        visible: visible.input.checked,
        collapsed: collapsed.input.checked,
        color: color.input.value.trim(),
        rank: rank.input.value.trim(),
        infoAttribute: extraControls.info?.input.value.trim() ?? "",
        directed: extraControls.directed?.input.checked ?? false,
        labelVisible: extraControls.labelVisible?.input.checked ?? true
      });

      const applyRules = () => {
        const rulesSnapshot = readRules();
        swatch.style.background = GraphType.normalizeColor(rulesSnapshot.color) || "#9daab2";
        this.applyTypeProjectionRules(type.globalId, element, rulesSnapshot);
      };

      [visible.input, collapsed.input, extraControls.directed?.input, extraControls.labelVisible?.input]
        .filter(Boolean)
        .forEach(input => input.addEventListener("change", applyRules));
      [color.input, rank.input, extraControls.info?.input]
        .filter(Boolean)
        .forEach(input => input.addEventListener("change", applyRules));

      const actions = this.document.createElement("div");
      actions.className = "button-row basis-rule-actions";
      const save = this.document.createElement("button");
      save.type = "button";
      save.className = "compact-button";
      save.textContent = "Сохранить";
      save.addEventListener("click", () => this.saveTypeProjectionRules(type.globalId, element, readRules()));
      actions.append(save);
      rules.append(actions);

      row.append(header, rules);
      container.append(row);
    });

  }

  basisRuleKey(element, globalId) {
  return `${element}:${globalId}`;

  }

  applyTypeProjectionRules(globalId, element, rules) {
  const typeMap = element === "node" ? this.graph.schema.nodeTypes : this.graph.schema.edgeTypes;
  const current = typeMap.get(globalId);
  if (!current) {
    return;
  }

  typeMap.set(globalId, new GraphType({
    ...current,
    element,
    visible: rules.visible,
    collapsed: rules.collapsed,
    color: rules.color,
    rank: GraphType.readRank(rules.rank, element === "edge" ? 30 : 50),
    infoAttribute: rules.infoAttribute,
    directed: rules.directed,
    labelVisible: rules.labelVisible,
    attributes: current.attributes
  }));
  this.rebuildProjectionFromBasis("basis-rule-change");

  }

  createCheckboxRule(text, checked) {
  const label = this.document.createElement("label");
  label.className = "check-line basis-rule-check";
  const input = this.document.createElement("input");
  input.type = "checkbox";
  input.checked = checked;
  const span = this.document.createElement("span");
  span.textContent = text;
  label.append(input, span);
  return { label, input };

  }

  createTextRule(text, value, placeholder) {
  const label = this.document.createElement("label");
  const span = this.document.createElement("span");
  span.textContent = text;
  const input = this.document.createElement("input");
  input.type = "text";
  input.value = value;
  input.placeholder = placeholder;
  label.append(span, input);
  return { label, input };

  }

  async saveTypeProjectionRules(globalId, element, rules) {
  this.setBusy(true);
  try {
    const loaded = await this.getLoadedNode(globalId);
    const attributes = { ...(loaded.attributes ?? {}) };
    attributes[graphKindAttribute] = attributes[graphKindAttribute] || "type";
    attributes[graphElementAttribute] = element;
    attributes[projectionVisibleAttribute] = rules.visible ? "true" : "false";
    attributes[projectionCollapsedAttribute] = rules.collapsed ? "true" : "false";
    if (rules.color) {
      attributes[projectionColorAttribute] = rules.color;
      attributes.color = rules.color;
    } else {
      delete attributes[projectionColorAttribute];
      delete attributes.color;
    }

    if (rules.rank) {
      attributes[projectionRankAttribute] = String(GraphType.readRank(rules.rank, element === "edge" ? 30 : 50));
    } else {
      delete attributes[projectionRankAttribute];
    }

    if (element === "node") {
      if (rules.infoAttribute) {
        attributes[projectionInfoAttribute] = rules.infoAttribute;
      } else {
        delete attributes[projectionInfoAttribute];
      }
    } else {
      attributes[projectionDirectedAttribute] = rules.directed ? "true" : "false";
      attributes.directed = rules.directed ? "true" : "false";
      attributes[projectionLabelVisibleAttribute] = rules.labelVisible ? "true" : "false";
    }

    await this.updateGraphNodeAttributes(globalId, attributes);
    loaded.attributes = attributes;
    this.graph.loaded.set(globalId, loaded);
    if (element === "node") {
      this.graph.schema.nodeTypes.set(globalId, GraphType.fromNode(loaded, "node"));
    } else {
      this.graph.schema.edgeTypes.set(globalId, GraphType.fromNode(loaded, "edge"));
    }
    this.rebuildProjectionFromBasis("basis-rule-saved");
    this.renderTypeControls();
    this.setStatus(`Правила сохранены: ${this.displayName(globalId)}`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  }

  renderRelationList() {
  this.typedEdgeList.replaceChildren();
  const graph = this.graph.physicalGraph();
  const relations = this.graph.discoverRelationInstances(graph);
  const summary = this.document.createElement("div");
  summary.className = "result-summary";
  summary.textContent = `Инстансы связей в загруженном графе: ${relations.length}`;
  this.typedEdgeList.append(summary);
  relations
    .sort((a, b) => a.relationGlobalId.localeCompare(b.relationGlobalId, "ru"))
    .forEach(relation => {
      const row = this.document.createElement("div");
      row.className = "basis-relation-row";
      const open = this.document.createElement("button");
      open.type = "button";
      open.className = "result-row type-open-button";
      open.textContent = `${relation.type?.label ?? "связь"}: ${this.displayName(relation.sourceGlobalId)} -> ${this.displayName(relation.targetGlobalId)}`;
      open.title = relation.relationGlobalId;
      open.addEventListener("click", () => {
        this.graph.selectedName = relation.relationGlobalId;
        this.render();
        this.setActiveTab("operations");
      });
      row.append(open);
      this.typedEdgeList.append(row);
    });

  }

  renderProjectionSummary() {
  const physical = this.graph.physicalGraph();
  const relations = this.graph.discoverRelationInstances(physical);
  const graph = this.graph.visibleGraph();
  const projectedRelations = graph.edges.filter(edge => edge.projected).length;
  const rankedNodes = graph.nodes.filter(node => Number.isFinite(node.viewRank));
  const topRank = rankedNodes.length === 0
    ? ""
    : ` Топ rank: ${GraphType.formatRank(Math.max(...rankedNodes.map(node => node.viewRank)))}.`;
  this.projectionSummary.textContent = `Проекция по типам: ${graph.nodes.length} узлов, ${graph.edges.length} связей. Кэш: ${physical.nodes.length} узлов, ${physical.edges.length} исходных связей, ${relations.length} типизированных конструкций, ${projectedRelations} свернутых.${topRank}`;

  }

  getBasis() {
  return this.graph.schema.basis;

  }

  readBasisInputs() {
  const defaultBasis = this.graph.defaultBasis();
  this.graph.schema.basis = {
    nodeTypeRoot: this.nodeTypeRootInput.value.trim() || defaultBasis.nodeTypeRoot,
    edgeTypeRoot: this.edgeTypeRootInput.value.trim() || defaultBasis.edgeTypeRoot,
    relationRoot: this.relationRootInput.value.trim() || defaultBasis.relationRoot
  };

  }

  syncBasisInputs() {
  this.nodeTypeRootInput.value = this.graph.schema.basis.nodeTypeRoot;
  this.edgeTypeRootInput.value = this.graph.schema.basis.edgeTypeRoot;
  this.relationRootInput.value = this.graph.schema.basis.relationRoot;

  }

  seedPosition(name, fromName, index, angleOverride = null) {
  if (this.graph.positions.has(name)) {
    return;
  }

  if (!fromName || !this.graph.positions.has(fromName)) {
    this.graph.positions.set(name, { x: 0, y: 0 });
    this.graph.velocities.set(name, { x: 0, y: 0 });
    return;
  }

  const source = this.graph.positions.get(fromName);
  const angle = Number.isFinite(angleOverride)
    ? angleOverride
    : index * 2.399963 + [...name].reduce((sum, char) => sum + char.charCodeAt(0), 0) * 0.017;
  const distance = nodeRadius * 3;
  this.graph.positions.set(name, {
    x: source.x + Math.cos(angle) * distance,
    y: source.y + Math.sin(angle) * distance
  });
  this.graph.velocities.set(name, { x: 0, y: 0 });

  }

  seedSubgraphPosition(name, index, count) {
  const radius = Math.max(nodeRadius * 3, Math.min(nodeRadius * 8, count * nodeRadius));
  const angle = count <= 1 ? 0 : (Math.PI * 2 * index) / count;
  this.graph.positions.set(name, {
    x: Math.cos(angle) * radius,
    y: Math.sin(angle) * radius
  });
  this.graph.velocities.set(name, { x: 0, y: 0 });

  }

  formatNeighborError(error, neighborLocalId) {
  if (error.status === 404) {
    return `Сосед "${neighborLocalId}" не найден`;
  }

  if (error.status === 409) {
    return `Сосед "${neighborLocalId}" неоднозначен`;
  }

  return error.message;

  }

  setActiveTab(name) {
  this.document.querySelectorAll<HTMLElement>(".tab-button").forEach(button => {
    button.classList.toggle("active", button.dataset.tab === name);
  });
  this.document.querySelectorAll(".tab-panel").forEach(panel => {
    panel.classList.toggle("active", panel.id === `tab-${name}`);
  });

  }

  parseCsv(value) {
  return value
    .split(",")
    .map(item => item.trim())
    .filter(Boolean);

  }

  readNumber(selector, fallback) {
  const value = Number.parseInt(this.document.querySelector(selector).value, 10);
  return Number.isFinite(value) ? value : fallback;

  }

  setStatus(message) {
  this.statusOutput.value = message;
  this.statusOutput.textContent = message;

  }

  setEmptyState(message) {
  this.emptyTitle.textContent = message;

  }

  setBusy(value) {
  this.graph.busy = value;
  this.document.querySelectorAll("button").forEach(button => {
    if (!button.classList.contains("tab-button")) {
      button.disabled = value;
    }
  });
  if (!value) {
    this.updateEditorState();
  }

  }

  updateEditorState(graph = null) {
  const nodeCount = this.selectedNodeObjects(graph).length;
  const edgeCount = this.selectedEdgeObjects(graph).length;
  const total = nodeCount + edgeCount;
  this.clearSelectionButton.disabled = this.graph.busy || total === 0;
  this.deleteSelectedNodesButton.disabled = this.graph.busy || nodeCount === 0;
  this.assignNodeTypeButton.disabled = this.graph.busy || nodeCount === 0 || !this.assignNodeType.value;
  this.assignEdgeTypeButton.disabled = this.graph.busy || edgeCount === 0 || !this.assignEdgeType.value;
  this.connectForm.querySelector("button").disabled = this.graph.busy
    || nodeCount !== 1
    || !this.connectTargetName.value.trim();
  if (!this.graph.searchAbort) {
    this.setSearchStreaming(false);
  }

  }

  async apiJson(url, options = {}) {
    return this.api.json(url, options);
  }

  parseGlobalId(value) {
    return GraphId.parse(value);
  }

  toGlobalIdQuery(value) {
    return GraphId.toQuery(value);
  }

  normalizeNodeResponse(node) {
    return GraphNode.fromApi(node);
  }

  normalizeEdgeResponse(edge) {
    return GraphEdge.fromApi(edge);
  }

  displayName(globalId) {
    return this.graph.displayName(globalId);
  }

  refreshEdgeAngles() {
  for (const node of this.graph.loaded.values()) {
    if (node.showed === false) {
      continue;
    }

    this.assignEdgeAngles(node);
  }

  }

  assignEdgeAngles(node) {
  const nodeName = node.name ?? node.globalId;
  const edges = (node.edges ?? []).map(edge => GraphEdge.from(edge));
  const ordered = [...edges].sort((left, right) => {
    const leftName = left.otherEndpoint(nodeName);
    const rightName = right.otherEndpoint(nodeName);
    return leftName.localeCompare(rightName, "ru") || left.key.localeCompare(right.key, "ru");
  });
  const step = ordered.length > 0 ? (Math.PI * 2) / ordered.length : 0;
  const start = -Math.PI / 2;

  ordered.forEach((edge, index) => {
    const otherName = edge.otherEndpoint(nodeName);
    const currentAngle = edge.controlAngleFor(nodeName);
    const loadedAngle = this.loadedEdgeAngle(nodeName, otherName);
    if (loadedAngle !== null) {
      edge.setControlAngle(nodeName, loadedAngle);
    } else if (currentAngle === null) {
      edge.setControlAngle(nodeName, start + step * index);
    }
  });
  node.edges = edges;

  }

  edgeAngleFromAnchor(anchorName, otherName) {
  if (!anchorName || !otherName) {
    return null;
  }

  const anchor = this.graph.loaded.get(anchorName);
  const edge = anchor?.edges
    ?.map(item => GraphEdge.from(item))
    .find(item => item.otherEndpoint(anchorName) === otherName);
  return edge?.controlAngleFor(anchorName) ?? this.loadedEdgeAngle(anchorName, otherName);

  }

  loadedEdgeAngle(anchorName, otherName) {
  if (!this.graph.loaded.has(anchorName) || !this.graph.loaded.has(otherName)) {
    return null;
  }

  const anchor = this.graph.positions.get(anchorName);
  const other = this.graph.positions.get(otherName);
  if (!anchor || !other) {
    return null;
  }

  const dx = other.x - anchor.x;
  const dy = other.y - anchor.y;
  if (Math.hypot(dx, dy) < 0.01) {
    return null;
  }

  return Math.atan2(dy, dx);

  }

  edgeEndpointDisplayName(edge, globalId) {
    return GraphEdge.from(edge).endpointDisplayName(globalId, id => this.displayName(id));
  }

  edgeNeighborLocalId(edge, anchorName) {
    return GraphEdge.from(edge).neighborLocalIdFor(anchorName);
  }

  ensureNodeLoaded(globalId) {
    if (this.graph.hasNode(globalId)) {
      return Promise.resolve();
    }

    return this.apiJson("/api/graph/nodes?" + this.toGlobalIdQuery(globalId))
      .then(node => this.storeNodeExpansion(this.normalizeNodeResponse(node), null, { select: false }));
  }

  async getLoadedNode(globalId) {
    await this.ensureNodeLoaded(globalId);
    return this.graph.node(globalId);
  }

  async updateGraphNodeAttributes(globalId, attributes) {
    await this.apiJson("/api/graph/nodes?" + this.toGlobalIdQuery(globalId), {
      method: "PUT",
      body: JSON.stringify({ attributes }),
      expectJson: false
    });
  }

}
