/*
  Vanilla GraphData UI organized as DDD-style layers without adding a build step.
  Domain constants/state stay framework-agnostic, infrastructure owns HTTP, and
  GraphDataApplication coordinates use cases plus SVG/DOM presentation.
*/

const svgNs = "http://www.w3.org/2000/svg";
const nodeRadius = 34;
const endpointOffset = nodeRadius + 9;
const defaultBasis = {
  nodeTypeRoot: "graphdata/types/nodes",
  edgeTypeRoot: "graphdata/types/edges",
  relationRoot: "graphdata/relations"
};
const graphKindAttribute = "graph.kind";
const graphElementAttribute = "graph.element";
const graphRoleAttribute = "graph.role";
const graphTypeNameAttribute = "graph.typeName";
const projectionVisibleAttribute = "projection.visible";
const projectionColorAttribute = "projection.color";
const projectionInfoAttribute = "projection.infoAttribute";
const projectionDirectedAttribute = "projection.directed";
const projectionLabelVisibleAttribute = "projection.labelVisible";
const projectionRankAttribute = "projection.rank";

class GraphWorkspaceState {
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

  resetGraph() {
    this.rootName = null;
    this.selectedName = null;
    this.loaded.clear();
    this.parentByNode.clear();
    this.positions.clear();
    this.velocities.clear();
  }
}

class GraphDataHttpClient {
  constructor(fetchApi) {
    this.fetchApi = fetchApi;
  }

  async json(url, options = {}) {
    const response = await this.fetchApi(url, {
      method: options.method ?? "GET",
      headers: { "Content-Type": "application/json" },
      body: options.body
    });

    if (!response.ok) {
      const text = await response.text();
      const error = new Error(text || `HTTP ${response.status}`);
      error.status = response.status;
      throw error;
    }

    if (options.expectJson === false || response.status === 204) {
      return null;
    }

    return response.json();
  }

  fetch(url, options = {}) {
    return this.fetchApi(url, options);
  }
}

class GraphDataApplication {
  constructor({ document, window, apiClient = new GraphDataHttpClient(window.fetch.bind(window)) } = {}) {
    this.document = document;
    this.window = window;
    this.apiClient = apiClient;
    this.state = new GraphWorkspaceState();

    this.svg = this.requireElement("#graph");
    this.viewport = this.requireElement("#viewport");
    this.rootForm = this.requireElement("#root-form");
    this.rootInput = this.requireElement("#root-input");
    this.fitButton = this.requireElement("#fit-button");
    this.resetButton = this.requireElement("#reset-button");
    this.statusOutput = this.requireElement("#status");
    this.emptyState = this.requireElement("#empty-state");
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
  }

  start() {
    this.bindTabs();
    this.bindToolbar();
    this.bindNodeForms();
    this.bindSearch();
    this.bindSubgraph();
    this.bindProjection();
    this.bindGraphSurface();

    this.setSearchQueryTemplate("all");
    this.syncBasisInputs();
    this.renderTypeControls();
    this.render();

    const params = new URLSearchParams(this.window.location.search);
    const initialGlobalId = params.get("globalId");
    if (initialGlobalId) {
      this.rootInput.value = initialGlobalId;
      void this.loadRoot(initialGlobalId);
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
    this.document.querySelectorAll(".tab-button").forEach(button => {
      button.addEventListener("click", () => this.setActiveTab(button.dataset.tab));
    });
  }

  bindToolbar() {
    this.rootForm.addEventListener("submit", event => {
      event.preventDefault();
      void this.loadRoot(this.rootInput.value.trim());
    });

    this.fitButton.addEventListener("click", () => this.fitView());
    this.resetButton.addEventListener("click", () => {
      this.state.resetGraph();
      this.render();
      this.setStatus("");
    });
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
      const sourceGlobalId = this.state.selectedName;
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
    this.document.querySelectorAll("[data-query-template]").forEach(button => {
      button.addEventListener("click", () => this.setSearchQueryTemplate(button.dataset.queryTemplate));
    });

    this.searchForm.addEventListener("submit", event => {
      event.preventDefault();
      void this.searchNodes();
    });

    this.searchStopButton.addEventListener("click", () => {
      this.state.searchAbort?.abort();
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
      this.state.schema.projectionBasis = this.projectionBasis.value;
      if (this.state.schema.projectionBasis === "typed") {
        await this.refreshTypes();
      }
      this.render();
      this.runSimulation(18);
    });

    this.loadBasisButton.addEventListener("click", () => void this.loadBasis());
    this.ensureBasisButton.addEventListener("click", () => void this.ensureDefaultBasis());
    this.refreshTypesButton.addEventListener("click", () => void this.refreshTypes());
    this.loadRelationsButton.addEventListener("click", () => void this.loadRelationInstances());
    this.assignNodeTypeButton.addEventListener("click", () => void this.assignSelectedNodeType());
  }

  bindGraphSurface() {
    this.svg.addEventListener("pointerdown", event => {
      if (event.button !== 0 || event.target.closest(".node") || event.target.closest(".edge-button")) {
        return;
      }

      this.svg.setPointerCapture(event.pointerId);
      this.svg.classList.add("dragging");
      this.state.pointer = { x: event.clientX, y: event.clientY };
    });

    this.svg.addEventListener("pointermove", event => {
      if (this.state.dragging) {
        const position = this.state.positions.get(this.state.dragging.name);
        if (!position) {
          return;
        }

        const dx = (event.clientX - this.state.dragging.x) / this.state.view.scale;
        const dy = (event.clientY - this.state.dragging.y) / this.state.view.scale;
        position.x += dx;
        position.y += dy;
        this.state.dragging.x = event.clientX;
        this.state.dragging.y = event.clientY;
        this.render();
        return;
      }

      if (!this.state.pointer) {
        return;
      }

      const dx = event.clientX - this.state.pointer.x;
      const dy = event.clientY - this.state.pointer.y;
      this.state.view.x += dx;
      this.state.view.y += dy;
      this.state.pointer = { x: event.clientX, y: event.clientY };
      this.applyView();
    });

    this.svg.addEventListener("pointerup", event => {
      if (this.state.dragging) {
        this.svg.releasePointerCapture(event.pointerId);
        this.state.dragging = null;
        return;
      }

      if (this.state.pointer) {
        this.svg.releasePointerCapture(event.pointerId);
      }

      this.state.pointer = null;
      this.svg.classList.remove("dragging");
    });

    this.svg.addEventListener("wheel", event => {
      event.preventDefault();
      const rect = this.svg.getBoundingClientRect();
      const mouseX = event.clientX - rect.left;
      const mouseY = event.clientY - rect.top;
      const before = this.screenToGraph(mouseX, mouseY);
      const scale = Math.min(2.8, Math.max(0.25, this.state.view.scale * Math.exp(-event.deltaY * 0.0012)));
      this.state.view.scale = scale;
      this.state.view.x = mouseX - before.x * scale;
      this.state.view.y = mouseY - before.y * scale;
      this.applyView();
    }, { passive: false });
  }

  async apiJson(url, options = {}) {
    return this.apiClient.json(url, options);
  }

  async loadRoot(name) {
  if (!name) {
    this.setStatus("Введите GlobalId узла");
    return;
  }

  this.state.rootName = name;
  this.state.selectedName = name;
  this.state.loaded.clear();
  this.state.parentByNode.clear();
  this.state.positions.clear();
  this.state.velocities.clear();
  this.seedPosition(name, null, 0);
  await this.loadNode(name, null);
  const url = new URL(this.window.location.href);
  url.searchParams.set("globalId", this.state.rootName ?? name);
  this.window.history.replaceState({}, "", url);
  this.fitView();

  }

  async loadNode(name, fromName, options = {}) {
  const select = options.select ?? true;
  this.setBusy(true);
  try {
    const expansion = this.normalizeNodeResponse(await this.apiJson(`/api/graph/nodes?${this.toGlobalIdQuery(name)}`));
    this.storeNodeExpansion(expansion, fromName, { select });

    this.render();
    this.renderTypeControls();
    this.runSimulation(34);
    this.setStatus(`Развернуто узлов: ${this.state.loaded.size}`);
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
    const alreadyLoaded = this.state.loaded.has(expansion.name);
    this.storeNodeExpansion(expansion, anchorName, { select: true });

    this.render();
    this.renderTypeControls();
    this.runSimulation(alreadyLoaded ? 18 : 34);
    this.setStatus(alreadyLoaded
      ? `Узел "${expansion.displayName}" уже был загружен, связь добавлена`
      : `Развернуто узлов: ${this.state.loaded.size}`);
  } catch (error) {
    this.setStatus(this.formatNeighborError(error, neighborLocalId));
  } finally {
    this.setBusy(false);
  }

  }

  storeNodeExpansion(expansion, fromName, options = {}) {
  const select = options.select ?? true;
  const existing = this.state.loaded.get(expansion.name);
  const stored = existing ? this.mergeNodeResponses(existing, expansion) : expansion;
  this.state.loaded.set(expansion.name, stored);

  if (!fromName && this.state.rootName && !this.state.loaded.has(this.state.rootName)) {
    this.state.rootName = expansion.name;
  }

  if (select) {
    this.state.selectedName = expansion.name;
  }

  this.seedPosition(expansion.name, fromName, 0);
  if (fromName && fromName !== expansion.name && !this.state.parentByNode.has(expansion.name)) {
    this.state.parentByNode.set(expansion.name, fromName);
  }

  stored.edges.forEach((edge, index) => {
    this.seedPosition(this.getOtherEndpoint(edge, expansion.name), expansion.name, index);
  });

  return stored;

  }

  mergeNodeResponses(existing, expansion) {
  return {
    ...existing,
    ...expansion,
    attributes: expansion.attributes ?? existing.attributes ?? {},
    edges: this.mergeEdges(existing.edges, expansion.edges)
  };

  }

  mergeEdges(left = [], right = []) {
  const edges = new Map();
  [...left, ...right].forEach(edge => {
    if (edge.sourceGlobalId && edge.targetGlobalId) {
      edges.set(this.edgeKey(edge.sourceGlobalId, edge.targetGlobalId), edge);
    }
  });
  return [...edges.values()];

  }

  async createNode(name, options = {}) {
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
    this.state.rootName = this.state.rootName ?? created.name;
    this.state.selectedName = created.name;
    this.seedPosition(created.name, this.state.rootName === created.name ? null : this.state.rootName, this.state.loaded.size);
    if (typeGlobalId) {
      const expanded = this.normalizeNodeResponse(await this.apiJson(`/api/graph/nodes?${this.toGlobalIdQuery(created.globalId)}`));
      this.storeNodeExpansion(expanded, null, { select: true });
    } else {
      this.state.loaded.set(created.name, created);
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
  const nodeName = this.state.selectedName;
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
  const nodeName = this.state.selectedName;
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

  async connectNodes(sourceGlobalId, targetGlobalId, options = {}) {
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
      this.state.selectedName = sourceGlobalId;
    } else {
      await this.connectGraphNodes(sourceGlobalId, targetGlobalId);
      await this.loadNode(sourceGlobalId, null, { select: true });
    }
    this.connectTargetName.value = "";
    this.connectEdgeName.value = "";
    this.render();
    this.renderTypeControls();
    this.runSimulation(32);
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
    this.state.schema.basis = {
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

  async refreshTypes(options = {}) {
  if (!options.preserveBusy) {
    this.setBusy(true);
  }

  try {
    this.readBasisInputs();
    const basis = this.getBasis();
    const response = await this.loadSubgraphForRoots([basis.nodeTypeRoot, basis.edgeTypeRoot], 4);
    this.mergeSubgraphIntoViewer(response, { select: false });
    const nodes = (response.nodes ?? []).map(node => this.normalizeNodeResponse(node));
    this.state.schema.nodeTypes = new Map();
    this.state.schema.edgeTypes = new Map();

    nodes
      .filter(node => node.globalId !== basis.nodeTypeRoot && node.globalId !== basis.edgeTypeRoot)
      .forEach(node => {
        if (this.isChildOf(node.globalId, basis.nodeTypeRoot)) {
          this.state.schema.nodeTypes.set(node.globalId, this.toGraphType(node, "node"));
        } else if (this.isChildOf(node.globalId, basis.edgeTypeRoot)) {
          this.state.schema.edgeTypes.set(node.globalId, this.toGraphType(node, "edge"));
        }
      });

    this.renderTypeControls();
    this.render();
    this.setStatus(`Типы: ${this.state.schema.nodeTypes.size} узлов, ${this.state.schema.edgeTypes.size} связей`);
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
      .filter(globalId => this.isChildOf(globalId, this.getBasis().relationRoot));

    for (const relationId of relationIds) {
      const subgraph = await this.loadSubgraphForRoots([relationId], 2);
      this.mergeSubgraphIntoViewer(subgraph, { select: false });
    }

    this.renderTypeControls();
    this.render();
    this.runSimulation(32);
    this.setStatus(`Инстансы связей загружены: ${relationIds.length}`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  }

  async assignSelectedNodeType() {
  const nodeName = this.state.selectedName;
  const typeGlobalId = this.assignNodeType.value;
  if (!nodeName || !this.state.loaded.has(nodeName) || !typeGlobalId) {
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
  const attrs = {
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
  const response = await this.apiClient.fetch("/api/graph/search/nodes", {
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
  const currentName = this.state.selectedName || this.rootInput.value.trim() || "node-name";
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

  parseGlobalId(value) {
  return value.split("/").filter(Boolean);

  }

  toGlobalIdQuery(value) {
  return this.parseGlobalId(value)
    .map(segment => `globalId=${encodeURIComponent(segment)}`)
    .join("&");

  }

  normalizeNodeResponse(node) {
  const globalId = node.globalId ?? node.name;
  const localId = node.localId ?? node.name;
  return {
    ...node,
    name: globalId,
    globalId,
    localId,
    displayName: localId,
    edges: (node.edges ?? []).map(edge => this.normalizeEdgeResponse(edge))
  };

  }

  normalizeEdgeResponse(edge) {
  const sourceGlobalId = edge.sourceGlobalId;
  const targetGlobalId = edge.targetGlobalId;
  const sourceLocalId = edge.sourceLocalId;
  const targetLocalId = edge.targetLocalId;
  const neighborLocalId = edge.neighborLocalId;
  return {
    ...edge,
    sourceGlobalId,
    targetGlobalId,
    sourceLocalId,
    targetLocalId,
    neighborLocalId
  };

  }

  displayName(globalId) {
  return this.state.loaded.get(globalId)?.displayName ?? globalId;

  }

  edgeEndpointDisplayName(edge, globalId) {
  if (edge.sourceGlobalId === globalId) {
    return edge.sourceLocalId ?? this.displayName(globalId);
  }
  if (edge.targetGlobalId === globalId) {
    return edge.targetLocalId ?? this.displayName(globalId);
  }
  return this.displayName(globalId);

  }

  edgeNeighborLocalId(edge, anchorName) {
  if (edge.neighborLocalId) {
    return edge.neighborLocalId;
  }

  return edge.sourceGlobalId === anchorName
    ? edge.targetLocalId
    : edge.sourceLocalId;

  }

  async searchNodes() {
  let query;
  try {
    query = JSON.parse(this.searchQueryJson.value.trim());
  } catch (error) {
    this.setStatus(`JSON: ${error.message}`);
    return;
  }

  this.state.searchAbort?.abort();
  const controller = new AbortController();
  this.state.searchAbort = controller;
  this.setSearchStreaming(true);
  this.renderSearchResults([]);

  let count = 0;
  try {
    const response = await this.apiClient.fetch("/api/graph/search/nodes", {
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
    if (this.state.searchAbort === controller) {
      this.state.searchAbort = null;
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
  const roots = this.parseCsv(this.document.querySelector("#subgraph-roots").value);
  if (roots.length === 0) {
    this.setStatus("Укажите корневые узлы");
    return;
  }

  this.setBusy(true);
  try {
    const response = await this.apiJson("/api/graph/subgraph", {
      method: "POST",
      body: JSON.stringify({
        globalIds: roots.map(root => this.parseGlobalId(root)),
        maxDepth: this.readNumber("#subgraph-depth", 1),
        includeDisconnectedRoots: this.document.querySelector("#subgraph-include-disconnected").checked
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

  loadSubgraphIntoViewer(response, roots) {
  this.state.loaded.clear();
  this.state.parentByNode.clear();
  this.state.positions.clear();
  this.state.velocities.clear();
  const nodes = (response.nodes ?? []).map(node => this.normalizeNodeResponse(node));
  const edges = (response.edges ?? []).map(edge => this.normalizeEdgeResponse(edge));
  this.state.rootName = roots[0] ?? nodes[0]?.name ?? null;
  this.state.selectedName = this.state.rootName;

  nodes.forEach((node, index) => {
    this.state.loaded.set(node.name, {
      ...node,
      edges: edges.filter(edge => edge.sourceGlobalId === node.name || edge.targetGlobalId === node.name)
    });
    this.seedSubgraphPosition(node.name, index, nodes.length);
  });

  this.render();
  this.renderTypeControls();
  this.runSimulation(40);
  this.fitView();

  }

  mergeSubgraphIntoViewer(response, options = {}) {
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
    const expansion = {
      ...node,
      edges: this.mergeEdges(node.edges, edgesByNode.get(node.name) ?? [])
    };
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
  if (!name || name === this.state.rootName || !this.state.loaded.has(name)) {
    return;
  }

  const fallbackSelection = this.state.parentByNode.get(name) ?? this.state.rootName;
  const removed = new Set();
  const visit = current => {
    removed.add(current);
    for (const [child, parent] of this.state.parentByNode.entries()) {
      if (parent === current) {
        visit(child);
      }
    }
  };

  visit(name);
  removed.forEach(nodeName => this.removeLocalNode(nodeName, false, false));
  this.state.selectedName = fallbackSelection;
  this.render();
  this.runSimulation(18);
  this.setStatus(`Развернуто узлов: ${this.state.loaded.size}`);

  }

  removeLocalNode(name, selectFallback = true, pruneEdges = true) {
  this.state.loaded.delete(name);
  if (pruneEdges) {
    this.state.positions.delete(name);
    this.state.velocities.delete(name);
  }
  this.state.parentByNode.delete(name);
  for (const [child, parent] of [...state.parentByNode.entries()]) {
    if (parent === name) {
      this.state.parentByNode.delete(child);
    }
  }

  if (pruneEdges) {
    for (const expansion of this.state.loaded.values()) {
      expansion.edges = (expansion.edges ?? [])
        .filter(edge => edge.sourceGlobalId !== name && edge.targetGlobalId !== name);
    }
  }

  if (selectFallback && this.state.selectedName === name) {
    this.state.selectedName = this.state.loaded.keys().next().value ?? null;
    this.state.rootName = this.state.selectedName;
  }

  }

  handleEndpointClick(edge, anchorName) {
  const otherName = edge.sourceGlobalId === anchorName ? edge.targetGlobalId : edge.sourceGlobalId;
  const anchorLoaded = this.state.loaded.has(anchorName);
  const otherLoaded = this.state.loaded.has(otherName);

  if (anchorLoaded && !otherLoaded) {
    this.loadNeighbor(anchorName, this.edgeNeighborLocalId(edge, anchorName));
    return;
  }

  if (!anchorLoaded && otherLoaded) {
    this.loadNeighbor(otherName, this.edgeNeighborLocalId(edge, otherName));
    return;
  }

  if (anchorLoaded && otherLoaded) {
    if (this.state.parentByNode.get(otherName) === anchorName) {
      this.collapseNode(otherName);
      return;
    }

    if (this.state.parentByNode.get(anchorName) === otherName && anchorName !== this.state.rootName) {
      this.collapseNode(anchorName);
      return;
    }

    this.state.selectedName = otherName;
    this.render();
  }

  }

  buildGraph() {
  const physical = this.buildPhysicalGraph();
  if (this.state.schema.projectionBasis === "empty") {
    return physical;
  }

  return this.buildProjectedGraph(physical);

  }

  buildPhysicalGraph() {
  const nodes = new Map();
  const edges = new Map();

  for (const expansion of this.state.loaded.values()) {
    nodes.set(expansion.name, {
      name: expansion.name,
      displayName: expansion.displayName,
      localId: expansion.localId,
      globalId: expansion.globalId,
      attributes: expansion.attributes ?? {}
    });

    (expansion.edges ?? []).forEach(edge => {
      const key = this.edgeKey(edge.sourceGlobalId, edge.targetGlobalId);
      if (!edges.has(key)) {
        edges.set(key, {
          key,
          sourceGlobalId: edge.sourceGlobalId,
          targetGlobalId: edge.targetGlobalId,
          sourceLocalId: edge.sourceLocalId,
          targetLocalId: edge.targetLocalId
        });
      }
    });
  }

  return { nodes: [...nodes.values()], edges: [...edges.values()] };

  }

  buildProjectedGraph(physical) {
  const relationInstances = this.discoverRelationInstances(physical);
  const hidden = new Set();

  for (const relation of relationInstances) {
    hidden.add(relation.relationGlobalId);
    relation.portGlobalIds.forEach(name => hidden.add(name));
  }

  for (const type of [...state.schema.nodeTypes.values(), ...state.schema.edgeTypes.values()]) {
    hidden.add(type.globalId);
  }

  const nodeTypeAssignments = this.getNodeTypeAssignments(physical);
  const visibleNodes = physical.nodes
    .filter(node => !hidden.has(node.name))
    .filter(node => !this.isSchemaRootNode(node.name))
    .filter(node => nodeTypeAssignments.get(node.name)?.visible !== false);
  const visibleNodeIds = new Set(visibleNodes.map(node => node.name));
  const typedNodes = visibleNodes.map(node => {
    const nodeType = nodeTypeAssignments.get(node.name);
    return {
      ...node,
      typeGlobalId: nodeType?.globalId,
      typeLabel: nodeType?.label,
      typeRank: nodeType?.rank,
      color: nodeType?.color,
      displayName: this.formatProjectedNodeName(node, nodeType)
    };
  });

  const hiddenPhysicalEdges = new Set();
  for (const relation of relationInstances) {
    relation.physicalEdgeKeys.forEach(key => hiddenPhysicalEdges.add(key));
  }
  for (const [nodeId, nodeType] of nodeTypeAssignments) {
    hiddenPhysicalEdges.add(this.edgeKey(nodeId, nodeType.globalId));
  }

  const physicalEdges = physical.edges.filter(edge => {
    return visibleNodeIds.has(edge.sourceGlobalId)
      && visibleNodeIds.has(edge.targetGlobalId)
      && !hiddenPhysicalEdges.has(edge.key ?? this.edgeKey(edge.sourceGlobalId, edge.targetGlobalId));
  });

  const projectedEdges = relationInstances
    .filter(relation => visibleNodeIds.has(relation.sourceGlobalId) && visibleNodeIds.has(relation.targetGlobalId))
    .filter(relation => relation.type?.visible !== false)
    .map(relation => ({
      key: `projected:${relation.relationGlobalId}`,
      sourceGlobalId: relation.sourceGlobalId,
      targetGlobalId: relation.targetGlobalId,
      sourceLocalId: this.getLocalId(relation.sourceGlobalId),
      targetLocalId: this.getLocalId(relation.targetGlobalId),
      relationGlobalId: relation.relationGlobalId,
      typeGlobalId: relation.type?.globalId,
      label: relation.type?.labelVisible === false ? "" : relation.type?.label ?? relation.displayName,
      color: relation.type?.color,
      directed: relation.type?.directed ?? false,
      typeRank: relation.type?.rank,
      projected: true
    }));

  return this.applyBasisRanks({
    nodes: typedNodes,
    edges: [...physicalEdges, ...projectedEdges]
  });

  }

  applyBasisRanks(graph) {
  const nodeStats = new Map(graph.nodes.map(node => [node.name, {
    weightedDegree: 0,
    focusBoost: 0,
    reasons: []
  }]));

  const rankedEdges = graph.edges.map(edge => {
    const edgeType = edge.typeGlobalId ? this.state.schema.edgeTypes.get(edge.typeGlobalId) : null;
    const basisWeight = this.readRank(edge.typeRank ?? edgeType?.rank, edge.projected ? 35 : 8);
    const rank = this.roundRank(basisWeight);
    const sourceStats = nodeStats.get(edge.sourceGlobalId);
    const targetStats = nodeStats.get(edge.targetGlobalId);

    if (sourceStats) {
      sourceStats.weightedDegree += basisWeight;
    }
    if (targetStats) {
      targetStats.weightedDegree += basisWeight;
    }
    if (this.state.selectedName === edge.sourceGlobalId && targetStats) {
      targetStats.focusBoost += Math.min(25, basisWeight * 0.35);
    }
    if (this.state.selectedName === edge.targetGlobalId && sourceStats) {
      sourceStats.focusBoost += Math.min(25, basisWeight * 0.35);
    }

    return {
      ...edge,
      viewRank: rank,
      viewRankReason: edgeType?.label
        ? `edge type ${edgeType.label}: ${this.formatRank(rank)}`
        : `physical edge: ${this.formatRank(rank)}`
    };
  });

  const rankedNodes = graph.nodes.map(node => {
    const stats = nodeStats.get(node.name);
    const typePriority = this.readRank(node.typeRank, node.typeGlobalId ? 50 : 20);
    const degreeScore = Math.log1p(stats?.weightedDegree ?? 0) * 8;
    const rootBoost = node.name === this.state.rootName ? 18 : 0;
    const selectedBoost = node.name === this.state.selectedName ? 30 : 0;
    const rank = this.roundRank(typePriority + degreeScore + rootBoost + selectedBoost + (stats?.focusBoost ?? 0));
    const reasons = [
      node.typeLabel ? `type ${node.typeLabel}: ${this.formatRank(typePriority)}` : `untyped: ${this.formatRank(typePriority)}`,
      `links: ${this.formatRank(degreeScore)}`
    ];
    if (rootBoost) {
      reasons.push(`root: ${this.formatRank(rootBoost)}`);
    }
    if (selectedBoost) {
      reasons.push(`selected: ${this.formatRank(selectedBoost)}`);
    }
    if (stats?.focusBoost) {
      reasons.push(`focus: ${this.formatRank(stats.focusBoost)}`);
    }

    return {
      ...node,
      viewRank: rank,
      viewRadius: this.rankToRadius(rank),
      viewRankReason: reasons.join("; ")
    };
  });

  rankedNodes.sort((left, right) => right.viewRank - left.viewRank || left.displayName.localeCompare(right.displayName, "ru"));
  rankedEdges.sort((left, right) => (right.viewRank ?? 0) - (left.viewRank ?? 0));

  return {
    nodes: rankedNodes,
    edges: rankedEdges
  };

  }

  formatProjectedNodeName(node, nodeType) {
  const parts = [node.displayName];
  if (nodeType?.label) {
    parts.push(nodeType.label);
  }
  if (nodeType?.infoAttribute && node.attributes?.[nodeType.infoAttribute]) {
    parts.push(node.attributes[nodeType.infoAttribute]);
  }
  return parts.join(" : ");

  }

  discoverRelationInstances(physical) {
  const nodesByName = new Map(physical.nodes.map(node => [node.name, node]));
  const edgesByNode = new Map();
  physical.edges.forEach(edge => {
    [edge.sourceGlobalId, edge.targetGlobalId].forEach(name => {
      if (!edgesByNode.has(name)) {
        edgesByNode.set(name, []);
      }
      edgesByNode.get(name).push(edge);
    });
  });

  return physical.nodes
    .filter(node => this.isRelationInstanceNode(node))
    .map(relation => {
      const relationEdges = edgesByNode.get(relation.name) ?? [];
      const type = relationEdges
        .map(edge => this.getOtherEndpoint(edge, relation.name))
        .map(name => this.state.schema.edgeTypes.get(name))
        .find(Boolean) ?? null;
      const ports = physical.nodes
        .filter(node => this.isChildOf(node.name, relation.name))
        .filter(node => this.getAttribute(node, graphKindAttribute) === "edge-port" || this.getAttribute(node, graphRoleAttribute));
      const sourcePort = ports.find(node => this.getAttribute(node, graphRoleAttribute) === "source" || node.localId === "source");
      const targetPort = ports.find(node => this.getAttribute(node, graphRoleAttribute) === "target" || node.localId === "target");
      const sourceGlobalId = sourcePort ? this.getPortEndpoint(sourcePort.name, edgesByNode, relation.name) : null;
      const targetGlobalId = targetPort ? this.getPortEndpoint(targetPort.name, edgesByNode, relation.name) : null;

      if (!sourceGlobalId || !targetGlobalId) {
        return null;
      }

      const physicalEdgeKeys = new Set(relationEdges.map(edge => edge.key ?? this.edgeKey(edge.sourceGlobalId, edge.targetGlobalId)));
      for (const port of ports) {
        physicalEdgeKeys.add(this.edgeKey(relation.name, port.name));
        for (const edge of edgesByNode.get(port.name) ?? []) {
          physicalEdgeKeys.add(edge.key ?? this.edgeKey(edge.sourceGlobalId, edge.targetGlobalId));
        }
      }

      return {
        relationGlobalId: relation.name,
        displayName: relation.displayName,
        sourceGlobalId,
        targetGlobalId,
        type,
        portGlobalIds: ports.map(port => port.name),
        physicalEdgeKeys
      };
    })
    .filter(Boolean);

  }

  getPortEndpoint(portGlobalId, edgesByNode, relationGlobalId) {
  for (const edge of edgesByNode.get(portGlobalId) ?? []) {
    const other = this.getOtherEndpoint(edge, portGlobalId);
    if (other !== relationGlobalId && !this.isChildOf(other, relationGlobalId)) {
      return other;
    }
  }

  return null;

  }

  getNodeTypeAssignments(physical) {
  const assignments = new Map();
  for (const edge of physical.edges) {
    const sourceType = this.state.schema.nodeTypes.get(edge.sourceGlobalId);
    const targetType = this.state.schema.nodeTypes.get(edge.targetGlobalId);
    if (sourceType && !targetType) {
      assignments.set(edge.targetGlobalId, sourceType);
    } else if (targetType && !sourceType) {
      assignments.set(edge.sourceGlobalId, targetType);
    }
  }
  return assignments;

  }

  isRelationInstanceNode(node) {
  if (this.getAttribute(node, graphKindAttribute) === "edge-instance") {
    return true;
  }

  return this.isChildOf(node.name, this.getBasis().relationRoot)
    && node.name !== this.getBasis().relationRoot
    && !node.name.slice(this.getBasis().relationRoot.length + 1).includes("/");

  }

  isSchemaRootNode(globalId) {
  const basis = this.getBasis();
  return globalId === "graphdata"
    || globalId === "graphdata/types"
    || globalId === basis.nodeTypeRoot
    || globalId === basis.edgeTypeRoot
    || globalId === basis.relationRoot;

  }

  isChildOf(globalId, parentGlobalId) {
  return Boolean(parentGlobalId)
    && globalId.length > parentGlobalId.length
    && globalId.startsWith(`${parentGlobalId}/`);

  }

  getAttribute(node, key) {
  return node.attributes?.[key] ?? node.attributes?.[key.toLowerCase()] ?? "";

  }

  getLocalId(globalId) {
  const segments = this.parseGlobalId(globalId);
  return segments.length > 0 ? segments[segments.length - 1] : globalId;

  }

  render() {
  const graph = this.buildGraph();
  this.viewport.replaceChildren();
  this.emptyState.classList.toggle("hidden", graph.nodes.length > 0);

  const edgeLayer = this.createSvg("g", { class: "edges" });
  const nodeLayer = this.createSvg("g", { class: "nodes" });
  const buttonLayer = this.createSvg("g", { class: "edge-buttons" });
  this.viewport.append(edgeLayer, nodeLayer, buttonLayer);

  const nodesByName = new Map(graph.nodes.map(node => [node.name, node]));
  graph.edges.forEach(edge => this.renderEdge(edgeLayer, buttonLayer, edge, nodesByName));
  graph.nodes.forEach(node => this.renderNode(nodeLayer, node));
  this.renderInspector(graph);
  this.applyView();

  }

  renderEdge(edgeLayer, buttonLayer, edge, nodesByName) {
  const sourceLoaded = this.state.loaded.has(edge.sourceGlobalId);
  const targetLoaded = this.state.loaded.has(edge.targetGlobalId);
  const source = this.state.positions.get(edge.sourceGlobalId);
  const target = this.state.positions.get(edge.targetGlobalId);

  if (!source || !target || (!sourceLoaded && !targetLoaded)) {
    return;
  }

  if (sourceLoaded && targetLoaded) {
    const sourceButton = this.pointOnCircle(source, target, this.getNodeEndpointOffset(nodesByName.get(edge.sourceGlobalId)));
    const targetButton = this.pointOnCircle(target, source, this.getNodeEndpointOffset(nodesByName.get(edge.targetGlobalId)));
    const line = this.createSvg("line", {
      class: "edge-line",
      x1: sourceButton.x,
      y1: sourceButton.y,
      x2: targetButton.x,
      y2: targetButton.y,
      style: edge.color ? `stroke:${edge.color}` : ""
    });

    edgeLayer.append(line);
    this.renderEdgeLabel(edgeLayer, source, target, edge);
    this.renderEndpointButton(buttonLayer, sourceButton, edge, edge.sourceGlobalId, false);
    this.renderEndpointButton(buttonLayer, targetButton, edge, edge.targetGlobalId, false);
    return;
  }

  const anchorName = sourceLoaded ? edge.sourceGlobalId : edge.targetGlobalId;
  const hiddenName = sourceLoaded ? edge.targetGlobalId : edge.sourceGlobalId;
  const anchor = sourceLoaded ? source : target;
  const hidden = sourceLoaded ? target : source;
  const buttonPoint = this.pointOnCircle(anchor, hidden, this.getNodeEndpointOffset(nodesByName.get(anchorName)));
  this.renderEndpointButton(buttonLayer, buttonPoint, edge, anchorName, true, hiddenName);

  }

  getNodeEndpointOffset(node) {
  return (node?.viewRadius ?? nodeRadius) + 9;

  }

  renderEdgeLabel(layer, source, target, edge) {
  if (!edge.label && !edge.directed) {
    return;
  }

  const text = this.createSvg("text", {
    class: "edge-label",
    x: (source.x + target.x) / 2,
    y: (source.y + target.y) / 2 - 7,
    style: edge.color ? `fill:${edge.color}` : ""
  });
  text.textContent = `${edge.label ?? ""}${edge.directed ? " ->" : ""}`.trim();
  layer.append(text);

  }

  renderEndpointButton(layer, point, edge, anchorName, collapsed, hiddenName = null) {
  const otherName = hiddenName ?? (edge.sourceGlobalId === anchorName ? edge.targetGlobalId : edge.sourceGlobalId);
  const otherDisplayName = this.edgeEndpointDisplayName(edge, otherName);
  const group = this.createSvg("g", {
    class: `edge-button ${collapsed ? "collapsed" : "expanded"}`,
    transform: `translate(${point.x} ${point.y})`,
    role: "button",
    tabindex: "0",
    "aria-label": collapsed ? `Развернуть ${otherDisplayName}` : `Выбрать ${otherDisplayName}`
  });
  const title = this.createSvg("title", {});
  const titleLines = [collapsed ? `Развернуть ${otherDisplayName}` : `Выбрать ${otherDisplayName}`];
  if (Number.isFinite(edge.viewRank)) {
    titleLines.push(`Edge rank: ${this.formatRank(edge.viewRank)}`);
  }
  if (edge.viewRankReason) {
    titleLines.push(edge.viewRankReason);
  }
  title.textContent = titleLines.join("\n");

  const hit = this.createSvg("circle", { class: "edge-button-hit", r: 17, cx: 0, cy: 0 });
  const core = this.createSvg("circle", {
    class: "edge-button-core",
    r: collapsed ? 8 : 6,
    cx: 0,
    cy: 0
  });

  const activate = event => {
    event.stopPropagation();
    this.handleEndpointClick(edge, anchorName);
  };

  group.addEventListener("click", activate);
  group.addEventListener("keydown", event => {
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      activate(event);
    }
  });

  group.append(title, hit, core);
  layer.append(group);

  }

  renderNode(layer, node) {
  const position = this.state.positions.get(node.name);
  if (!position) {
    return;
  }

  const group = this.createSvg("g", {
    class: `node${this.state.selectedName === node.name ? " selected" : ""}`,
    transform: `translate(${position.x} ${position.y})`,
    tabindex: "0",
    "aria-label": node.displayName ?? node.name
  });
  const title = this.createSvg("title", {});
  const titleLines = [node.globalId ?? node.name];
  if (node.typeLabel) {
    titleLines.push(`Type: ${node.typeLabel}`);
  }
  if (Number.isFinite(node.viewRank)) {
    titleLines.push(`Rank: ${this.formatRank(node.viewRank)}`);
  }
  if (node.viewRankReason) {
    titleLines.push(node.viewRankReason);
  }
  title.textContent = titleLines.join("\n");
  const circle = this.createSvg("circle", {
    class: "node-shell",
    r: node.viewRadius ?? nodeRadius,
    cx: 0,
    cy: 0,
    style: node.color ? `stroke:${node.color}` : ""
  });
  const label = this.createSvg("text", { class: "node-label", x: 0, y: 0 });
  label.textContent = this.trimName(node.displayName ?? node.name, 18);

  group.addEventListener("click", event => {
    event.stopPropagation();
    this.state.selectedName = node.name;
    this.render();
  });

  group.addEventListener("pointerdown", event => {
    event.stopPropagation();
    this.svg.setPointerCapture(event.pointerId);
    this.state.dragging = {
      name: node.name,
      x: event.clientX,
      y: event.clientY
    };
  });

  group.append(title, circle, label);
  layer.append(group);

  }

  renderInspector(graph) {
  const selected = graph.nodes.find(node => node.name === this.state.selectedName);
  this.selectedName.textContent = selected?.displayName ?? "-";
  this.selectedName.title = selected?.globalId ?? "";
  this.selectedRank.textContent = selected?.viewRank === undefined ? "rank: -" : `rank: ${this.formatRank(selected.viewRank)}`;
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
    row.className = `neighbor-row${this.state.loaded.has(name) ? " loaded" : ""}`;
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
    this.rootInput.value = node.globalId;
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
      this.state.selectedName = node.name;
      this.setActiveTab("node");
      this.render();
    });
    this.subgraphResults.append(button);
  });

  }

  renderTypeControls() {
  this.renderTypeSelect(this.createNodeType, this.state.schema.nodeTypes, "Без типа");
  this.renderTypeSelect(this.assignNodeType, this.state.schema.nodeTypes, "Не менять тип");
  this.renderTypeSelect(this.connectEdgeType, this.state.schema.edgeTypes, "Физическая связь");
  this.renderTypeList(this.nodeTypeList, "Типы узлов", this.state.schema.nodeTypes, "node");
  this.renderTypeList(this.edgeTypeList, "Типы связей", this.state.schema.edgeTypes, "edge");
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
        this.state.selectedName = type.globalId;
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
      const rank = this.createTextRule("Ранг", this.formatRankInput(type.rank), element === "node" ? "50" : "30");
      rules.append(visible.label, color.label, rank.label);

      const extraControls = {};
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
      attributes[projectionRankAttribute] = String(this.readRank(rules.rank, element === "edge" ? 30 : 50));
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
    this.state.loaded.set(globalId, loaded);
    if (element === "node") {
      this.state.schema.nodeTypes.set(globalId, this.toGraphType(loaded, "node"));
    } else {
      this.state.schema.edgeTypes.set(globalId, this.toGraphType(loaded, "edge"));
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
  const graph = this.buildPhysicalGraph();
  const relations = this.discoverRelationInstances(graph);
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
        this.state.selectedName = relation.relationGlobalId;
        this.render();
        this.setActiveTab("node");
      });
      row.append(open);
      this.typedEdgeList.append(row);
    });

  }

  renderProjectionSummary() {
  const physical = this.buildPhysicalGraph();
  const relations = this.discoverRelationInstances(physical);
  const graph = this.buildGraph();
  const rankedNodes = graph.nodes.filter(node => Number.isFinite(node.viewRank));
  const topRank = rankedNodes.length === 0
    ? ""
    : ` Топ rank: ${this.formatRank(Math.max(...rankedNodes.map(node => node.viewRank)))}.`;
  const basisLabel = this.state.schema.projectionBasis === "empty" ? "пустой базис" : "типовой базис";
  this.projectionSummary.textContent = `Проекция: ${basisLabel}. Загружено: ${physical.nodes.length} узлов, ${physical.edges.length} исходных связей, ${relations.length} типизированных связей.${topRank}`;

  }

  toGraphType(node, fallbackElement = "node") {
  const fallbackRank = fallbackElement === "edge" ? 30 : 50;
  return {
    globalId: node.globalId,
    localId: node.localId,
    label: node.attributes?.label || node.localId,
    color: this.normalizeColor(node.attributes?.[projectionColorAttribute] || node.attributes?.color),
    element: node.attributes?.[graphElementAttribute] || fallbackElement,
    visible: String(node.attributes?.[projectionVisibleAttribute] ?? "true").toLowerCase() !== "false",
    infoAttribute: node.attributes?.[projectionInfoAttribute] || "",
    labelVisible: String(node.attributes?.[projectionLabelVisibleAttribute] ?? "true").toLowerCase() !== "false",
    directed: String(node.attributes?.[projectionDirectedAttribute] ?? node.attributes?.directed ?? "").toLowerCase() === "true",
    rank: this.readRank(node.attributes?.[projectionRankAttribute] ?? node.attributes?.rank, fallbackRank),
    attributes: node.attributes ?? {}
  };

  }

  readRank(value, fallback = 0) {
  const parsed = Number.parseFloat(value);
  return Number.isFinite(parsed) ? Math.max(0, parsed) : fallback;

  }

  roundRank(value) {
  return Math.round(value * 10) / 10;

  }

  formatRank(value) {
  return this.roundRank(value).toFixed(1);

  }

  formatRankInput(value) {
  return Number.isFinite(value) ? String(this.roundRank(value)) : "";

  }

  rankToRadius(rank) {
  return Math.round(Math.max(28, Math.min(48, nodeRadius + (rank - 55) * 0.14)));

  }

  normalizeColor(value) {
  if (!value || !/^#[0-9a-f]{6}$/i.test(value.trim())) {
    return "";
  }

  return value.trim();

  }

  async ensureNodeLoaded(globalId) {
  if (this.state.loaded.has(globalId)) {
    return;
  }

  const node = this.normalizeNodeResponse(await this.apiJson(`/api/graph/nodes?${this.toGlobalIdQuery(globalId)}`));
  this.storeNodeExpansion(node, null, { select: false });

  }

  async getLoadedNode(globalId) {
  await this.ensureNodeLoaded(globalId);
  return this.state.loaded.get(globalId);

  }

  async updateGraphNodeAttributes(globalId, attributes) {
  await this.apiJson(`/api/graph/nodes?${this.toGlobalIdQuery(globalId)}`, {
    method: "PUT",
    body: JSON.stringify({ attributes }),
    expectJson: false
  });

  }

  getBasis() {
  return this.state.schema.basis;

  }

  readBasisInputs() {
  this.state.schema.basis = {
    nodeTypeRoot: this.nodeTypeRootInput.value.trim() || defaultBasis.nodeTypeRoot,
    edgeTypeRoot: this.edgeTypeRootInput.value.trim() || defaultBasis.edgeTypeRoot,
    relationRoot: this.relationRootInput.value.trim() || defaultBasis.relationRoot
  };

  }

  syncBasisInputs() {
  this.nodeTypeRootInput.value = this.state.schema.basis.nodeTypeRoot;
  this.edgeTypeRootInput.value = this.state.schema.basis.edgeTypeRoot;
  this.relationRootInput.value = this.state.schema.basis.relationRoot;
  this.projectionBasis.value = this.state.schema.projectionBasis;

  }

  createRelationLocalId(typeGlobalId) {
  const typeName = this.getLocalId(typeGlobalId).replace(/[^A-Za-z0-9._ -]/g, "-");
  return `${typeName}-${Date.now().toString(36)}`;

  }

  seedPosition(name, fromName, index) {
  if (this.state.positions.has(name)) {
    return;
  }

  if (!fromName || !this.state.positions.has(fromName)) {
    this.state.positions.set(name, { x: 0, y: 0 });
    this.state.velocities.set(name, { x: 0, y: 0 });
    return;
  }

  const source = this.state.positions.get(fromName);
  const angle = index * 2.399963 + [...name].reduce((sum, char) => sum + char.charCodeAt(0), 0) * 0.017;
  const distance = 92;
  this.state.positions.set(name, {
    x: source.x + Math.cos(angle) * distance,
    y: source.y + Math.sin(angle) * distance
  });
  this.state.velocities.set(name, { x: 0, y: 0 });

  }

  seedSubgraphPosition(name, index, count) {
  const radius = Math.max(120, Math.min(320, count * 32));
  const angle = count <= 1 ? 0 : (Math.PI * 2 * index) / count;
  this.state.positions.set(name, {
    x: Math.cos(angle) * radius,
    y: Math.sin(angle) * radius
  });
  this.state.velocities.set(name, { x: 0, y: 0 });

  }

  runSimulation(frames) {
  if (this.state.simulationHandle) {
    cancelAnimationFrame(this.state.simulationHandle);
  }

  let remaining = frames;
  const tick = () => {
    this.simulateStep();
    this.render();
    remaining -= 1;
    if (remaining > 0) {
      this.state.simulationHandle = requestAnimationFrame(tick);
    }
  };

  this.state.simulationHandle = requestAnimationFrame(tick);

  }

  simulateStep() {
  const graph = this.buildGraph();
  const nodes = graph.nodes;
  const forces = new Map(nodes.map(node => [node.name, { x: 0, y: 0 }]));

  for (let i = 0; i < nodes.length; i += 1) {
    for (let j = i + 1; j < nodes.length; j += 1) {
      const a = nodes[i];
      const b = nodes[j];
      const pa = this.state.positions.get(a.name);
      const pb = this.state.positions.get(b.name);
      if (!pa || !pb) {
        continue;
      }

      let dx = pb.x - pa.x;
      let dy = pb.y - pa.y;
      let distance = Math.hypot(dx, dy);
      if (distance < 0.01) {
        distance = 0.01;
        dx = 0.01;
        dy = 0;
      }

      const strength = 3000 / (distance * distance);
      const fx = (dx / distance) * strength;
      const fy = (dy / distance) * strength;
      forces.get(a.name).x -= fx;
      forces.get(a.name).y -= fy;
      forces.get(b.name).x += fx;
      forces.get(b.name).y += fy;
    }
  }

  graph.edges
    .filter(edge => this.state.loaded.has(edge.sourceGlobalId) && this.state.loaded.has(edge.targetGlobalId))
    .forEach(edge => {
      const source = this.state.positions.get(edge.sourceGlobalId);
      const target = this.state.positions.get(edge.targetGlobalId);
      if (!source || !target) {
        return;
      }

      let dx = target.x - source.x;
      let dy = target.y - source.y;
      let distance = Math.max(1, Math.hypot(dx, dy));
      const strength = (distance - 185) * 0.018;
      const fx = (dx / distance) * strength;
      const fy = (dy / distance) * strength;
      forces.get(edge.sourceGlobalId).x += fx;
      forces.get(edge.sourceGlobalId).y += fy;
      forces.get(edge.targetGlobalId).x -= fx;
      forces.get(edge.targetGlobalId).y -= fy;
    });

  nodes.forEach(node => {
    const position = this.state.positions.get(node.name);
    const velocity = this.state.velocities.get(node.name) ?? { x: 0, y: 0 };
    const force = forces.get(node.name);
    if (!position || !force) {
      return;
    }

    force.x += -position.x * 0.0025;
    force.y += -position.y * 0.0025;
    velocity.x = (velocity.x + force.x) * 0.78;
    velocity.y = (velocity.y + force.y) * 0.78;
    position.x += velocity.x;
    position.y += velocity.y;
    this.state.velocities.set(node.name, velocity);
  });

  }

  fitView() {
  const graph = this.buildGraph();
  if (graph.nodes.length === 0) {
    this.state.view = { x: 0, y: 0, scale: 1 };
    this.applyView();
    return;
  }

  const rect = this.svg.getBoundingClientRect();
  const points = graph.nodes
    .map(node => this.state.positions.get(node.name))
    .filter(Boolean);
  const minX = Math.min(...points.map(point => point.x)) - 120;
  const maxX = Math.max(...points.map(point => point.x)) + 120;
  const minY = Math.min(...points.map(point => point.y)) - 120;
  const maxY = Math.max(...points.map(point => point.y)) + 120;
  const width = Math.max(1, maxX - minX);
  const height = Math.max(1, maxY - minY);
  const scale = Math.min(1.8, Math.max(0.32, Math.min(rect.width / width, rect.height / height)));

  this.state.view.scale = scale;
  this.state.view.x = rect.width / 2 - ((minX + maxX) / 2) * scale;
  this.state.view.y = rect.height / 2 - ((minY + maxY) / 2) * scale;
  this.applyView();

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
  this.document.querySelectorAll(".tab-button").forEach(button => {
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

  pointOnCircle(anchor, target, radius) {
  let dx = target.x - anchor.x;
  let dy = target.y - anchor.y;
  let distance = Math.hypot(dx, dy);
  if (distance < 0.01) {
    dx = 1;
    dy = 0;
    distance = 1;
  }

  return {
    x: anchor.x + (dx / distance) * radius,
    y: anchor.y + (dy / distance) * radius
  };

  }

  applyView() {
  this.viewport.setAttribute("transform", `translate(${this.state.view.x} ${this.state.view.y}) scale(${this.state.view.scale})`);

  }

  screenToGraph(x, y) {
  return {
    x: (x - this.state.view.x) / this.state.view.scale,
    y: (y - this.state.view.y) / this.state.view.scale
  };

  }

  createSvg(name, attrs) {
  const element = this.document.createElementNS(svgNs, name);
  Object.entries(attrs).forEach(([key, value]) => element.setAttribute(key, value));
  return element;

  }

  edgeKey(a, b) {
  return a.localeCompare(b, "ru") < 0 ? `${a}\u0000${b}` : `${b}\u0000${a}`;

  }

  getOtherEndpoint(edge, nodeName) {
  return edge.sourceGlobalId === nodeName ? edge.targetGlobalId : edge.sourceGlobalId;

  }

  trimName(name, limit) {
  return name.length > limit ? `${name.slice(0, limit - 1)}…` : name;

  }

  setStatus(message) {
  this.statusOutput.value = message;
  this.statusOutput.textContent = message;

  }

  setBusy(value) {
  this.state.busy = value;
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
  const hasSelection = Boolean(this.state.selectedName && this.state.loaded.has(this.state.selectedName));
  this.saveNodeButton.disabled = this.state.busy || !hasSelection;
  this.deleteNodeButton.disabled = this.state.busy || !hasSelection;
  this.connectForm.querySelector("button").disabled = this.state.busy || !hasSelection;
  if (!this.state.searchAbort) {
    this.setSearchStreaming(false);
  }

  }

}

const graphDataApplication = new GraphDataApplication({ document, window });
window.graphDataApplication = graphDataApplication;
graphDataApplication.start();
