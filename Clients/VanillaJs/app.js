const svg = document.querySelector("#graph");
const viewport = document.querySelector("#viewport");
const rootForm = document.querySelector("#root-form");
const rootInput = document.querySelector("#root-input");
const fitButton = document.querySelector("#fit-button");
const resetButton = document.querySelector("#reset-button");
const statusOutput = document.querySelector("#status");
const emptyState = document.querySelector("#empty-state");
const selectedName = document.querySelector("#selected-name");
const selectedRank = document.querySelector("#selected-rank");
const attributeEditor = document.querySelector("#attribute-editor");
const addAttributeButton = document.querySelector("#add-attribute-button");
const saveNodeButton = document.querySelector("#save-node-button");
const deleteNodeButton = document.querySelector("#delete-node-button");
const createNodeForm = document.querySelector("#create-node-form");
const createNodeName = document.querySelector("#create-node-name");
const createNodeParent = document.querySelector("#create-node-parent");
const createNodeType = document.querySelector("#create-node-type");
const connectForm = document.querySelector("#connect-form");
const connectTargetName = document.querySelector("#connect-target-name");
const connectEdgeType = document.querySelector("#connect-edge-type");
const connectEdgeName = document.querySelector("#connect-edge-name");
const neighborList = document.querySelector("#neighbor-list");
const searchForm = document.querySelector("#search-form");
const searchQueryJson = document.querySelector("#search-query-json");
const searchSubmitButton = document.querySelector("#search-submit-button");
const searchStopButton = document.querySelector("#search-stop-button");
const searchResults = document.querySelector("#search-results");
const subgraphForm = document.querySelector("#subgraph-form");
const subgraphResults = document.querySelector("#subgraph-results");
const projectionBasis = document.querySelector("#projection-basis");
const basisNodeInput = document.querySelector("#basis-node");
const nodeTypeRootInput = document.querySelector("#node-type-root");
const edgeTypeRootInput = document.querySelector("#edge-type-root");
const relationRootInput = document.querySelector("#relation-root");
const loadBasisButton = document.querySelector("#load-basis-button");
const ensureBasisButton = document.querySelector("#ensure-basis-button");
const refreshTypesButton = document.querySelector("#refresh-types-button");
const loadRelationsButton = document.querySelector("#load-relations-button");
const projectionSummary = document.querySelector("#projection-summary");
const nodeTypeList = document.querySelector("#node-type-list");
const assignNodeType = document.querySelector("#assign-node-type");
const assignNodeTypeButton = document.querySelector("#assign-node-type-button");
const edgeTypeList = document.querySelector("#edge-type-list");
const typedEdgeList = document.querySelector("#typed-edge-list");

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

const state = {
  rootName: null,
  selectedName: null,
  loaded: new Map(),
  parentByNode: new Map(),
  positions: new Map(),
  velocities: new Map(),
  view: { x: 0, y: 0, scale: 1 },
  dragging: null,
  pointer: null,
  simulationHandle: null,
  searchAbort: null,
  busy: false,
  schema: {
    projectionBasis: "empty",
    basis: { ...defaultBasis },
    nodeTypes: new Map(),
    edgeTypes: new Map()
  }
};

document.querySelectorAll(".tab-button").forEach(button => {
  button.addEventListener("click", () => setActiveTab(button.dataset.tab));
});

document.querySelectorAll("[data-query-template]").forEach(button => {
  button.addEventListener("click", () => setSearchQueryTemplate(button.dataset.queryTemplate));
});

const params = new URLSearchParams(window.location.search);
const initialGlobalId = params.get("globalId");
if (initialGlobalId) {
  rootInput.value = initialGlobalId;
  loadRoot(initialGlobalId);
}

setSearchQueryTemplate("all");
syncBasisInputs();
renderTypeControls();

rootForm.addEventListener("submit", event => {
  event.preventDefault();
  loadRoot(rootInput.value.trim());
});

fitButton.addEventListener("click", fitView);
resetButton.addEventListener("click", () => {
  state.rootName = null;
  state.selectedName = null;
  state.loaded.clear();
  state.parentByNode.clear();
  state.positions.clear();
  state.velocities.clear();
  render();
  setStatus("");
});

addAttributeButton.addEventListener("click", () => addAttributeRow("", ""));
saveNodeButton.addEventListener("click", saveSelectedNode);
deleteNodeButton.addEventListener("click", deleteSelectedNode);

createNodeForm.addEventListener("submit", async event => {
  event.preventDefault();
  const name = createNodeName.value.trim();
  if (!name) {
    setStatus("Введите LocalId нового узла");
    return;
  }

  await createNode(name, {
    parentGlobalId: createNodeParent.value.trim(),
    typeGlobalId: createNodeType.value
  });
});

connectForm.addEventListener("submit", async event => {
  event.preventDefault();
  const sourceGlobalId = state.selectedName;
  const targetGlobalId = connectTargetName.value.trim();
  if (!sourceGlobalId || !targetGlobalId) {
    setStatus("Выберите узел и укажите цель связи");
    return;
  }

  await connectNodes(sourceGlobalId, targetGlobalId, {
    typeGlobalId: connectEdgeType.value,
    relationLocalId: connectEdgeName.value.trim()
  });
});

searchForm.addEventListener("submit", event => {
  event.preventDefault();
  searchNodes();
});

searchStopButton.addEventListener("click", () => {
  state.searchAbort?.abort();
});

subgraphForm.addEventListener("submit", event => {
  event.preventDefault();
  loadSubgraph();
});

projectionBasis.addEventListener("change", async () => {
  state.schema.projectionBasis = projectionBasis.value;
  if (state.schema.projectionBasis === "typed") {
    await refreshTypes();
  }
  render();
  runSimulation(18);
});

loadBasisButton.addEventListener("click", loadBasis);
ensureBasisButton.addEventListener("click", ensureDefaultBasis);
refreshTypesButton.addEventListener("click", refreshTypes);
loadRelationsButton.addEventListener("click", loadRelationInstances);

assignNodeTypeButton.addEventListener("click", assignSelectedNodeType);

svg.addEventListener("pointerdown", event => {
  if (event.button !== 0 || event.target.closest(".node") || event.target.closest(".edge-button")) {
    return;
  }

  svg.setPointerCapture(event.pointerId);
  svg.classList.add("dragging");
  state.pointer = { x: event.clientX, y: event.clientY };
});

svg.addEventListener("pointermove", event => {
  if (state.dragging) {
    const position = state.positions.get(state.dragging.name);
    if (!position) {
      return;
    }

    const dx = (event.clientX - state.dragging.x) / state.view.scale;
    const dy = (event.clientY - state.dragging.y) / state.view.scale;
    position.x += dx;
    position.y += dy;
    state.dragging.x = event.clientX;
    state.dragging.y = event.clientY;
    render();
    return;
  }

  if (!state.pointer) {
    return;
  }

  const dx = event.clientX - state.pointer.x;
  const dy = event.clientY - state.pointer.y;
  state.view.x += dx;
  state.view.y += dy;
  state.pointer = { x: event.clientX, y: event.clientY };
  applyView();
});

svg.addEventListener("pointerup", event => {
  if (state.dragging) {
    svg.releasePointerCapture(event.pointerId);
    state.dragging = null;
    return;
  }

  if (state.pointer) {
    svg.releasePointerCapture(event.pointerId);
  }

  state.pointer = null;
  svg.classList.remove("dragging");
});

svg.addEventListener("wheel", event => {
  event.preventDefault();
  const rect = svg.getBoundingClientRect();
  const mouseX = event.clientX - rect.left;
  const mouseY = event.clientY - rect.top;
  const before = screenToGraph(mouseX, mouseY);
  const scale = Math.min(2.8, Math.max(0.25, state.view.scale * Math.exp(-event.deltaY * 0.0012)));
  state.view.scale = scale;
  state.view.x = mouseX - before.x * scale;
  state.view.y = mouseY - before.y * scale;
  applyView();
}, { passive: false });

async function loadRoot(name) {
  if (!name) {
    setStatus("Введите GlobalId узла");
    return;
  }

  state.rootName = name;
  state.selectedName = name;
  state.loaded.clear();
  state.parentByNode.clear();
  state.positions.clear();
  state.velocities.clear();
  seedPosition(name, null, 0);
  await loadNode(name, null);
  const url = new URL(window.location.href);
  url.searchParams.set("globalId", state.rootName ?? name);
  window.history.replaceState({}, "", url);
  fitView();
}

async function loadNode(name, fromName, options = {}) {
  const select = options.select ?? true;
  setBusy(true);
  try {
    const expansion = normalizeNodeResponse(await apiJson(`/api/graph/nodes?${toGlobalIdQuery(name)}`));
    storeNodeExpansion(expansion, fromName, { select });

    render();
    renderTypeControls();
    runSimulation(34);
    setStatus(`Развернуто узлов: ${state.loaded.size}`);
  } catch (error) {
    setStatus(error.message);
  } finally {
    setBusy(false);
  }
}

async function loadNeighbor(anchorName, neighborLocalId) {
  if (!anchorName || !neighborLocalId) {
    setStatus("Не удалось определить соседа для раскрытия");
    return;
  }

  setBusy(true);
  try {
    const expansion = normalizeNodeResponse(await apiJson(
      `/api/graph/nodes/${encodeURIComponent(anchorName)}/neighbor/${encodeURIComponent(neighborLocalId)}`));
    const alreadyLoaded = state.loaded.has(expansion.name);
    storeNodeExpansion(expansion, anchorName, { select: true });

    render();
    renderTypeControls();
    runSimulation(alreadyLoaded ? 18 : 34);
    setStatus(alreadyLoaded
      ? `Узел "${expansion.displayName}" уже был загружен, связь добавлена`
      : `Развернуто узлов: ${state.loaded.size}`);
  } catch (error) {
    setStatus(formatNeighborError(error, neighborLocalId));
  } finally {
    setBusy(false);
  }
}

function storeNodeExpansion(expansion, fromName, options = {}) {
  const select = options.select ?? true;
  const existing = state.loaded.get(expansion.name);
  const stored = existing ? mergeNodeResponses(existing, expansion) : expansion;
  state.loaded.set(expansion.name, stored);

  if (!fromName && state.rootName && !state.loaded.has(state.rootName)) {
    state.rootName = expansion.name;
  }

  if (select) {
    state.selectedName = expansion.name;
  }

  seedPosition(expansion.name, fromName, 0);
  if (fromName && fromName !== expansion.name && !state.parentByNode.has(expansion.name)) {
    state.parentByNode.set(expansion.name, fromName);
  }

  stored.edges.forEach((edge, index) => {
    seedPosition(getOtherEndpoint(edge, expansion.name), expansion.name, index);
  });

  return stored;
}

function mergeNodeResponses(existing, expansion) {
  return {
    ...existing,
    ...expansion,
    attributes: expansion.attributes ?? existing.attributes ?? {},
    edges: mergeEdges(existing.edges, expansion.edges)
  };
}

function mergeEdges(left = [], right = []) {
  const edges = new Map();
  [...left, ...right].forEach(edge => {
    if (edge.sourceGlobalId && edge.targetGlobalId) {
      edges.set(edgeKey(edge.sourceGlobalId, edge.targetGlobalId), edge);
    }
  });
  return [...edges.values()];
}

async function createNode(name, options = {}) {
  const parentGlobalId = options.parentGlobalId || null;
  const typeGlobalId = options.typeGlobalId || "";
  setBusy(true);
  try {
    const attributes = typeGlobalId
      ? {
          [graphKindAttribute]: "instance",
          [graphElementAttribute]: "node",
          [graphTypeNameAttribute]: typeGlobalId
        }
      : null;
    const created = await createGraphNode(name, parentGlobalId, attributes);
    if (typeGlobalId) {
      await connectGraphNodes(created.globalId, typeGlobalId);
    }
    createNodeName.value = "";
    createNodeParent.value = "";
    state.rootName = state.rootName ?? created.name;
    state.selectedName = created.name;
    seedPosition(created.name, state.rootName === created.name ? null : state.rootName, state.loaded.size);
    if (typeGlobalId) {
      const expanded = normalizeNodeResponse(await apiJson(`/api/graph/nodes?${toGlobalIdQuery(created.globalId)}`));
      storeNodeExpansion(expanded, null, { select: true });
    } else {
      state.loaded.set(created.name, created);
    }
    render();
    renderTypeControls();
    setActiveTab("node");
    setStatus(`Создан узел "${created.displayName}"`);
  } catch (error) {
    setStatus(error.message);
  } finally {
    setBusy(false);
  }
}

async function createGraphNode(localId, parentGlobalId = null, attributes = null) {
  return normalizeNodeResponse(await apiJson("/api/graph/nodes", {
    method: "POST",
    body: JSON.stringify({
      localId,
      parentGlobalId: parentGlobalId ? parseGlobalId(parentGlobalId) : null,
      attributes
    })
  }));
}

async function saveSelectedNode() {
  const nodeName = state.selectedName;
  if (!nodeName) {
    setStatus("Узел не выбран");
    return;
  }

  setBusy(true);
  try {
    await apiJson(`/api/graph/nodes?${toGlobalIdQuery(nodeName)}`, {
      method: "PUT",
      body: JSON.stringify({ attributes: readAttributeEditor() }),
      expectJson: false
    });
    await loadNode(nodeName, null, { select: true });
    setStatus(`Сохранен узел "${displayName(nodeName)}"`);
  } catch (error) {
    setStatus(error.message);
  } finally {
    setBusy(false);
  }
}

async function deleteSelectedNode() {
  const nodeName = state.selectedName;
  if (!nodeName) {
    setStatus("Узел не выбран");
    return;
  }

  setBusy(true);
  try {
    await apiJson(`/api/graph/nodes?${toGlobalIdQuery(nodeName)}`, {
      method: "DELETE",
      expectJson: false
    });
    removeLocalNode(nodeName);
    render();
    renderTypeControls();
    setStatus(`Удален узел "${displayName(nodeName)}"`);
  } catch (error) {
    setStatus(error.message);
  } finally {
    setBusy(false);
  }
}

async function connectNodes(sourceGlobalId, targetGlobalId, options = {}) {
  const typeGlobalId = options.typeGlobalId || "";
  setBusy(true);
  try {
    if (typeGlobalId) {
      const relation = await createTypedEdgeRelation(
        sourceGlobalId,
        targetGlobalId,
        typeGlobalId,
        options.relationLocalId || "");
      const subgraph = await loadSubgraphForRoots([relation.globalId, sourceGlobalId, targetGlobalId, typeGlobalId], 2);
      mergeSubgraphIntoViewer(subgraph, { select: false });
      state.selectedName = sourceGlobalId;
    } else {
      await connectGraphNodes(sourceGlobalId, targetGlobalId);
      await loadNode(sourceGlobalId, null, { select: true });
    }
    connectTargetName.value = "";
    connectEdgeName.value = "";
    render();
    renderTypeControls();
    runSimulation(32);
    setStatus(`Связаны "${displayName(sourceGlobalId)}" и "${displayName(targetGlobalId)}"`);
  } catch (error) {
    setStatus(error.message);
  } finally {
    setBusy(false);
  }
}

async function connectGraphNodes(sourceGlobalId, targetGlobalId) {
  await apiJson("/api/graph/connections", {
    method: "POST",
    body: JSON.stringify({
      sourceGlobalId: parseGlobalId(sourceGlobalId),
      targetGlobalId: parseGlobalId(targetGlobalId)
    }),
    expectJson: false
  });
}

async function loadBasis() {
  const basisName = basisNodeInput.value.trim();
  if (!basisName) {
    readBasisInputs();
    await refreshTypes();
    render();
    return;
  }

  setBusy(true);
  try {
    const basisNode = normalizeNodeResponse(await apiJson(`/api/graph/nodes?${toGlobalIdQuery(basisName)}`));
    state.schema.basis = {
      nodeTypeRoot: basisNode.attributes?.nodeTypeRoot || defaultBasis.nodeTypeRoot,
      edgeTypeRoot: basisNode.attributes?.edgeTypeRoot || defaultBasis.edgeTypeRoot,
      relationRoot: basisNode.attributes?.relationRoot || defaultBasis.relationRoot
    };
    syncBasisInputs();
    storeNodeExpansion(basisNode, null, { select: false });
    await refreshTypes({ preserveBusy: true });
    render();
    setStatus(`Базис загружен: ${basisName}`);
  } catch (error) {
    setStatus(error.message);
  } finally {
    setBusy(false);
  }
}

async function ensureDefaultBasis() {
  setBusy(true);
  try {
    readBasisInputs();
    const basis = getBasis();
    await ensurePath(basis.nodeTypeRoot, { [graphKindAttribute]: "type-root", [graphElementAttribute]: "node" });
    await ensurePath(basis.edgeTypeRoot, { [graphKindAttribute]: "type-root", [graphElementAttribute]: "edge" });
    await ensurePath(basis.relationRoot, { [graphKindAttribute]: "relation-root", [graphElementAttribute]: "edge" });

    await upsertGraphType(basis.nodeTypeRoot, "Type", "Type", "#334155", "node", false, 90);
    await upsertGraphType(basis.nodeTypeRoot, "Instance", "Instance", "#0f766e", "node", false, 70);
    await upsertGraphType(basis.edgeTypeRoot, "Type", "Type", "#7c2d12", "edge", true, 60);
    await upsertGraphType(basis.edgeTypeRoot, "Instance", "Instance", "#b45309", "edge", true, 50);

    const basisName = basisNodeInput.value.trim();
    if (basisName) {
      const segments = parseGlobalId(basisName);
      const localId = segments[segments.length - 1];
      const parent = segments.length > 1 ? segments.slice(0, -1).join("/") : null;
      if (parent) {
        await ensurePath(parent);
      }
      await createGraphNode(localId, parent, {
        [graphKindAttribute]: "basis",
        nodeTypeRoot: basis.nodeTypeRoot,
        edgeTypeRoot: basis.edgeTypeRoot,
        relationRoot: basis.relationRoot
      });
    }

    await refreshTypes({ preserveBusy: true });
    render();
    setStatus("Базовый базис создан или обновлен");
  } catch (error) {
    setStatus(error.message);
  } finally {
    setBusy(false);
  }
}

async function refreshTypes(options = {}) {
  if (!options.preserveBusy) {
    setBusy(true);
  }

  try {
    readBasisInputs();
    const basis = getBasis();
    const response = await loadSubgraphForRoots([basis.nodeTypeRoot, basis.edgeTypeRoot], 4);
    mergeSubgraphIntoViewer(response, { select: false });
    const nodes = (response.nodes ?? []).map(normalizeNodeResponse);
    state.schema.nodeTypes = new Map();
    state.schema.edgeTypes = new Map();

    nodes
      .filter(node => node.globalId !== basis.nodeTypeRoot && node.globalId !== basis.edgeTypeRoot)
      .forEach(node => {
        if (isChildOf(node.globalId, basis.nodeTypeRoot)) {
          state.schema.nodeTypes.set(node.globalId, toGraphType(node, "node"));
        } else if (isChildOf(node.globalId, basis.edgeTypeRoot)) {
          state.schema.edgeTypes.set(node.globalId, toGraphType(node, "edge"));
        }
      });

    renderTypeControls();
    render();
    setStatus(`Типы: ${state.schema.nodeTypes.size} узлов, ${state.schema.edgeTypes.size} связей`);
  } catch (error) {
    setStatus(error.message);
  } finally {
    if (!options.preserveBusy) {
      setBusy(false);
    }
  }
}

async function loadRelationInstances() {
  setBusy(true);
  try {
    readBasisInputs();
    const matches = await searchNodeMatches({
      return: ["n"],
      where: {
        kind: "attribute",
        node: variableSelector("n"),
        key: graphKindAttribute,
        operator: "equals",
        value: "edge-instance"
      },
      limit: 500
    });
    const relationIds = matches
      .map(match => normalizeNodeResponse(match.node).globalId)
      .filter(globalId => isChildOf(globalId, getBasis().relationRoot));

    for (const relationId of relationIds) {
      const subgraph = await loadSubgraphForRoots([relationId], 2);
      mergeSubgraphIntoViewer(subgraph, { select: false });
    }

    renderTypeControls();
    render();
    runSimulation(32);
    setStatus(`Инстансы связей загружены: ${relationIds.length}`);
  } catch (error) {
    setStatus(error.message);
  } finally {
    setBusy(false);
  }
}

async function assignSelectedNodeType() {
  const nodeName = state.selectedName;
  const typeGlobalId = assignNodeType.value;
  if (!nodeName || !state.loaded.has(nodeName) || !typeGlobalId) {
    setStatus("Выберите загруженный узел и тип узла");
    return;
  }

  setBusy(true);
  try {
    await connectGraphNodes(nodeName, typeGlobalId);
    await loadNode(nodeName, null, { select: true });
    render();
    setStatus(`Тип ${displayName(typeGlobalId)} назначен узлу ${displayName(nodeName)}`);
  } catch (error) {
    setStatus(error.message);
  } finally {
    setBusy(false);
  }
}

async function createTypedEdgeRelation(source, target, typeGlobalId, relationLocalId = "") {
  readBasisInputs();
  await ensurePath(getBasis().relationRoot);
  const relation = await createGraphNode(relationLocalId || createRelationLocalId(typeGlobalId), getBasis().relationRoot, {
    [graphKindAttribute]: "edge-instance",
    [graphElementAttribute]: "edge",
    [graphTypeNameAttribute]: typeGlobalId
  });
  const sourcePort = await createGraphNode("source", relation.globalId, {
    [graphKindAttribute]: "edge-port",
    [graphRoleAttribute]: "source"
  });
  const targetPort = await createGraphNode("target", relation.globalId, {
    [graphKindAttribute]: "edge-port",
    [graphRoleAttribute]: "target"
  });

  await connectGraphNodes(relation.globalId, typeGlobalId);
  await connectGraphNodes(sourcePort.globalId, source);
  await connectGraphNodes(targetPort.globalId, target);
  return relation;
}

async function upsertGraphType(rootGlobalId, localId, label, color, element, directed = false, rank = element === "node" ? 50 : 30) {
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
  return createGraphNode(localId, rootGlobalId, attrs);
}

async function ensurePath(globalId, leafAttributes = null) {
  const segments = parseGlobalId(globalId);
  let parent = null;
  let current = "";
  for (let index = 0; index < segments.length; index += 1) {
    const segment = segments[index];
    current = current ? `${current}/${segment}` : segment;
    const attrs = index === segments.length - 1 ? leafAttributes : null;
    await createGraphNode(segment, parent, attrs);
    parent = current;
  }
}

async function searchNodeMatches(query) {
  const response = await fetch("/api/graph/search/nodes", {
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
  await readNdjsonStream(response, match => matches.push(match));
  return matches;
}

function setSearchQueryTemplate(name) {
  const currentName = state.selectedName || rootInput.value.trim() || "node-name";
  const templates = {
    all: {
      return: ["n"],
      where: {
        kind: "node",
        node: variableSelector("n")
      },
      limit: 50
    },
    text: {
      return: ["n"],
      where: {
        kind: "text",
        node: variableSelector("n"),
        value: "sample"
      },
      limit: 50
    },
    connected: {
      return: ["n"],
      where: {
        kind: "connected",
        left: variableSelector("n"),
        right: literalSelector(currentName || "node-name")
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
            left: variableSelector("n"),
            right: variableSelector("x")
          },
          {
            kind: "attribute",
            node: variableSelector("x"),
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
            left: variableSelector("n"),
            right: variableSelector("x")
          },
          {
            kind: "connected",
            left: variableSelector("x"),
            right: variableSelector("z")
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
            node: variableSelector("x")
          },
          {
            kind: "not",
            expression: {
              kind: "exists",
              variables: ["y"],
              expression: {
                kind: "connected",
                left: variableSelector("x"),
                right: variableSelector("y")
              }
            }
          }
        ]
      },
      limit: 50
    }
  };

  searchQueryJson.value = JSON.stringify(templates[name] ?? templates.all, null, 2);
}

function variableSelector(name) {
  return { kind: "var", name };
}

function literalSelector(name) {
  return { kind: "literal", name };
}

function parseGlobalId(value) {
  return value.split("/").filter(Boolean);
}

function toGlobalIdQuery(value) {
  return parseGlobalId(value)
    .map(segment => `globalId=${encodeURIComponent(segment)}`)
    .join("&");
}

function normalizeNodeResponse(node) {
  const globalId = node.globalId ?? node.name;
  const localId = node.localId ?? node.name;
  return {
    ...node,
    name: globalId,
    globalId,
    localId,
    displayName: localId,
    edges: (node.edges ?? []).map(normalizeEdgeResponse)
  };
}

function normalizeEdgeResponse(edge) {
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

function displayName(globalId) {
  return state.loaded.get(globalId)?.displayName ?? globalId;
}

function edgeEndpointDisplayName(edge, globalId) {
  if (edge.sourceGlobalId === globalId) {
    return edge.sourceLocalId ?? displayName(globalId);
  }
  if (edge.targetGlobalId === globalId) {
    return edge.targetLocalId ?? displayName(globalId);
  }
  return displayName(globalId);
}

function edgeNeighborLocalId(edge, anchorName) {
  if (edge.neighborLocalId) {
    return edge.neighborLocalId;
  }

  return edge.sourceGlobalId === anchorName
    ? edge.targetLocalId
    : edge.sourceLocalId;
}

async function searchNodes() {
  let query;
  try {
    query = JSON.parse(searchQueryJson.value.trim());
  } catch (error) {
    setStatus(`JSON: ${error.message}`);
    return;
  }

  state.searchAbort?.abort();
  const controller = new AbortController();
  state.searchAbort = controller;
  setSearchStreaming(true);
  renderSearchResults([]);

  let count = 0;
  try {
    const response = await fetch("/api/graph/search/nodes", {
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

    await readNdjsonStream(response, match => {
      count += 1;
      appendSearchResult(match);
      setStatus(`Найдено решений: ${count}`);
    });

    setStatus(`Найдено решений: ${count}`);
  } catch (error) {
    if (error.name === "AbortError") {
      setStatus(`Поиск остановлен: ${count}`);
    } else {
      setStatus(error.message);
    }
  } finally {
    if (state.searchAbort === controller) {
      state.searchAbort = null;
      setSearchStreaming(false);
    }
  }
}

async function readNdjsonStream(response, onItem) {
  if (!response.body) {
    parseNdjsonLines(await response.text(), onItem);
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
    parseNdjsonLines(lines.join("\n"), onItem);
  }

  buffer += decoder.decode();
  parseNdjsonLines(buffer, onItem);
}

function parseNdjsonLines(text, onItem) {
  text
    .split("\n")
    .map(line => line.trim())
    .filter(Boolean)
    .forEach(line => onItem(JSON.parse(line)));
}

function setSearchStreaming(value) {
  searchSubmitButton.disabled = value;
  searchStopButton.disabled = !value;
}

async function loadSubgraph() {
  const roots = parseCsv(document.querySelector("#subgraph-roots").value);
  if (roots.length === 0) {
    setStatus("Укажите корневые узлы");
    return;
  }

  setBusy(true);
  try {
    const response = await apiJson("/api/graph/subgraph", {
      method: "POST",
      body: JSON.stringify({
        globalIds: roots.map(parseGlobalId),
        maxDepth: readNumber("#subgraph-depth", 1),
        includeDisconnectedRoots: document.querySelector("#subgraph-include-disconnected").checked
      })
    });
    loadSubgraphIntoViewer(response, roots);
    renderSubgraphResults(response);
    renderTypeControls();
    setStatus(`Подграф: ${(response.nodes ?? []).length} узлов, ${(response.edges ?? []).length} ребер`);
  } catch (error) {
    setStatus(error.message);
  } finally {
    setBusy(false);
  }
}

function loadSubgraphIntoViewer(response, roots) {
  state.loaded.clear();
  state.parentByNode.clear();
  state.positions.clear();
  state.velocities.clear();
  const nodes = (response.nodes ?? []).map(normalizeNodeResponse);
  const edges = (response.edges ?? []).map(normalizeEdgeResponse);
  state.rootName = roots[0] ?? nodes[0]?.name ?? null;
  state.selectedName = state.rootName;

  nodes.forEach((node, index) => {
    state.loaded.set(node.name, {
      ...node,
      edges: edges.filter(edge => edge.sourceGlobalId === node.name || edge.targetGlobalId === node.name)
    });
    seedSubgraphPosition(node.name, index, nodes.length);
  });

  render();
  renderTypeControls();
  runSimulation(40);
  fitView();
}

function mergeSubgraphIntoViewer(response, options = {}) {
  const select = options.select ?? false;
  const nodes = (response.nodes ?? []).map(normalizeNodeResponse);
  const edges = (response.edges ?? []).map(normalizeEdgeResponse);
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
      edges: mergeEdges(node.edges, edgesByNode.get(node.name) ?? [])
    };
    storeNodeExpansion(expansion, options.fromName ?? null, {
      select: select && index === 0
    });
  });
  renderTypeControls();
}

async function loadSubgraphForRoots(roots, maxDepth = 1) {
  return apiJson("/api/graph/subgraph", {
    method: "POST",
    body: JSON.stringify({
      globalIds: roots.map(parseGlobalId),
      maxDepth
    })
  });
}

function collapseNode(name) {
  if (!name || name === state.rootName || !state.loaded.has(name)) {
    return;
  }

  const fallbackSelection = state.parentByNode.get(name) ?? state.rootName;
  const removed = new Set();
  const visit = current => {
    removed.add(current);
    for (const [child, parent] of state.parentByNode.entries()) {
      if (parent === current) {
        visit(child);
      }
    }
  };

  visit(name);
  removed.forEach(nodeName => removeLocalNode(nodeName, false, false));
  state.selectedName = fallbackSelection;
  render();
  runSimulation(18);
  setStatus(`Развернуто узлов: ${state.loaded.size}`);
}

function removeLocalNode(name, selectFallback = true, pruneEdges = true) {
  state.loaded.delete(name);
  if (pruneEdges) {
    state.positions.delete(name);
    state.velocities.delete(name);
  }
  state.parentByNode.delete(name);
  for (const [child, parent] of [...state.parentByNode.entries()]) {
    if (parent === name) {
      state.parentByNode.delete(child);
    }
  }

  if (pruneEdges) {
    for (const expansion of state.loaded.values()) {
      expansion.edges = (expansion.edges ?? [])
        .filter(edge => edge.sourceGlobalId !== name && edge.targetGlobalId !== name);
    }
  }

  if (selectFallback && state.selectedName === name) {
    state.selectedName = state.loaded.keys().next().value ?? null;
    state.rootName = state.selectedName;
  }
}

function handleEndpointClick(edge, anchorName) {
  const otherName = edge.sourceGlobalId === anchorName ? edge.targetGlobalId : edge.sourceGlobalId;
  const anchorLoaded = state.loaded.has(anchorName);
  const otherLoaded = state.loaded.has(otherName);

  if (anchorLoaded && !otherLoaded) {
    loadNeighbor(anchorName, edgeNeighborLocalId(edge, anchorName));
    return;
  }

  if (!anchorLoaded && otherLoaded) {
    loadNeighbor(otherName, edgeNeighborLocalId(edge, otherName));
    return;
  }

  if (anchorLoaded && otherLoaded) {
    if (state.parentByNode.get(otherName) === anchorName) {
      collapseNode(otherName);
      return;
    }

    if (state.parentByNode.get(anchorName) === otherName && anchorName !== state.rootName) {
      collapseNode(anchorName);
      return;
    }

    state.selectedName = otherName;
    render();
  }
}

function buildGraph() {
  const physical = buildPhysicalGraph();
  if (state.schema.projectionBasis === "empty") {
    return physical;
  }

  return buildProjectedGraph(physical);
}

function buildPhysicalGraph() {
  const nodes = new Map();
  const edges = new Map();

  for (const expansion of state.loaded.values()) {
    nodes.set(expansion.name, {
      name: expansion.name,
      displayName: expansion.displayName,
      localId: expansion.localId,
      globalId: expansion.globalId,
      attributes: expansion.attributes ?? {}
    });

    (expansion.edges ?? []).forEach(edge => {
      const key = edgeKey(edge.sourceGlobalId, edge.targetGlobalId);
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

function buildProjectedGraph(physical) {
  const relationInstances = discoverRelationInstances(physical);
  const hidden = new Set();

  for (const relation of relationInstances) {
    hidden.add(relation.relationGlobalId);
    relation.portGlobalIds.forEach(name => hidden.add(name));
  }

  for (const type of [...state.schema.nodeTypes.values(), ...state.schema.edgeTypes.values()]) {
    hidden.add(type.globalId);
  }

  const nodeTypeAssignments = getNodeTypeAssignments(physical);
  const visibleNodes = physical.nodes
    .filter(node => !hidden.has(node.name))
    .filter(node => !isSchemaRootNode(node.name))
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
      displayName: formatProjectedNodeName(node, nodeType)
    };
  });

  const hiddenPhysicalEdges = new Set();
  for (const relation of relationInstances) {
    relation.physicalEdgeKeys.forEach(key => hiddenPhysicalEdges.add(key));
  }
  for (const [nodeId, nodeType] of nodeTypeAssignments) {
    hiddenPhysicalEdges.add(edgeKey(nodeId, nodeType.globalId));
  }

  const physicalEdges = physical.edges.filter(edge => {
    return visibleNodeIds.has(edge.sourceGlobalId)
      && visibleNodeIds.has(edge.targetGlobalId)
      && !hiddenPhysicalEdges.has(edge.key ?? edgeKey(edge.sourceGlobalId, edge.targetGlobalId));
  });

  const projectedEdges = relationInstances
    .filter(relation => visibleNodeIds.has(relation.sourceGlobalId) && visibleNodeIds.has(relation.targetGlobalId))
    .filter(relation => relation.type?.visible !== false)
    .map(relation => ({
      key: `projected:${relation.relationGlobalId}`,
      sourceGlobalId: relation.sourceGlobalId,
      targetGlobalId: relation.targetGlobalId,
      sourceLocalId: getLocalId(relation.sourceGlobalId),
      targetLocalId: getLocalId(relation.targetGlobalId),
      relationGlobalId: relation.relationGlobalId,
      typeGlobalId: relation.type?.globalId,
      label: relation.type?.labelVisible === false ? "" : relation.type?.label ?? relation.displayName,
      color: relation.type?.color,
      directed: relation.type?.directed ?? false,
      typeRank: relation.type?.rank,
      projected: true
    }));

  return applyBasisRanks({
    nodes: typedNodes,
    edges: [...physicalEdges, ...projectedEdges]
  });
}

function applyBasisRanks(graph) {
  const nodeStats = new Map(graph.nodes.map(node => [node.name, {
    weightedDegree: 0,
    focusBoost: 0,
    reasons: []
  }]));

  const rankedEdges = graph.edges.map(edge => {
    const edgeType = edge.typeGlobalId ? state.schema.edgeTypes.get(edge.typeGlobalId) : null;
    const basisWeight = readRank(edge.typeRank ?? edgeType?.rank, edge.projected ? 35 : 8);
    const rank = roundRank(basisWeight);
    const sourceStats = nodeStats.get(edge.sourceGlobalId);
    const targetStats = nodeStats.get(edge.targetGlobalId);

    if (sourceStats) {
      sourceStats.weightedDegree += basisWeight;
    }
    if (targetStats) {
      targetStats.weightedDegree += basisWeight;
    }
    if (state.selectedName === edge.sourceGlobalId && targetStats) {
      targetStats.focusBoost += Math.min(25, basisWeight * 0.35);
    }
    if (state.selectedName === edge.targetGlobalId && sourceStats) {
      sourceStats.focusBoost += Math.min(25, basisWeight * 0.35);
    }

    return {
      ...edge,
      viewRank: rank,
      viewRankReason: edgeType?.label
        ? `edge type ${edgeType.label}: ${formatRank(rank)}`
        : `physical edge: ${formatRank(rank)}`
    };
  });

  const rankedNodes = graph.nodes.map(node => {
    const stats = nodeStats.get(node.name);
    const typePriority = readRank(node.typeRank, node.typeGlobalId ? 50 : 20);
    const degreeScore = Math.log1p(stats?.weightedDegree ?? 0) * 8;
    const rootBoost = node.name === state.rootName ? 18 : 0;
    const selectedBoost = node.name === state.selectedName ? 30 : 0;
    const rank = roundRank(typePriority + degreeScore + rootBoost + selectedBoost + (stats?.focusBoost ?? 0));
    const reasons = [
      node.typeLabel ? `type ${node.typeLabel}: ${formatRank(typePriority)}` : `untyped: ${formatRank(typePriority)}`,
      `links: ${formatRank(degreeScore)}`
    ];
    if (rootBoost) {
      reasons.push(`root: ${formatRank(rootBoost)}`);
    }
    if (selectedBoost) {
      reasons.push(`selected: ${formatRank(selectedBoost)}`);
    }
    if (stats?.focusBoost) {
      reasons.push(`focus: ${formatRank(stats.focusBoost)}`);
    }

    return {
      ...node,
      viewRank: rank,
      viewRadius: rankToRadius(rank),
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

function formatProjectedNodeName(node, nodeType) {
  const parts = [node.displayName];
  if (nodeType?.label) {
    parts.push(nodeType.label);
  }
  if (nodeType?.infoAttribute && node.attributes?.[nodeType.infoAttribute]) {
    parts.push(node.attributes[nodeType.infoAttribute]);
  }
  return parts.join(" : ");
}

function discoverRelationInstances(physical) {
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
    .filter(node => isRelationInstanceNode(node))
    .map(relation => {
      const relationEdges = edgesByNode.get(relation.name) ?? [];
      const type = relationEdges
        .map(edge => getOtherEndpoint(edge, relation.name))
        .map(name => state.schema.edgeTypes.get(name))
        .find(Boolean) ?? null;
      const ports = physical.nodes
        .filter(node => isChildOf(node.name, relation.name))
        .filter(node => getAttribute(node, graphKindAttribute) === "edge-port" || getAttribute(node, graphRoleAttribute));
      const sourcePort = ports.find(node => getAttribute(node, graphRoleAttribute) === "source" || node.localId === "source");
      const targetPort = ports.find(node => getAttribute(node, graphRoleAttribute) === "target" || node.localId === "target");
      const sourceGlobalId = sourcePort ? getPortEndpoint(sourcePort.name, edgesByNode, relation.name) : null;
      const targetGlobalId = targetPort ? getPortEndpoint(targetPort.name, edgesByNode, relation.name) : null;

      if (!sourceGlobalId || !targetGlobalId) {
        return null;
      }

      const physicalEdgeKeys = new Set(relationEdges.map(edge => edge.key ?? edgeKey(edge.sourceGlobalId, edge.targetGlobalId)));
      for (const port of ports) {
        physicalEdgeKeys.add(edgeKey(relation.name, port.name));
        for (const edge of edgesByNode.get(port.name) ?? []) {
          physicalEdgeKeys.add(edge.key ?? edgeKey(edge.sourceGlobalId, edge.targetGlobalId));
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

function getPortEndpoint(portGlobalId, edgesByNode, relationGlobalId) {
  for (const edge of edgesByNode.get(portGlobalId) ?? []) {
    const other = getOtherEndpoint(edge, portGlobalId);
    if (other !== relationGlobalId && !isChildOf(other, relationGlobalId)) {
      return other;
    }
  }

  return null;
}

function getNodeTypeAssignments(physical) {
  const assignments = new Map();
  for (const edge of physical.edges) {
    const sourceType = state.schema.nodeTypes.get(edge.sourceGlobalId);
    const targetType = state.schema.nodeTypes.get(edge.targetGlobalId);
    if (sourceType && !targetType) {
      assignments.set(edge.targetGlobalId, sourceType);
    } else if (targetType && !sourceType) {
      assignments.set(edge.sourceGlobalId, targetType);
    }
  }
  return assignments;
}

function isRelationInstanceNode(node) {
  if (getAttribute(node, graphKindAttribute) === "edge-instance") {
    return true;
  }

  return isChildOf(node.name, getBasis().relationRoot)
    && node.name !== getBasis().relationRoot
    && !node.name.slice(getBasis().relationRoot.length + 1).includes("/");
}

function isSchemaRootNode(globalId) {
  const basis = getBasis();
  return globalId === "graphdata"
    || globalId === "graphdata/types"
    || globalId === basis.nodeTypeRoot
    || globalId === basis.edgeTypeRoot
    || globalId === basis.relationRoot;
}

function isChildOf(globalId, parentGlobalId) {
  return Boolean(parentGlobalId)
    && globalId.length > parentGlobalId.length
    && globalId.startsWith(`${parentGlobalId}/`);
}

function getAttribute(node, key) {
  return node.attributes?.[key] ?? node.attributes?.[key.toLowerCase()] ?? "";
}

function getLocalId(globalId) {
  const segments = parseGlobalId(globalId);
  return segments.length > 0 ? segments[segments.length - 1] : globalId;
}

function render() {
  const graph = buildGraph();
  viewport.replaceChildren();
  emptyState.classList.toggle("hidden", graph.nodes.length > 0);

  const edgeLayer = createSvg("g", { class: "edges" });
  const nodeLayer = createSvg("g", { class: "nodes" });
  const buttonLayer = createSvg("g", { class: "edge-buttons" });
  viewport.append(edgeLayer, nodeLayer, buttonLayer);

  const nodesByName = new Map(graph.nodes.map(node => [node.name, node]));
  graph.edges.forEach(edge => renderEdge(edgeLayer, buttonLayer, edge, nodesByName));
  graph.nodes.forEach(node => renderNode(nodeLayer, node));
  renderInspector(graph);
  applyView();
}

function renderEdge(edgeLayer, buttonLayer, edge, nodesByName) {
  const sourceLoaded = state.loaded.has(edge.sourceGlobalId);
  const targetLoaded = state.loaded.has(edge.targetGlobalId);
  const source = state.positions.get(edge.sourceGlobalId);
  const target = state.positions.get(edge.targetGlobalId);

  if (!source || !target || (!sourceLoaded && !targetLoaded)) {
    return;
  }

  if (sourceLoaded && targetLoaded) {
    const sourceButton = pointOnCircle(source, target, getNodeEndpointOffset(nodesByName.get(edge.sourceGlobalId)));
    const targetButton = pointOnCircle(target, source, getNodeEndpointOffset(nodesByName.get(edge.targetGlobalId)));
    const line = createSvg("line", {
      class: "edge-line",
      x1: sourceButton.x,
      y1: sourceButton.y,
      x2: targetButton.x,
      y2: targetButton.y,
      style: edge.color ? `stroke:${edge.color}` : ""
    });

    edgeLayer.append(line);
    renderEdgeLabel(edgeLayer, source, target, edge);
    renderEndpointButton(buttonLayer, sourceButton, edge, edge.sourceGlobalId, false);
    renderEndpointButton(buttonLayer, targetButton, edge, edge.targetGlobalId, false);
    return;
  }

  const anchorName = sourceLoaded ? edge.sourceGlobalId : edge.targetGlobalId;
  const hiddenName = sourceLoaded ? edge.targetGlobalId : edge.sourceGlobalId;
  const anchor = sourceLoaded ? source : target;
  const hidden = sourceLoaded ? target : source;
  const buttonPoint = pointOnCircle(anchor, hidden, getNodeEndpointOffset(nodesByName.get(anchorName)));
  renderEndpointButton(buttonLayer, buttonPoint, edge, anchorName, true, hiddenName);
}

function getNodeEndpointOffset(node) {
  return (node?.viewRadius ?? nodeRadius) + 9;
}

function renderEdgeLabel(layer, source, target, edge) {
  if (!edge.label && !edge.directed) {
    return;
  }

  const text = createSvg("text", {
    class: "edge-label",
    x: (source.x + target.x) / 2,
    y: (source.y + target.y) / 2 - 7,
    style: edge.color ? `fill:${edge.color}` : ""
  });
  text.textContent = `${edge.label ?? ""}${edge.directed ? " ->" : ""}`.trim();
  layer.append(text);
}

function renderEndpointButton(layer, point, edge, anchorName, collapsed, hiddenName = null) {
  const otherName = hiddenName ?? (edge.sourceGlobalId === anchorName ? edge.targetGlobalId : edge.sourceGlobalId);
  const otherDisplayName = edgeEndpointDisplayName(edge, otherName);
  const group = createSvg("g", {
    class: `edge-button ${collapsed ? "collapsed" : "expanded"}`,
    transform: `translate(${point.x} ${point.y})`,
    role: "button",
    tabindex: "0",
    "aria-label": collapsed ? `Развернуть ${otherDisplayName}` : `Выбрать ${otherDisplayName}`
  });
  const title = createSvg("title", {});
  const titleLines = [collapsed ? `Развернуть ${otherDisplayName}` : `Выбрать ${otherDisplayName}`];
  if (Number.isFinite(edge.viewRank)) {
    titleLines.push(`Edge rank: ${formatRank(edge.viewRank)}`);
  }
  if (edge.viewRankReason) {
    titleLines.push(edge.viewRankReason);
  }
  title.textContent = titleLines.join("\n");

  const hit = createSvg("circle", { class: "edge-button-hit", r: 17, cx: 0, cy: 0 });
  const core = createSvg("circle", {
    class: "edge-button-core",
    r: collapsed ? 8 : 6,
    cx: 0,
    cy: 0
  });

  const activate = event => {
    event.stopPropagation();
    handleEndpointClick(edge, anchorName);
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

function renderNode(layer, node) {
  const position = state.positions.get(node.name);
  if (!position) {
    return;
  }

  const group = createSvg("g", {
    class: `node${state.selectedName === node.name ? " selected" : ""}`,
    transform: `translate(${position.x} ${position.y})`,
    tabindex: "0",
    "aria-label": node.displayName ?? node.name
  });
  const title = createSvg("title", {});
  const titleLines = [node.globalId ?? node.name];
  if (node.typeLabel) {
    titleLines.push(`Type: ${node.typeLabel}`);
  }
  if (Number.isFinite(node.viewRank)) {
    titleLines.push(`Rank: ${formatRank(node.viewRank)}`);
  }
  if (node.viewRankReason) {
    titleLines.push(node.viewRankReason);
  }
  title.textContent = titleLines.join("\n");
  const circle = createSvg("circle", {
    class: "node-shell",
    r: node.viewRadius ?? nodeRadius,
    cx: 0,
    cy: 0,
    style: node.color ? `stroke:${node.color}` : ""
  });
  const label = createSvg("text", { class: "node-label", x: 0, y: 0 });
  label.textContent = trimName(node.displayName ?? node.name, 18);

  group.addEventListener("click", event => {
    event.stopPropagation();
    state.selectedName = node.name;
    render();
  });

  group.addEventListener("pointerdown", event => {
    event.stopPropagation();
    svg.setPointerCapture(event.pointerId);
    state.dragging = {
      name: node.name,
      x: event.clientX,
      y: event.clientY
    };
  });

  group.append(title, circle, label);
  layer.append(group);
}

function renderInspector(graph) {
  const selected = graph.nodes.find(node => node.name === state.selectedName);
  selectedName.textContent = selected?.displayName ?? "-";
  selectedName.title = selected?.globalId ?? "";
  selectedRank.textContent = selected?.viewRank === undefined ? "rank: -" : `rank: ${formatRank(selected.viewRank)}`;
  selectedRank.title = selected?.viewRankReason ?? "";
  updateEditorState();
  renderAttributeEditor(selected?.attributes ?? {});
  neighborList.replaceChildren();

  if (!selected) {
    return;
  }

  const neighbors = graph.edges
    .filter(edge => edge.sourceGlobalId === selected.name || edge.targetGlobalId === selected.name)
    .map(edge => edge.sourceGlobalId === selected.name ? edge.targetGlobalId : edge.sourceGlobalId)
    .sort((a, b) => displayName(a).localeCompare(displayName(b), "ru"));

  neighbors.forEach(name => {
    const edge = graph.edges.find(candidate =>
      (candidate.sourceGlobalId === selected.name && candidate.targetGlobalId === name) ||
      (candidate.sourceGlobalId === name && candidate.targetGlobalId === selected.name));
    const row = document.createElement("button");
    row.type = "button";
    row.className = `neighbor-row${state.loaded.has(name) ? " loaded" : ""}`;
    const dot = document.createElement("span");
    dot.className = "neighbor-dot";
    const text = document.createElement("span");
    text.textContent = edge ? edgeEndpointDisplayName(edge, name) : displayName(name);
    row.title = name;
    row.append(dot, text);
    row.addEventListener("click", () => {
      if (edge) {
        handleEndpointClick(edge, selected.name);
      }
    });
    neighborList.append(row);
  });
}

function renderAttributeEditor(attributes) {
  const focused = document.activeElement;
  if (focused?.closest("#attribute-editor")) {
    return;
  }

  attributeEditor.replaceChildren();
  const entries = Object.entries(attributes);
  if (entries.length === 0) {
    addAttributeRow("", "");
    return;
  }

  entries.forEach(([key, value]) => addAttributeRow(key, value));
}

function addAttributeRow(key, value) {
  const row = document.createElement("div");
  row.className = "attribute-row";
  const keyInput = document.createElement("input");
  keyInput.className = "attribute-key";
  keyInput.placeholder = "Ключ";
  keyInput.value = key;
  const valueInput = document.createElement("input");
  valueInput.className = "attribute-value";
  valueInput.placeholder = "Значение";
  valueInput.value = value;
  const removeButton = document.createElement("button");
  removeButton.type = "button";
  removeButton.className = "icon-button";
  removeButton.textContent = "×";
  removeButton.addEventListener("click", () => row.remove());
  row.append(keyInput, valueInput, removeButton);
  attributeEditor.append(row);
}

function readAttributeEditor() {
  const attributes = {};
  attributeEditor.querySelectorAll(".attribute-row").forEach(row => {
    const key = row.querySelector(".attribute-key").value.trim();
    const value = row.querySelector(".attribute-value").value;
    if (key) {
      attributes[key] = value;
    }
  });
  return attributes;
}

function renderSearchResults(matches) {
  searchResults.replaceChildren();
  matches.forEach(appendSearchResult);
}

function appendSearchResult(match) {
  const node = normalizeNodeResponse(match.node);
  const button = document.createElement("button");
  button.type = "button";
  button.className = "result-row";
  button.innerHTML = `<strong></strong><span></span>`;
  button.querySelector("strong").textContent = node.displayName;
  const bindings = Object.entries(match.bindings ?? {})
    .map(([variable, binding]) => {
      const normalized = normalizeNodeResponse(binding);
      return `${variable}=${normalized.displayName}`;
    })
    .join(" · ");
  button.querySelector("span").textContent = bindings || `score ${match.score} ${match.matchedBy?.join(" ") ?? ""}`;
  button.title = node.globalId;
  button.addEventListener("click", () => {
    rootInput.value = node.globalId;
    loadRoot(node.globalId);
    setActiveTab("node");
  });
  searchResults.append(button);
}

function renderSubgraphResults(response) {
  subgraphResults.replaceChildren();
  const nodes = (response.nodes ?? []).map(normalizeNodeResponse);
  const edges = response.edges ?? [];
  const summary = document.createElement("div");
  summary.className = "result-summary";
  summary.textContent = `${nodes.length} узлов, ${edges.length} ребер`;
  subgraphResults.append(summary);
  nodes.forEach(node => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "result-row";
    button.textContent = node.displayName;
    button.title = node.globalId;
    button.addEventListener("click", () => {
      state.selectedName = node.name;
      setActiveTab("node");
      render();
    });
    subgraphResults.append(button);
  });
}

function renderTypeControls() {
  renderTypeSelect(createNodeType, state.schema.nodeTypes, "Без типа");
  renderTypeSelect(assignNodeType, state.schema.nodeTypes, "Не менять тип");
  renderTypeSelect(connectEdgeType, state.schema.edgeTypes, "Физическая связь");
  renderTypeList(nodeTypeList, "Типы узлов", state.schema.nodeTypes, "node");
  renderTypeList(edgeTypeList, "Типы связей", state.schema.edgeTypes, "edge");
  renderRelationList();
  renderProjectionSummary();
}

function renderTypeSelect(select, types, emptyLabel = "Выберите тип") {
  const current = select.value;
  select.replaceChildren();
  const empty = document.createElement("option");
  empty.value = "";
  empty.textContent = types.size === 0 ? "Типы не загружены" : emptyLabel;
  select.append(empty);
  [...types.values()]
    .sort((a, b) => a.label.localeCompare(b.label, "ru"))
    .forEach(type => {
      const option = document.createElement("option");
      option.value = type.globalId;
      option.textContent = type.label;
      select.append(option);
    });
  if (types.has(current)) {
    select.value = current;
  }
}

function renderTypeList(container, title, types, element) {
  container.replaceChildren();
  const summary = document.createElement("div");
  summary.className = "result-summary";
  summary.textContent = `${title}: ${types.size}`;
  container.append(summary);
  [...types.values()]
    .sort((a, b) => a.label.localeCompare(b.label, "ru"))
    .forEach(type => {
      const row = document.createElement("div");
      row.className = "basis-rule";
      row.title = type.globalId;

      const header = document.createElement("div");
      header.className = "type-row";
      const swatch = document.createElement("span");
      swatch.className = "type-swatch";
      swatch.style.background = type.color || "#9daab2";
      const open = document.createElement("button");
      open.type = "button";
      open.className = "result-row type-open-button";
      open.textContent = type.directed ? `${type.label} ->` : type.label;
      open.title = type.globalId;
      open.addEventListener("click", async () => {
        await ensureNodeLoaded(type.globalId);
        state.selectedName = type.globalId;
        render();
        setActiveTab("node");
      });
      const save = document.createElement("button");
      save.type = "button";
      save.className = "compact-button";
      save.textContent = "Сохранить";
      header.append(swatch, open, save);

      const rules = document.createElement("div");
      rules.className = "basis-rule-grid";
      const visible = createCheckboxRule("Показывать", type.visible);
      const color = createTextRule("Цвет", type.color || "", "#0f766e");
      const rank = createTextRule("Ранг", formatRankInput(type.rank), element === "node" ? "50" : "30");
      rules.append(visible.label, color.label, rank.label);

      const extraControls = {};
      if (element === "node") {
        extraControls.info = createTextRule("Инфо атрибут", type.infoAttribute || "", "например: status");
        rules.append(extraControls.info.label);
      } else {
        extraControls.directed = createCheckboxRule("Стрелка", type.directed);
        extraControls.labelVisible = createCheckboxRule("Подпись", type.labelVisible);
        rules.append(extraControls.directed.label, extraControls.labelVisible.label);
      }

      save.addEventListener("click", () => saveTypeProjectionRules(type.globalId, element, {
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

function createCheckboxRule(text, checked) {
  const label = document.createElement("label");
  label.className = "check-line basis-rule-check";
  const input = document.createElement("input");
  input.type = "checkbox";
  input.checked = checked;
  const span = document.createElement("span");
  span.textContent = text;
  label.append(input, span);
  return { label, input };
}

function createTextRule(text, value, placeholder) {
  const label = document.createElement("label");
  const span = document.createElement("span");
  span.textContent = text;
  const input = document.createElement("input");
  input.type = "text";
  input.value = value;
  input.placeholder = placeholder;
  label.append(span, input);
  return { label, input };
}

async function saveTypeProjectionRules(globalId, element, rules) {
  setBusy(true);
  try {
    const loaded = await getLoadedNode(globalId);
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
      attributes[projectionRankAttribute] = String(readRank(rules.rank, element === "edge" ? 30 : 50));
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

    await updateGraphNodeAttributes(globalId, attributes);
    loaded.attributes = attributes;
    state.loaded.set(globalId, loaded);
    if (element === "node") {
      state.schema.nodeTypes.set(globalId, toGraphType(loaded, "node"));
    } else {
      state.schema.edgeTypes.set(globalId, toGraphType(loaded, "edge"));
    }
    render();
    renderTypeControls();
    setStatus(`Правила сохранены: ${displayName(globalId)}`);
  } catch (error) {
    setStatus(error.message);
  } finally {
    setBusy(false);
  }
}

function renderRelationList() {
  typedEdgeList.replaceChildren();
  const graph = buildPhysicalGraph();
  const relations = discoverRelationInstances(graph);
  const summary = document.createElement("div");
  summary.className = "result-summary";
  summary.textContent = `Инстансы связей в загруженном графе: ${relations.length}`;
  typedEdgeList.append(summary);
  relations
    .sort((a, b) => a.relationGlobalId.localeCompare(b.relationGlobalId, "ru"))
    .forEach(relation => {
      const row = document.createElement("div");
      row.className = "basis-relation-row";
      const open = document.createElement("button");
      open.type = "button";
      open.className = "result-row type-open-button";
      open.textContent = `${relation.type?.label ?? "связь"}: ${displayName(relation.sourceGlobalId)} -> ${displayName(relation.targetGlobalId)}`;
      open.title = relation.relationGlobalId;
      open.addEventListener("click", () => {
        state.selectedName = relation.relationGlobalId;
        render();
        setActiveTab("node");
      });
      row.append(open);
      typedEdgeList.append(row);
    });
}

function renderProjectionSummary() {
  const physical = buildPhysicalGraph();
  const relations = discoverRelationInstances(physical);
  const graph = buildGraph();
  const rankedNodes = graph.nodes.filter(node => Number.isFinite(node.viewRank));
  const topRank = rankedNodes.length === 0
    ? ""
    : ` Топ rank: ${formatRank(Math.max(...rankedNodes.map(node => node.viewRank)))}.`;
  const basisLabel = state.schema.projectionBasis === "empty" ? "пустой базис" : "типовой базис";
  projectionSummary.textContent = `Проекция: ${basisLabel}. Загружено: ${physical.nodes.length} узлов, ${physical.edges.length} исходных связей, ${relations.length} типизированных связей.${topRank}`;
}

function toGraphType(node, fallbackElement = "node") {
  const fallbackRank = fallbackElement === "edge" ? 30 : 50;
  return {
    globalId: node.globalId,
    localId: node.localId,
    label: node.attributes?.label || node.localId,
    color: normalizeColor(node.attributes?.[projectionColorAttribute] || node.attributes?.color),
    element: node.attributes?.[graphElementAttribute] || fallbackElement,
    visible: String(node.attributes?.[projectionVisibleAttribute] ?? "true").toLowerCase() !== "false",
    infoAttribute: node.attributes?.[projectionInfoAttribute] || "",
    labelVisible: String(node.attributes?.[projectionLabelVisibleAttribute] ?? "true").toLowerCase() !== "false",
    directed: String(node.attributes?.[projectionDirectedAttribute] ?? node.attributes?.directed ?? "").toLowerCase() === "true",
    rank: readRank(node.attributes?.[projectionRankAttribute] ?? node.attributes?.rank, fallbackRank),
    attributes: node.attributes ?? {}
  };
}

function readRank(value, fallback = 0) {
  const parsed = Number.parseFloat(value);
  return Number.isFinite(parsed) ? Math.max(0, parsed) : fallback;
}

function roundRank(value) {
  return Math.round(value * 10) / 10;
}

function formatRank(value) {
  return roundRank(value).toFixed(1);
}

function formatRankInput(value) {
  return Number.isFinite(value) ? String(roundRank(value)) : "";
}

function rankToRadius(rank) {
  return Math.round(Math.max(28, Math.min(48, nodeRadius + (rank - 55) * 0.14)));
}

function normalizeColor(value) {
  if (!value || !/^#[0-9a-f]{6}$/i.test(value.trim())) {
    return "";
  }

  return value.trim();
}

async function ensureNodeLoaded(globalId) {
  if (state.loaded.has(globalId)) {
    return;
  }

  const node = normalizeNodeResponse(await apiJson(`/api/graph/nodes?${toGlobalIdQuery(globalId)}`));
  storeNodeExpansion(node, null, { select: false });
}

async function getLoadedNode(globalId) {
  await ensureNodeLoaded(globalId);
  return state.loaded.get(globalId);
}

async function updateGraphNodeAttributes(globalId, attributes) {
  await apiJson(`/api/graph/nodes?${toGlobalIdQuery(globalId)}`, {
    method: "PUT",
    body: JSON.stringify({ attributes }),
    expectJson: false
  });
}

function getBasis() {
  return state.schema.basis;
}

function readBasisInputs() {
  state.schema.basis = {
    nodeTypeRoot: nodeTypeRootInput.value.trim() || defaultBasis.nodeTypeRoot,
    edgeTypeRoot: edgeTypeRootInput.value.trim() || defaultBasis.edgeTypeRoot,
    relationRoot: relationRootInput.value.trim() || defaultBasis.relationRoot
  };
}

function syncBasisInputs() {
  nodeTypeRootInput.value = state.schema.basis.nodeTypeRoot;
  edgeTypeRootInput.value = state.schema.basis.edgeTypeRoot;
  relationRootInput.value = state.schema.basis.relationRoot;
  projectionBasis.value = state.schema.projectionBasis;
}

function createRelationLocalId(typeGlobalId) {
  const typeName = getLocalId(typeGlobalId).replace(/[^A-Za-z0-9._ -]/g, "-");
  return `${typeName}-${Date.now().toString(36)}`;
}

function seedPosition(name, fromName, index) {
  if (state.positions.has(name)) {
    return;
  }

  if (!fromName || !state.positions.has(fromName)) {
    state.positions.set(name, { x: 0, y: 0 });
    state.velocities.set(name, { x: 0, y: 0 });
    return;
  }

  const source = state.positions.get(fromName);
  const angle = index * 2.399963 + [...name].reduce((sum, char) => sum + char.charCodeAt(0), 0) * 0.017;
  const distance = 92;
  state.positions.set(name, {
    x: source.x + Math.cos(angle) * distance,
    y: source.y + Math.sin(angle) * distance
  });
  state.velocities.set(name, { x: 0, y: 0 });
}

function seedSubgraphPosition(name, index, count) {
  const radius = Math.max(120, Math.min(320, count * 32));
  const angle = count <= 1 ? 0 : (Math.PI * 2 * index) / count;
  state.positions.set(name, {
    x: Math.cos(angle) * radius,
    y: Math.sin(angle) * radius
  });
  state.velocities.set(name, { x: 0, y: 0 });
}

function runSimulation(frames) {
  if (state.simulationHandle) {
    cancelAnimationFrame(state.simulationHandle);
  }

  let remaining = frames;
  const tick = () => {
    simulateStep();
    render();
    remaining -= 1;
    if (remaining > 0) {
      state.simulationHandle = requestAnimationFrame(tick);
    }
  };

  state.simulationHandle = requestAnimationFrame(tick);
}

function simulateStep() {
  const graph = buildGraph();
  const nodes = graph.nodes;
  const forces = new Map(nodes.map(node => [node.name, { x: 0, y: 0 }]));

  for (let i = 0; i < nodes.length; i += 1) {
    for (let j = i + 1; j < nodes.length; j += 1) {
      const a = nodes[i];
      const b = nodes[j];
      const pa = state.positions.get(a.name);
      const pb = state.positions.get(b.name);
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
    .filter(edge => state.loaded.has(edge.sourceGlobalId) && state.loaded.has(edge.targetGlobalId))
    .forEach(edge => {
      const source = state.positions.get(edge.sourceGlobalId);
      const target = state.positions.get(edge.targetGlobalId);
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
    const position = state.positions.get(node.name);
    const velocity = state.velocities.get(node.name) ?? { x: 0, y: 0 };
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
    state.velocities.set(node.name, velocity);
  });
}

function fitView() {
  const graph = buildGraph();
  if (graph.nodes.length === 0) {
    state.view = { x: 0, y: 0, scale: 1 };
    applyView();
    return;
  }

  const rect = svg.getBoundingClientRect();
  const points = graph.nodes
    .map(node => state.positions.get(node.name))
    .filter(Boolean);
  const minX = Math.min(...points.map(point => point.x)) - 120;
  const maxX = Math.max(...points.map(point => point.x)) + 120;
  const minY = Math.min(...points.map(point => point.y)) - 120;
  const maxY = Math.max(...points.map(point => point.y)) + 120;
  const width = Math.max(1, maxX - minX);
  const height = Math.max(1, maxY - minY);
  const scale = Math.min(1.8, Math.max(0.32, Math.min(rect.width / width, rect.height / height)));

  state.view.scale = scale;
  state.view.x = rect.width / 2 - ((minX + maxX) / 2) * scale;
  state.view.y = rect.height / 2 - ((minY + maxY) / 2) * scale;
  applyView();
}

async function apiJson(url, options = {}) {
  const response = await fetch(url, {
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

function formatNeighborError(error, neighborLocalId) {
  if (error.status === 404) {
    return `Сосед "${neighborLocalId}" не найден`;
  }

  if (error.status === 409) {
    return `Сосед "${neighborLocalId}" неоднозначен`;
  }

  return error.message;
}

function setActiveTab(name) {
  document.querySelectorAll(".tab-button").forEach(button => {
    button.classList.toggle("active", button.dataset.tab === name);
  });
  document.querySelectorAll(".tab-panel").forEach(panel => {
    panel.classList.toggle("active", panel.id === `tab-${name}`);
  });
}

function parseCsv(value) {
  return value
    .split(",")
    .map(item => item.trim())
    .filter(Boolean);
}

function readNumber(selector, fallback) {
  const value = Number.parseInt(document.querySelector(selector).value, 10);
  return Number.isFinite(value) ? value : fallback;
}

function pointOnCircle(anchor, target, radius) {
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

function applyView() {
  viewport.setAttribute("transform", `translate(${state.view.x} ${state.view.y}) scale(${state.view.scale})`);
}

function screenToGraph(x, y) {
  return {
    x: (x - state.view.x) / state.view.scale,
    y: (y - state.view.y) / state.view.scale
  };
}

function createSvg(name, attrs) {
  const element = document.createElementNS(svgNs, name);
  Object.entries(attrs).forEach(([key, value]) => element.setAttribute(key, value));
  return element;
}

function edgeKey(a, b) {
  return a.localeCompare(b, "ru") < 0 ? `${a}\u0000${b}` : `${b}\u0000${a}`;
}

function getOtherEndpoint(edge, nodeName) {
  return edge.sourceGlobalId === nodeName ? edge.targetGlobalId : edge.sourceGlobalId;
}

function trimName(name, limit) {
  return name.length > limit ? `${name.slice(0, limit - 1)}…` : name;
}

function setStatus(message) {
  statusOutput.value = message;
  statusOutput.textContent = message;
}

function setBusy(value) {
  state.busy = value;
  document.querySelectorAll("button").forEach(button => {
    if (!button.classList.contains("tab-button")) {
      button.disabled = value;
    }
  });
  if (!value) {
    updateEditorState();
  }
}

function updateEditorState() {
  const hasSelection = Boolean(state.selectedName && state.loaded.has(state.selectedName));
  saveNodeButton.disabled = state.busy || !hasSelection;
  deleteNodeButton.disabled = state.busy || !hasSelection;
  connectForm.querySelector("button").disabled = state.busy || !hasSelection;
  if (!state.searchAbort) {
    setSearchStreaming(false);
  }
}

render();
