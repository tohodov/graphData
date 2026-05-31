const svg = document.querySelector("#graph");
const viewport = document.querySelector("#viewport");
const rootForm = document.querySelector("#root-form");
const rootInput = document.querySelector("#root-input");
const fitButton = document.querySelector("#fit-button");
const resetButton = document.querySelector("#reset-button");
const statusOutput = document.querySelector("#status");
const emptyState = document.querySelector("#empty-state");
const selectedName = document.querySelector("#selected-name");
const attributeEditor = document.querySelector("#attribute-editor");
const addAttributeButton = document.querySelector("#add-attribute-button");
const saveNodeButton = document.querySelector("#save-node-button");
const deleteNodeButton = document.querySelector("#delete-node-button");
const createNodeForm = document.querySelector("#create-node-form");
const createNodeName = document.querySelector("#create-node-name");
const connectForm = document.querySelector("#connect-form");
const connectTargetName = document.querySelector("#connect-target-name");
const neighborList = document.querySelector("#neighbor-list");
const searchForm = document.querySelector("#search-form");
const searchQueryJson = document.querySelector("#search-query-json");
const searchSubmitButton = document.querySelector("#search-submit-button");
const searchStopButton = document.querySelector("#search-stop-button");
const searchResults = document.querySelector("#search-results");
const subgraphForm = document.querySelector("#subgraph-form");
const subgraphResults = document.querySelector("#subgraph-results");

const svgNs = "http://www.w3.org/2000/svg";
const nodeRadius = 34;
const endpointOffset = nodeRadius + 9;

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
  busy: false
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

  await createNode(name);
});

connectForm.addEventListener("submit", async event => {
  event.preventDefault();
  const sourceGlobalId = state.selectedName;
  const targetGlobalId = connectTargetName.value.trim();
  if (!sourceGlobalId || !targetGlobalId) {
    setStatus("Выберите узел и укажите цель связи");
    return;
  }

  await connectNodes(sourceGlobalId, targetGlobalId);
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

async function createNode(name) {
  setBusy(true);
  try {
    const created = normalizeNodeResponse(await apiJson("/api/graph/nodes", {
      method: "POST",
      body: JSON.stringify({ localId: name })
    }));
    createNodeName.value = "";
    state.rootName = state.rootName ?? created.name;
    state.selectedName = created.name;
    seedPosition(created.name, state.rootName === created.name ? null : state.rootName, state.loaded.size);
    state.loaded.set(created.name, created);
    render();
    setActiveTab("node");
    setStatus(`Создан узел "${created.displayName}"`);
  } catch (error) {
    setStatus(error.message);
  } finally {
    setBusy(false);
  }
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
    setStatus(`Удален узел "${displayName(nodeName)}"`);
  } catch (error) {
    setStatus(error.message);
  } finally {
    setBusy(false);
  }
}

async function connectNodes(sourceGlobalId, targetGlobalId) {
  setBusy(true);
  try {
    await apiJson("/api/graph/connections", {
      method: "POST",
      body: JSON.stringify({
        sourceGlobalId: parseGlobalId(sourceGlobalId),
        targetGlobalId: parseGlobalId(targetGlobalId)
      }),
      expectJson: false
    });
    connectTargetName.value = "";
    await loadNode(sourceGlobalId, null, { select: true });
    setStatus(`Связаны "${displayName(sourceGlobalId)}" и "${displayName(targetGlobalId)}"`);
  } catch (error) {
    setStatus(error.message);
  } finally {
    setBusy(false);
  }
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
  runSimulation(40);
  fitView();
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

function render() {
  const graph = buildGraph();
  viewport.replaceChildren();
  emptyState.classList.toggle("hidden", graph.nodes.length > 0);

  const edgeLayer = createSvg("g", { class: "edges" });
  const nodeLayer = createSvg("g", { class: "nodes" });
  const buttonLayer = createSvg("g", { class: "edge-buttons" });
  viewport.append(edgeLayer, nodeLayer, buttonLayer);

  graph.edges.forEach(edge => renderEdge(edgeLayer, buttonLayer, edge));
  graph.nodes.forEach(node => renderNode(nodeLayer, node));
  renderInspector(graph);
  applyView();
}

function renderEdge(edgeLayer, buttonLayer, edge) {
  const sourceLoaded = state.loaded.has(edge.sourceGlobalId);
  const targetLoaded = state.loaded.has(edge.targetGlobalId);
  const source = state.positions.get(edge.sourceGlobalId);
  const target = state.positions.get(edge.targetGlobalId);

  if (!source || !target || (!sourceLoaded && !targetLoaded)) {
    return;
  }

  if (sourceLoaded && targetLoaded) {
    const sourceButton = pointOnCircle(source, target, endpointOffset);
    const targetButton = pointOnCircle(target, source, endpointOffset);
    const line = createSvg("line", {
      class: "edge-line",
      x1: sourceButton.x,
      y1: sourceButton.y,
      x2: targetButton.x,
      y2: targetButton.y
    });

    edgeLayer.append(line);
    renderEndpointButton(buttonLayer, sourceButton, edge, edge.sourceGlobalId, false);
    renderEndpointButton(buttonLayer, targetButton, edge, edge.targetGlobalId, false);
    return;
  }

  const anchorName = sourceLoaded ? edge.sourceGlobalId : edge.targetGlobalId;
  const hiddenName = sourceLoaded ? edge.targetGlobalId : edge.sourceGlobalId;
  const anchor = sourceLoaded ? source : target;
  const hidden = sourceLoaded ? target : source;
  const buttonPoint = pointOnCircle(anchor, hidden, endpointOffset);
  renderEndpointButton(buttonLayer, buttonPoint, edge, anchorName, true, hiddenName);
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
  title.textContent = collapsed ? `Развернуть ${otherDisplayName}` : `Выбрать ${otherDisplayName}`;

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
  title.textContent = node.globalId ?? node.name;
  const circle = createSvg("circle", { class: "node-shell", r: nodeRadius, cx: 0, cy: 0 });
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
