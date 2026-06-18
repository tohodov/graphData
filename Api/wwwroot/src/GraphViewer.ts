import {
  graphElementAttribute,
  graphKindAttribute,
  graphTypeNameAttribute,
  projectionColorAttribute,
  projectionDirectedAttribute,
  projectionInfoAttribute,
  projectionLabelVisibleAttribute,
  projectionRankAttribute,
  projectionVisibleAttribute,
  graphRoleAttribute
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
    this.gpuWarning = this.document.querySelector("#gpu-warning");
    this.fitButton = this.requireElement("#fit-button");
    this.resetButton = this.requireElement("#reset-button");
    this.rendererSelect = this.requireElement("#renderer-select");
    this.mobileMenuToggle = this.requireElement("#mobile-menu-toggle");
    this.mobileMenuClose = this.requireElement("#mobile-menu-close");
    this.statusOutput = this.requireElement("#status");
    this.emptyState = this.requireElement("#empty-state");
    this.emptyTitle = this.requireElement("#empty-state .empty-title");
    this.selectedName = this.requireElement("#selected-name");
    this.selectedRank = this.requireElement("#selected-rank");
    this.attributeEditor = this.requireElement("#attribute-editor");
    this.addAttributeButton = this.requireElement("#add-attribute-button");
    this.saveNodeButton = this.requireElement("#save-node-button");
    this.deleteNodeButton = this.requireElement("#delete-node-button");
    this.createNodeForm = this.requireElement("#create-node-form");
    this.createNodeName = this.requireElement("#create-node-name");
    this.createNodeParent = this.requireElement("#create-node-parent");
    this.createNodeType = this.requireElement("#create-node-type");
    this.connectForm = this.requireElement("#connect-form");
    this.connectTargetName = this.requireElement("#connect-target-name");
    this.connectEdgeType = this.requireElement("#connect-edge-type");
    this.connectEdgeName = this.requireElement("#connect-edge-name");
    this.neighborList = this.requireElement("#neighbor-list");
    this.searchForm = this.requireElement("#search-form");
    this.searchQueryJson = this.requireElement("#search-query-json");
    this.searchSubmitButton = this.requireElement("#search-submit-button");
    this.searchStopButton = this.requireElement("#search-stop-button");
    this.searchResults = this.requireElement("#search-results");
    this.subgraphForm = this.requireElement("#subgraph-form");
    this.subgraphResults = this.requireElement("#subgraph-results");
    this.projectionBasis = this.requireElement("#projection-basis");
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
      canCollapseNode: name => this.canCollapseNode(name),
      collapseNode: name => this.collapseNode(name),
      edgeEndpointControl: (edge, anchorName) => this.edgeEndpointControl(edge, anchorName),
      activateEdgeEndpoint: (edge, anchorName) => this.handleEndpointClick(edge, anchorName),
      syncEdgeAngles: () => this.refreshEdgeAngles(),
      renderInspector: graph => this.renderInspector(graph),
      formatRank: value => GraphType.formatRank(value)
    });
  }

  async start() {
    this.bindTabs();
    this.bindToolbar();
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
      this.render();
      this.setStatus("");
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
    this.addAttributeButton.addEventListener("click", () => this.addAttributeRow("", ""));
    this.saveNodeButton.addEventListener("click", () => void this.saveSelectedNode());
    this.deleteNodeButton.addEventListener("click", () => void this.deleteSelectedNode());

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
      const sourceGlobalId = this.graph.selectedName;
      const targetGlobalId = this.connectTargetName.value.trim();
      if (!sourceGlobalId || !targetGlobalId) {
        this.setStatus("Выберите узел и укажите цель связи");
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
    this.projectionBasis.addEventListener("change", async () => {
      this.graph.schema.projectionBasis = this.projectionBasis.value;
      if (this.graph.schema.projectionBasis === "typed") {
        await this.refreshTypes();
      }
      this.render();
    });

    this.loadBasisButton.addEventListener("click", () => void this.loadBasis());
    this.ensureBasisButton.addEventListener("click", () => void this.ensureDefaultBasis());
    this.refreshTypesButton.addEventListener("click", () => void this.refreshTypes());
    this.loadRelationsButton.addEventListener("click", () => void this.loadRelationInstances());
    this.assignNodeTypeButton.addEventListener("click", () => void this.assignSelectedNodeType());
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
  this.seedPosition(name, null, 0);
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
    this.setStatus(`Развернуто узлов: ${this.graph.loaded.size}`);
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
    this.storeNodeExpansion(expansion, anchorName, { select: true });

    this.render();
    this.renderTypeControls();
    this.setStatus(alreadyLoaded
      ? `Узел "${expansion.displayName}" уже был загружен, связь добавлена`
      : `Развернуто узлов: ${this.graph.loaded.size}`);
  } catch (error) {
    this.setStatus(this.formatNeighborError(error, neighborLocalId));
  } finally {
    this.setBusy(false);
  }

  }

  storeNodeExpansion(expansion, fromName, options: any = {}) {
  const select = options.select ?? true;
  const existing = this.graph.loaded.get(expansion.name);
  const stored = existing ? this.mergeNodeResponses(existing, expansion) : expansion;
  this.graph.loaded.set(expansion.name, stored);

  if (!fromName && this.graph.rootName && !this.graph.loaded.has(this.graph.rootName)) {
    this.graph.rootName = expansion.name;
  }

  if (select) {
    this.graph.selectedName = expansion.name;
  }

  this.seedPosition(expansion.name, fromName, 0, this.edgeAngleFromAnchor(fromName, expansion.name));
  if (fromName && fromName !== expansion.name && !this.graph.parentByNode.has(expansion.name)) {
    this.graph.parentByNode.set(expansion.name, fromName);
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
    const attributes = typeGlobalId
      ? {
          [graphKindAttribute]: "instance",
          [graphElementAttribute]: "node",
          [graphTypeNameAttribute]: typeGlobalId
        }
      : null;
    const created = await this.createGraphNode(name, parentGlobalId, attributes);
    if (typeGlobalId) {
      await this.connectGraphNodes(created.globalId, typeGlobalId);
    }
    this.createNodeName.value = "";
    this.createNodeParent.value = "";
    this.graph.rootName = this.graph.rootName ?? created.name;
    this.graph.selectedName = created.name;
    this.seedPosition(created.name, this.graph.rootName === created.name ? null : this.graph.rootName, this.graph.loaded.size);
    if (typeGlobalId) {
      const expanded = this.normalizeNodeResponse(await this.apiJson(`/api/graph/nodes?${this.toGlobalIdQuery(created.globalId)}`));
      this.storeNodeExpansion(expanded, null, { select: true });
    } else {
      this.graph.loaded.set(created.name, created);
    }
    this.render();
    this.renderTypeControls();
    this.setActiveTab("node");
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

  async saveSelectedNode() {
  const nodeName = this.graph.selectedName;
  if (!nodeName) {
    this.setStatus("Узел не выбран");
    return;
  }

  this.setBusy(true);
  try {
    await this.apiJson(`/api/graph/nodes?${this.toGlobalIdQuery(nodeName)}`, {
      method: "PUT",
      body: JSON.stringify({ attributes: this.readAttributeEditor() }),
      expectJson: false
    });
    await this.loadNode(nodeName, null, { select: true });
    this.setStatus(`Сохранен узел "${this.displayName(nodeName)}"`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  }

  async deleteSelectedNode() {
  const nodeName = this.graph.selectedName;
  if (!nodeName) {
    this.setStatus("Узел не выбран");
    return;
  }

  this.setBusy(true);
  try {
    await this.apiJson(`/api/graph/nodes?${this.toGlobalIdQuery(nodeName)}`, {
      method: "DELETE",
      expectJson: false
    });
    this.removeLocalNode(nodeName);
    this.render();
    this.renderTypeControls();
    this.setStatus(`Удален узел "${this.displayName(nodeName)}"`);
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
      const relation = await this.createTypedEdgeRelation(
        sourceGlobalId,
        targetGlobalId,
        typeGlobalId,
        options.relationLocalId || "");
      const subgraph = await this.loadSubgraphForRoots([relation.globalId, sourceGlobalId, targetGlobalId, typeGlobalId], 2);
      this.mergeSubgraphIntoViewer(subgraph, { select: false });
      this.graph.selectedName = sourceGlobalId;
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
    await this.ensurePath(basis.edgeTypeRoot, { [graphKindAttribute]: "type-root", [graphElementAttribute]: "edge" });
    await this.ensurePath(basis.relationRoot, { [graphKindAttribute]: "relation-root", [graphElementAttribute]: "edge" });

    await this.upsertGraphType(basis.nodeTypeRoot, "Type", "Type", "#334155", "node", false, 90);
    await this.upsertGraphType(basis.nodeTypeRoot, "Instance", "Instance", "#0f766e", "node", false, 70);
    await this.upsertGraphType(basis.edgeTypeRoot, "Type", "Type", "#7c2d12", "edge", true, 60);
    await this.upsertGraphType(basis.edgeTypeRoot, "Instance", "Instance", "#b45309", "edge", true, 50);

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
    const basis = this.getBasis();
    const response = await this.loadSubgraphForRoots([basis.nodeTypeRoot, basis.edgeTypeRoot], 4);
    this.mergeSubgraphIntoViewer(response, { select: false });
    const nodes = (response.nodes ?? []).map(node => this.normalizeNodeResponse(node));
    this.graph.schema.nodeTypes = new Map();
    this.graph.schema.edgeTypes = new Map();

    nodes
      .filter(node => node.globalId !== basis.nodeTypeRoot && node.globalId !== basis.edgeTypeRoot)
      .forEach(node => {
        if (GraphId.isChildOf(node.globalId, basis.nodeTypeRoot)) {
          this.graph.schema.nodeTypes.set(node.globalId, GraphType.fromNode(node, "node"));
        } else if (GraphId.isChildOf(node.globalId, basis.edgeTypeRoot)) {
          this.graph.schema.edgeTypes.set(node.globalId, GraphType.fromNode(node, "edge"));
        }
      });

    this.renderTypeControls();
    this.render();
    this.setStatus(`Типы: ${this.graph.schema.nodeTypes.size} узлов, ${this.graph.schema.edgeTypes.size} связей`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    if (!options.preserveBusy) {
      this.setBusy(false);
    }
  }

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
      .map(match => this.normalizeNodeResponse(match.node).globalId)
      .filter(globalId => GraphId.isChildOf(globalId, this.getBasis().relationRoot));

    for (const relationId of relationIds) {
      const subgraph = await this.loadSubgraphForRoots([relationId], 2);
      this.mergeSubgraphIntoViewer(subgraph, { select: false });
    }

    this.renderTypeControls();
    this.render();
    this.setStatus(`Инстансы связей загружены: ${relationIds.length}`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  }

  async assignSelectedNodeType() {
  const nodeName = this.graph.selectedName;
  const typeGlobalId = this.assignNodeType.value;
  if (!nodeName || !this.graph.loaded.has(nodeName) || !typeGlobalId) {
    this.setStatus("Выберите загруженный узел и тип узла");
    return;
  }

  this.setBusy(true);
  try {
    await this.connectGraphNodes(nodeName, typeGlobalId);
    await this.loadNode(nodeName, null, { select: true });
    this.render();
    this.setStatus(`Тип ${this.displayName(typeGlobalId)} назначен узлу ${this.displayName(nodeName)}`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  }

  async createTypedEdgeRelation(source, target, typeGlobalId, relationLocalId = "") {
  this.readBasisInputs();
  await this.ensurePath(this.getBasis().relationRoot);
  const relation = await this.createGraphNode(relationLocalId || this.createRelationLocalId(typeGlobalId), this.getBasis().relationRoot, {
    [graphKindAttribute]: "edge-instance",
    [graphElementAttribute]: "edge",
    [graphTypeNameAttribute]: typeGlobalId
  });
  const sourcePort = await this.createGraphNode("source", relation.globalId, {
    [graphKindAttribute]: "edge-port",
    [graphRoleAttribute]: "source"
  });
  const targetPort = await this.createGraphNode("target", relation.globalId, {
    [graphKindAttribute]: "edge-port",
    [graphRoleAttribute]: "target"
  });

  await this.connectGraphNodes(relation.globalId, typeGlobalId);
  await this.connectGraphNodes(sourcePort.globalId, source);
  await this.connectGraphNodes(targetPort.globalId, target);
  return relation;

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
      select: select && index === 0
    });
  });
  this.renderTypeControls();

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

  collapseNode(name) {
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
  this.setStatus(`Развернуто узлов: ${this.graph.loaded.size}`);

  }

  canCollapseNode(name) {
  if (!name || name === this.graph.rootName || !this.graph.loaded.has(name)) {
    return false;
  }

  if (this.isEdgeElementNode(name)) {
    return false;
  }

  for (const parent of this.graph.parentByNode.values()) {
    if (parent === name) {
      return true;
    }
  }

  return false;

  }

  edgeEndpointControl(edge, anchorName) {
  const normalized = GraphEdge.from(edge);
  const otherName = normalized.otherEndpoint(anchorName);
  const anchorLoaded = this.graph.loaded.has(anchorName);
  const otherLoaded = this.graph.loaded.has(otherName);
  const otherLabel = this.edgeEndpointDisplayName(normalized, otherName);

  if (anchorLoaded && !otherLoaded) {
    return {
      kind: "expand",
      text: "+",
      title: `Развернуть ${otherLabel}`,
      otherName,
      angle: normalized.frontierAngleFor(anchorName)
    };
  }

  if (anchorLoaded && this.graph.isEdgeCollapsed(normalized)) {
    return {
      kind: "expand",
      text: "+",
      title: `Развернуть связь с ${otherLabel}`,
      otherName
    };
  }

  if (anchorLoaded && otherLoaded) {
    return {
      kind: "collapse",
      text: "-",
      title: `Свернуть связь с ${otherLabel}`,
      otherName
    };
  }

  return null;

  }

  isEdgeElementNode(name) {
  const node = this.graph.loaded.get(name);
  return GraphNode.from(node).graphElement("node") === "edge";

  }

  edgeTreeChildName(edge, anchorName) {
  const normalized = GraphEdge.from(edge);
  const otherName = normalized.sourceGlobalId === anchorName ? normalized.targetGlobalId : normalized.sourceGlobalId;
  return this.graph.parentByNode.get(otherName) === anchorName
    ? otherName
    : this.graph.parentByNode.get(anchorName) === otherName && anchorName !== this.graph.rootName
      ? anchorName
      : null;

  }

  removeLocalNode(name, selectFallback = true, pruneEdges = true) {
  const wasSelected = this.graph.selectedName === name;
  this.graph.loaded.delete(name);
  this.graph.removeSelectedName?.(name);
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
  const otherName = edge.sourceGlobalId === anchorName ? edge.targetGlobalId : edge.sourceGlobalId;
  const anchorLoaded = this.graph.loaded.has(anchorName);
  const otherLoaded = this.graph.loaded.has(otherName);

  if (anchorLoaded && !otherLoaded) {
    this.loadNeighbor(anchorName, this.edgeNeighborLocalId(edge, anchorName));
    return;
  }

  if (!anchorLoaded && otherLoaded) {
    this.loadNeighbor(otherName, this.edgeNeighborLocalId(edge, otherName));
    return;
  }

  if (anchorLoaded && this.graph.isEdgeCollapsed(edge)) {
    this.stopSimulation();
    this.graph.expandEdge(edge);
    this.render();
    this.setStatus(`Развернута связь "${this.displayName(anchorName)}" - "${this.displayName(otherName)}"`);
    return;
  }

  if (anchorLoaded && otherLoaded) {
    this.collapseEdge(edge, anchorName);
  }

  }

  collapseEdge(edge, anchorName) {
  const otherName = edge.sourceGlobalId === anchorName ? edge.targetGlobalId : edge.sourceGlobalId;
  const childName = this.edgeTreeChildName(edge, anchorName);

  if (childName) {
    this.collapseNode(childName);
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
  const selected = graph.nodes.find(node => node.name === this.graph.selectedName);
  this.selectedName.textContent = selected?.displayName ?? "-";
  this.selectedName.title = selected?.globalId ?? "";
  this.selectedRank.textContent = selected?.viewRank === undefined ? "rank: -" : `rank: ${GraphType.formatRank(selected.viewRank)}`;
  this.selectedRank.title = selected?.viewRankReason ?? "";
  this.updateEditorState();
  this.renderAttributeEditor(selected?.attributes ?? {});
  this.neighborList.replaceChildren();

  if (!selected) {
    return;
  }

  const neighbors = graph.edges
    .filter(edge => edge.sourceGlobalId === selected.name || edge.targetGlobalId === selected.name)
    .map(edge => edge.sourceGlobalId === selected.name ? edge.targetGlobalId : edge.sourceGlobalId)
    .sort((a, b) => this.displayName(a).localeCompare(this.displayName(b), "ru"));

  neighbors.forEach(name => {
    const edge = graph.edges.find(candidate =>
      (candidate.sourceGlobalId === selected.name && candidate.targetGlobalId === name) ||
      (candidate.sourceGlobalId === name && candidate.targetGlobalId === selected.name));
    const row = this.document.createElement("button");
    row.type = "button";
    row.className = `neighbor-row${this.graph.loaded.has(name) ? " loaded" : ""}`;
    const dot = this.document.createElement("span");
    dot.className = "neighbor-dot";
    const text = this.document.createElement("span");
    text.textContent = edge ? this.edgeEndpointDisplayName(edge, name) : this.displayName(name);
    row.title = name;
    row.append(dot, text);
    row.addEventListener("click", () => {
      if (edge) {
        this.handleEndpointClick(edge, selected.name);
      }
    });
    this.neighborList.append(row);
  });

  }

  renderAttributeEditor(attributes) {
  const focused = this.document.activeElement;
  if (focused?.closest("#attribute-editor")) {
    return;
  }

  this.attributeEditor.replaceChildren();
  const entries = Object.entries(attributes);
  if (entries.length === 0) {
    this.addAttributeRow("", "");
    return;
  }

  entries.forEach(([key, value]) => this.addAttributeRow(key, value));

  }

  addAttributeRow(key, value) {
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
  this.attributeEditor.append(row);

  }

  readAttributeEditor() {
  const attributes = {};
  this.attributeEditor.querySelectorAll(".attribute-row").forEach(row => {
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
    this.setActiveTab("node");
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
      this.setActiveTab("node");
      this.render();
    });
    this.subgraphResults.append(button);
  });

  }

  renderTypeControls() {
  this.renderTypeSelect(this.createNodeType, this.graph.schema.nodeTypes, "Без типа");
  this.renderTypeSelect(this.assignNodeType, this.graph.schema.nodeTypes, "Не менять тип");
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
  [...types.values()]
    .sort((a, b) => a.label.localeCompare(b.label, "ru"))
    .forEach(type => {
      const row = this.document.createElement("div");
      row.className = "basis-rule";
      row.title = type.globalId;

      const header = this.document.createElement("div");
      header.className = "type-row";
      const swatch = this.document.createElement("span");
      swatch.className = "type-swatch";
      swatch.style.background = type.color || "#9daab2";
      const open = this.document.createElement("button");
      open.type = "button";
      open.className = "result-row type-open-button";
      open.textContent = type.directed ? `${type.label} ->` : type.label;
      open.title = type.globalId;
      open.addEventListener("click", async () => {
        await this.ensureNodeLoaded(type.globalId);
        this.graph.selectedName = type.globalId;
        this.render();
        this.setActiveTab("node");
      });
      const save = this.document.createElement("button");
      save.type = "button";
      save.className = "compact-button";
      save.textContent = "Сохранить";
      header.append(swatch, open, save);

      const rules = this.document.createElement("div");
      rules.className = "basis-rule-grid";
      const visible = this.createCheckboxRule("Показывать", type.visible);
      const color = this.createTextRule("Цвет", type.color || "", "#0f766e");
      const rank = this.createTextRule("Ранг", GraphType.formatRankInput(type.rank), element === "node" ? "50" : "30");
      rules.append(visible.label, color.label, rank.label);

      const extraControls: any = {};
      if (element === "node") {
        extraControls.info = this.createTextRule("Инфо атрибут", type.infoAttribute || "", "например: status");
        rules.append(extraControls.info.label);
      } else {
        extraControls.directed = this.createCheckboxRule("Стрелка", type.directed);
        extraControls.labelVisible = this.createCheckboxRule("Подпись", type.labelVisible);
        rules.append(extraControls.directed.label, extraControls.labelVisible.label);
      }

      save.addEventListener("click", () => this.saveTypeProjectionRules(type.globalId, element, {
        visible: visible.input.checked,
        color: color.input.value.trim(),
        rank: rank.input.value.trim(),
        infoAttribute: extraControls.info?.input.value.trim() ?? "",
        directed: extraControls.directed?.input.checked ?? false,
        labelVisible: extraControls.labelVisible?.input.checked ?? true
      }));

      row.append(header, rules);
      container.append(row);
    });

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
    this.render();
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
        this.setActiveTab("node");
      });
      row.append(open);
      this.typedEdgeList.append(row);
    });

  }

  renderProjectionSummary() {
  const physical = this.graph.physicalGraph();
  const relations = this.graph.discoverRelationInstances(physical);
  const graph = this.graph.visibleGraph();
  const rankedNodes = graph.nodes.filter(node => Number.isFinite(node.viewRank));
  const topRank = rankedNodes.length === 0
    ? ""
    : ` Топ rank: ${GraphType.formatRank(Math.max(...rankedNodes.map(node => node.viewRank)))}.`;
  const basisLabel = this.graph.schema.projectionBasis === "empty" ? "пустой базис" : "типовой базис";
  this.projectionSummary.textContent = `Проекция: ${basisLabel}. Загружено: ${physical.nodes.length} узлов, ${physical.edges.length} исходных связей, ${relations.length} типизированных связей.${topRank}`;

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
  this.projectionBasis.value = this.graph.schema.projectionBasis;

  }

  createRelationLocalId(typeGlobalId) {
  const typeName = GraphId.localId(typeGlobalId).replace(/[^A-Za-z0-9._ -]/g, "-");
  return `${typeName}-${Date.now().toString(36)}`;

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
  const distance = 92;
  this.graph.positions.set(name, {
    x: source.x + Math.cos(angle) * distance,
    y: source.y + Math.sin(angle) * distance
  });
  this.graph.velocities.set(name, { x: 0, y: 0 });

  }

  seedSubgraphPosition(name, index, count) {
  const radius = Math.max(120, Math.min(320, count * 32));
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

  updateEditorState() {
  const hasSelection = Boolean(this.graph.selectedName && this.graph.loaded.has(this.graph.selectedName));
  this.saveNodeButton.disabled = this.graph.busy || !hasSelection;
  this.deleteNodeButton.disabled = this.graph.busy || !hasSelection;
  this.connectForm.querySelector("button").disabled = this.graph.busy || !hasSelection;
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
    const currentAngle = edge.frontierAngleFor(nodeName);
    const loadedAngle = this.loadedEdgeAngle(nodeName, otherName);
    if (loadedAngle !== null) {
      edge.setFrontierAngle(nodeName, loadedAngle);
    } else if (currentAngle === null) {
      edge.setFrontierAngle(nodeName, start + step * index);
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
  return edge?.frontierAngleFor(anchorName) ?? this.loadedEdgeAngle(anchorName, otherName);

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
