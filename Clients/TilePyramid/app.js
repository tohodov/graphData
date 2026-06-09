const canvas = document.querySelector("#map-canvas");
const ctx = canvas.getContext("2d");
const loadForm = document.querySelector("#load-form");
const rootsInput = document.querySelector("#roots-input");
const depthInput = document.querySelector("#depth-input");
const loadButton = document.querySelector("#load-button");
const fitButton = document.querySelector("#fit-button");
const resetButton = document.querySelector("#reset-button");
const statusOutput = document.querySelector("#status");
const loadSelectedButton = document.querySelector("#load-selected-button");
const capacityInput = document.querySelector("#capacity-input");
const capacityOutput = document.querySelector("#capacity-output");
const tileDebugInput = document.querySelector("#tile-debug-input");

const statLevel = document.querySelector("#stat-level");
const statTiles = document.querySelector("#stat-tiles");
const statDrawn = document.querySelector("#stat-drawn");
const statFrame = document.querySelector("#stat-frame");
const selectedTitle = document.querySelector("#selected-title");
const selectedGlobal = document.querySelector("#selected-global");
const selectedRank = document.querySelector("#selected-rank");
const selectedDegree = document.querySelector("#selected-degree");
const attributeList = document.querySelector("#attribute-list");
const pipelineGraph = document.querySelector("#pipeline-graph");
const pipelineBuild = document.querySelector("#pipeline-build");
const pipelineMode = document.querySelector("#pipeline-mode");

const maxLevel = 7;
const tileScreenSize = 512;
const maxDevicePixelRatio = 2;
const edgeColor = "rgba(164, 177, 155, 0.22)";
const selectedColor = "#ebd36a";
const labelFill = "rgba(18, 20, 18, 0.84)";
const labelStroke = "rgba(143, 210, 110, 0.28)";
const palette = [
  "#8fd26e",
  "#d8b968",
  "#7fb4e8",
  "#db806d",
  "#b596df",
  "#80c6bd",
  "#d690b6",
  "#b9cb6d"
];

const state = {
  graph: null,
  pyramid: null,
  selectedIndex: -1,
  view: { x: 0, y: 0, scale: 1 },
  fitScale: 1,
  pointer: null,
  renderPending: false,
  renderDirty: false,
  abort: null,
  capacity: Number.parseInt(capacityInput.value, 10),
  showTiles: false
};

main();

function main() {
  attachEvents();
  resetGraph();

  const initialGlobalId = new URLSearchParams(window.location.search).get("globalId");
  if (initialGlobalId) {
    rootsInput.value = initialGlobalId;
    loadRoots([initialGlobalId], readDepth());
  }
}

function attachEvents() {
  loadForm.addEventListener("submit", event => {
    event.preventDefault();
    loadRoots(parseRoots(rootsInput.value), readDepth());
  });

  fitButton.addEventListener("click", fitView);
  resetButton.addEventListener("click", resetGraph);
  loadSelectedButton.addEventListener("click", () => {
    const node = getSelectedNode();
    if (node) {
      rootsInput.value = node.globalId;
      loadRoots([node.globalId], readDepth());
    }
  });

  capacityInput.addEventListener("input", () => {
    state.capacity = Number.parseInt(capacityInput.value, 10);
    capacityOutput.textContent = String(state.capacity);
  });

  capacityInput.addEventListener("change", () => {
    if (!state.graph) {
      return;
    }

    rebuildPyramid();
    requestRender();
  });

  tileDebugInput.addEventListener("change", () => {
    state.showTiles = tileDebugInput.checked;
    requestRender();
  });

  canvas.addEventListener("pointerdown", event => {
    canvas.setPointerCapture(event.pointerId);
    canvas.classList.add("dragging");
    state.pointer = {
      x: event.clientX,
      y: event.clientY,
      startX: event.clientX,
      startY: event.clientY
    };
  });

  canvas.addEventListener("pointermove", event => {
    if (!state.pointer) {
      return;
    }

    const dx = event.clientX - state.pointer.x;
    const dy = event.clientY - state.pointer.y;
    state.pointer.x = event.clientX;
    state.pointer.y = event.clientY;
    state.view.x += dx;
    state.view.y += dy;
    requestRender();
  });

  canvas.addEventListener("pointerup", event => {
    canvas.releasePointerCapture(event.pointerId);
    canvas.classList.remove("dragging");
    const pointer = state.pointer;
    state.pointer = null;
    if (!pointer) {
      return;
    }

    const moved = Math.hypot(event.clientX - pointer.startX, event.clientY - pointer.startY);
    if (moved <= 4) {
      selectNearest(event.clientX, event.clientY);
    }
  });

  canvas.addEventListener("wheel", event => {
    event.preventDefault();
    const rect = canvas.getBoundingClientRect();
    const x = event.clientX - rect.left;
    const y = event.clientY - rect.top;
    const before = screenToWorld(x, y);
    state.view.scale = clamp(state.view.scale * Math.exp(-event.deltaY * 0.0012), 0.01, 64);
    state.view.x = x - before.x * state.view.scale;
    state.view.y = y - before.y * state.view.scale;
    requestRender();
  }, { passive: false });

  window.addEventListener("resize", () => {
    resizeCanvas();
    requestRender();
  });
}

async function loadRoots(roots, depth) {
  if (roots.length === 0) {
    setStatus("Enter at least one root GlobalId");
    return;
  }

  state.abort?.abort();
  state.abort = new AbortController();
  setBusy(true);
  pipelineGraph.textContent = "loading";
  pipelineBuild.textContent = "waiting";
  setStatus("Loading subgraph...");

  try {
    const started = performance.now();
    const response = await apiJson("/api/graph/subgraph", {
      method: "POST",
      signal: state.abort.signal,
      body: JSON.stringify({
        globalIds: roots.map(parseGlobalId),
        maxDepth: depth
      })
    });
    const graph = buildGraph(response, roots);
    state.graph = graph;
    state.selectedIndex = graph.nodes.length > 0 ? 0 : -1;
    pipelineGraph.textContent = `${formatCount(graph.nodes.length)} nodes, ${formatCount(graph.edges.length)} edges`;
    rebuildPyramid();
    fitView();
    renderSelection();
    draw();
    setStatus(`Loaded in ${(performance.now() - started).toFixed(1)} ms`);
  } catch (error) {
    if (error.name !== "AbortError") {
      setStatus(error.message);
      pipelineGraph.textContent = "error";
    }
  } finally {
    state.abort = null;
    setBusy(false);
  }
}

function rebuildPyramid() {
  if (!state.graph) {
    state.pyramid = null;
    return;
  }

  const started = performance.now();
  state.pyramid = buildTilePyramid(state.graph, state.capacity);
  pipelineBuild.textContent = `${state.pyramid.levels.length} levels, ${(performance.now() - started).toFixed(1)} ms`;
}

function buildGraph(response, roots) {
  const nodes = (response.nodes ?? []).map(normalizeNode);
  const idByGlobal = new Map();
  nodes.forEach((node, index) => idByGlobal.set(node.globalId, index));

  const edgeMap = new Map();
  const addEdge = edge => {
    const normalized = normalizeEdge(edge);
    const source = idByGlobal.get(normalized.sourceGlobalId);
    const target = idByGlobal.get(normalized.targetGlobalId);
    if (source === undefined || target === undefined || source === target) {
      return;
    }

    const key = source < target ? `${source}\0${target}` : `${target}\0${source}`;
    if (!edgeMap.has(key)) {
      edgeMap.set(key, { source, target });
    }
  };

  (response.edges ?? []).forEach(addEdge);
  nodes.forEach(node => (node.edges ?? []).forEach(addEdge));
  const edges = [...edgeMap.values()];
  const degree = new Uint32Array(nodes.length);
  edges.forEach(edge => {
    degree[edge.source] += 1;
    degree[edge.target] += 1;
  });

  const positions = new Float32Array(nodes.length * 2);
  seedPositions(nodes, positions);
  runEdgeRelaxation(positions, edges, nodes.length);

  const rootSet = new Set(roots);
  let maxDegree = 1;
  degree.forEach(value => { maxDegree = Math.max(maxDegree, value); });

  nodes.forEach((node, index) => {
    node.degree = degree[index];
    node.rank = computeRank(node, index, maxDegree, rootSet);
    node.color = colorForNode(node);
    node.x = positions[index * 2];
    node.y = positions[index * 2 + 1];
  });

  return {
    nodes,
    edges,
    degree,
    positions,
    bounds: computeBounds(positions)
  };
}

function buildTilePyramid(graph, capacity) {
  const levels = [];
  const bounds = graph.bounds;
  const width = Math.max(1, bounds.maxX - bounds.minX);
  const height = Math.max(1, bounds.maxY - bounds.minY);

  for (let zoom = 0; zoom <= maxLevel; zoom += 1) {
    const gridSize = 2 ** zoom;
    const tileWidth = width / gridSize;
    const tileHeight = height / gridSize;
    const tileIndex = new Map();
    const tileNodeBuckets = new Map();
    const tilePointLimit = capacity + zoom * capacity;
    const tileLabelLimit = Math.max(16, Math.floor(capacity * (0.35 + zoom * 0.09)));
    const levelScale = 0.38 * (2 ** zoom);

    graph.nodes.forEach((node, index) => {
      const tile = locateTile(node.x, node.y, bounds, tileWidth, tileHeight, gridSize);
      const key = tileKey(tile.x, tile.y);
      if (!tileNodeBuckets.has(key)) {
        tileNodeBuckets.set(key, []);
      }
      tileNodeBuckets.get(key).push(index);
    });

    for (const [key, candidates] of tileNodeBuckets.entries()) {
      const [tileX, tileY] = parseTileKey(key);
      const tile = ensureTile(tileIndex, key, tileX, tileY, bounds, tileWidth, tileHeight);
      candidates.sort((left, right) => graph.nodes[right].rank - graph.nodes[left].rank);

      tile.points = candidates.slice(0, tilePointLimit);
      tile.labels = selectLabels(graph, candidates, tileLabelLimit, levelScale);
    }

    const visibleNodeSet = new Set();
    for (const tile of tileIndex.values()) {
      tile.points.forEach(index => visibleNodeSet.add(index));
    }

    graph.edges.forEach((edge, edgeIndex) => {
      if (!visibleNodeSet.has(edge.source) || !visibleNodeSet.has(edge.target)) {
        return;
      }

      const source = graph.nodes[edge.source];
      const target = graph.nodes[edge.target];
      const coveredTiles = tilesForSegment(source, target, bounds, tileWidth, tileHeight, gridSize);
      for (const key of coveredTiles) {
        const [tileX, tileY] = parseTileKey(key);
        const tile = ensureTile(tileIndex, key, tileX, tileY, bounds, tileWidth, tileHeight);
        if (tile.edges.length < capacity * 4) {
          tile.edges.push(edgeIndex);
        }
      }
    });

    levels.push({
      zoom,
      gridSize,
      tileWidth,
      tileHeight,
      tiles: tileIndex
    });
  }

  return { bounds, levels };
}

function selectLabels(graph, candidates, limit, levelScale) {
  const labels = [];
  const boxes = [];
  for (const index of candidates) {
    if (labels.length >= limit) {
      break;
    }

    const node = graph.nodes[index];
    const textWidth = Math.max(34, node.localId.length * 7 + 18) / levelScale;
    const textHeight = 20 / levelScale;
    const box = {
      x1: node.x + 9 / levelScale,
      y1: node.y - textHeight * 0.6,
      x2: node.x + 9 / levelScale + textWidth,
      y2: node.y + textHeight * 0.6
    };

    if (boxes.some(existing => boxesIntersect(existing, box))) {
      continue;
    }

    boxes.push(box);
    labels.push(index);
  }

  return labels;
}

function ensureTile(tileIndex, key, x, y, bounds, tileWidth, tileHeight) {
  let tile = tileIndex.get(key);
  if (tile) {
    return tile;
  }

  tile = {
    x,
    y,
    bounds: {
      minX: bounds.minX + x * tileWidth,
      minY: bounds.minY + y * tileHeight,
      maxX: bounds.minX + (x + 1) * tileWidth,
      maxY: bounds.minY + (y + 1) * tileHeight
    },
    points: [],
    labels: [],
    edges: []
  };
  tileIndex.set(key, tile);
  return tile;
}

function draw() {
  resizeCanvas();
  const started = performance.now();
  const width = canvas.clientWidth;
  const height = canvas.clientHeight;
  ctx.clearRect(0, 0, width, height);
  ctx.fillStyle = "#0d100d";
  ctx.fillRect(0, 0, width, height);

  if (!state.graph || !state.pyramid) {
    renderFrameStats(0, 0, 0, 0, performance.now() - started);
    return;
  }

  const level = chooseLevel();
  const visibleTiles = getVisibleTiles(level);
  const drawnEdges = new Set();
  const drawnNodes = new Set();
  const drawnLabels = new Set();

  ctx.lineCap = "round";
  ctx.lineJoin = "round";
  drawTileEdges(level, visibleTiles, drawnEdges);
  drawTileNodes(visibleTiles, drawnNodes);
  drawTileLabels(visibleTiles, drawnLabels);
  drawSelectedNode();

  if (state.showTiles) {
    drawTileGrid(visibleTiles);
  }

  renderFrameStats(
    level.zoom,
    visibleTiles.length,
    drawnNodes.size,
    drawnEdges.size,
    performance.now() - started);
}

function chooseLevel() {
  if (!state.pyramid) {
    return null;
  }

  const relative = Math.max(0.0001, state.view.scale / Math.max(0.0001, state.fitScale));
  const zoom = clamp(Math.floor(Math.log2(relative) + 1), 0, state.pyramid.levels.length - 1);
  return state.pyramid.levels[zoom];
}

function getVisibleTiles(level) {
  if (!level) {
    return [];
  }

  const rect = canvas.getBoundingClientRect();
  const topLeft = screenToWorld(0, 0);
  const bottomRight = screenToWorld(rect.width, rect.height);
  const minX = Math.min(topLeft.x, bottomRight.x);
  const maxX = Math.max(topLeft.x, bottomRight.x);
  const minY = Math.min(topLeft.y, bottomRight.y);
  const maxY = Math.max(topLeft.y, bottomRight.y);
  const bounds = state.pyramid.bounds;
  const tileMinX = clampIndex(Math.floor((minX - bounds.minX) / level.tileWidth), 0, level.gridSize - 1);
  const tileMaxX = clampIndex(Math.floor((maxX - bounds.minX) / level.tileWidth), 0, level.gridSize - 1);
  const tileMinY = clampIndex(Math.floor((minY - bounds.minY) / level.tileHeight), 0, level.gridSize - 1);
  const tileMaxY = clampIndex(Math.floor((maxY - bounds.minY) / level.tileHeight), 0, level.gridSize - 1);
  const tiles = [];

  for (let y = tileMinY; y <= tileMaxY; y += 1) {
    for (let x = tileMinX; x <= tileMaxX; x += 1) {
      const tile = level.tiles.get(tileKey(x, y));
      if (tile) {
        tiles.push(tile);
      }
    }
  }

  return tiles;
}

function drawTileEdges(level, tiles, drawnEdges) {
  const graph = state.graph;
  ctx.strokeStyle = edgeColor;
  ctx.lineWidth = clamp(1.2 * state.view.scale, 0.35, 1.8);
  ctx.beginPath();
  for (const tile of tiles) {
    for (const edgeIndex of tile.edges) {
      if (drawnEdges.has(edgeIndex)) {
        continue;
      }

      drawnEdges.add(edgeIndex);
      const edge = graph.edges[edgeIndex];
      const source = graph.nodes[edge.source];
      const target = graph.nodes[edge.target];
      const start = worldToScreen(source.x, source.y);
      const end = worldToScreen(target.x, target.y);
      ctx.moveTo(start.x, start.y);
      drawRoutedEdge(start, end, level.zoom);
    }
  }
  ctx.stroke();
}

function drawRoutedEdge(start, end, zoom) {
  if (zoom < 3) {
    ctx.lineTo(end.x, end.y);
    return;
  }

  const midX = (start.x + end.x) / 2;
  const midY = (start.y + end.y) / 2;
  const dx = end.x - start.x;
  const dy = end.y - start.y;
  const distance = Math.max(1, Math.hypot(dx, dy));
  const bend = clamp(distance * 0.06, 4, 18);
  ctx.quadraticCurveTo(
    midX - dy / distance * bend,
    midY + dx / distance * bend,
    end.x,
    end.y);
}

function drawTileNodes(tiles, drawnNodes) {
  const graph = state.graph;
  for (const tile of tiles) {
    for (const index of tile.points) {
      if (drawnNodes.has(index)) {
        continue;
      }

      drawnNodes.add(index);
      const node = graph.nodes[index];
      const point = worldToScreen(node.x, node.y);
      const radius = clamp(3.2 + Math.sqrt(node.degree) * 0.7, 3, 11);
      ctx.fillStyle = node.color;
      ctx.globalAlpha = 0.92;
      ctx.beginPath();
      ctx.arc(point.x, point.y, radius, 0, Math.PI * 2);
      ctx.fill();
    }
  }
  ctx.globalAlpha = 1;
}

function drawTileLabels(tiles, drawnLabels) {
  const graph = state.graph;
  ctx.font = "12px Segoe UI, system-ui, sans-serif";
  ctx.textBaseline = "middle";
  for (const tile of tiles) {
    for (const index of tile.labels) {
      if (drawnLabels.has(index)) {
        continue;
      }

      drawnLabels.add(index);
      const node = graph.nodes[index];
      const point = worldToScreen(node.x, node.y);
      drawLabel(node.localId, point.x + 9, point.y);
    }
  }
}

function drawLabel(text, x, y) {
  const width = Math.ceil(ctx.measureText(text).width + 12);
  const height = 19;
  ctx.fillStyle = labelFill;
  ctx.strokeStyle = labelStroke;
  ctx.lineWidth = 1;
  roundRect(ctx, x, y - height / 2, width, height, 5);
  ctx.fill();
  ctx.stroke();
  ctx.fillStyle = "#f0f4ed";
  ctx.fillText(text, x + 6, y + 0.5);
}

function drawSelectedNode() {
  const node = getSelectedNode();
  if (!node) {
    return;
  }

  const point = worldToScreen(node.x, node.y);
  ctx.strokeStyle = selectedColor;
  ctx.lineWidth = 2;
  ctx.beginPath();
  ctx.arc(point.x, point.y, 13, 0, Math.PI * 2);
  ctx.stroke();
  drawLabel(node.localId, point.x + 12, point.y - 16);
}

function drawTileGrid(tiles) {
  ctx.strokeStyle = "rgba(96, 112, 95, 0.32)";
  ctx.lineWidth = 1;
  for (const tile of tiles) {
    const start = worldToScreen(tile.bounds.minX, tile.bounds.minY);
    const end = worldToScreen(tile.bounds.maxX, tile.bounds.maxY);
    ctx.strokeRect(start.x, start.y, end.x - start.x, end.y - start.y);
  }
}

function renderFrameStats(level, tiles, nodes, edges, frameMs) {
  statLevel.textContent = String(level ?? 0);
  statTiles.textContent = formatCount(tiles);
  statDrawn.textContent = `${formatCount(nodes)} / ${formatCount(edges)}`;
  statFrame.textContent = `${frameMs.toFixed(2)} ms`;
}

function requestRender() {
  if (state.renderPending) {
    state.renderDirty = true;
    return;
  }

  state.renderPending = true;
  requestAnimationFrame(() => {
    state.renderPending = false;
    draw();
    if (state.renderDirty) {
      state.renderDirty = false;
      requestRender();
    }
  });
}

function fitView() {
  resizeCanvas();
  const graph = state.graph;
  const rect = canvas.getBoundingClientRect();
  if (!graph || graph.nodes.length === 0 || rect.width <= 0 || rect.height <= 0) {
    state.view = { x: rect.width / 2, y: rect.height / 2, scale: 1 };
    state.fitScale = 1;
    requestRender();
    return;
  }

  const bounds = graph.bounds;
  const width = Math.max(1, bounds.maxX - bounds.minX);
  const height = Math.max(1, bounds.maxY - bounds.minY);
  const scale = clamp(Math.min((rect.width - 80) / width, (rect.height - 120) / height), 0.01, 20);
  state.view.scale = scale;
  state.fitScale = scale;
  state.view.x = rect.width / 2 - ((bounds.minX + bounds.maxX) / 2) * scale;
  state.view.y = rect.height / 2 - ((bounds.minY + bounds.maxY) / 2) * scale;
  requestRender();
}

function resetGraph() {
  state.abort?.abort();
  state.graph = null;
  state.pyramid = null;
  state.selectedIndex = -1;
  state.view = { x: 0, y: 0, scale: 1 };
  state.fitScale = 1;
  pipelineGraph.textContent = "empty";
  pipelineBuild.textContent = "idle";
  pipelineMode.textContent = "semantic zoom";
  setStatus("");
  renderSelection();
  resizeCanvas();
  requestRender();
}

function selectNearest(clientX, clientY) {
  const graph = state.graph;
  if (!graph || graph.nodes.length === 0) {
    return;
  }

  const rect = canvas.getBoundingClientRect();
  const x = clientX - rect.left;
  const y = clientY - rect.top;
  let bestIndex = -1;
  let bestDistance = Infinity;
  const level = chooseLevel();
  const visibleTiles = getVisibleTiles(level);
  const candidates = new Set();
  visibleTiles.forEach(tile => tile.points.forEach(index => candidates.add(index)));

  for (const index of candidates) {
    const node = graph.nodes[index];
    const point = worldToScreen(node.x, node.y);
    const distance = Math.hypot(point.x - x, point.y - y);
    if (distance < bestDistance && distance <= 16) {
      bestDistance = distance;
      bestIndex = index;
    }
  }

  if (bestIndex !== -1) {
    state.selectedIndex = bestIndex;
    renderSelection();
    requestRender();
  }
}

function renderSelection() {
  const node = getSelectedNode();
  loadSelectedButton.disabled = !node;
  selectedTitle.textContent = node?.localId ?? "-";
  selectedGlobal.textContent = node?.globalId ?? "-";
  selectedRank.textContent = node ? node.rank.toFixed(3) : "-";
  selectedDegree.textContent = node ? String(node.degree) : "-";
  attributeList.replaceChildren();

  if (!node || Object.keys(node.attributes).length === 0) {
    const empty = document.createElement("div");
    empty.className = "empty-text";
    empty.textContent = "empty";
    attributeList.append(empty);
    return;
  }

  Object.entries(node.attributes)
    .sort(([left], [right]) => left.localeCompare(right))
    .slice(0, 80)
    .forEach(([key, value]) => {
      const row = document.createElement("div");
      row.className = "attribute-row";
      const keyElement = document.createElement("div");
      keyElement.className = "attribute-key";
      keyElement.textContent = key;
      const valueElement = document.createElement("div");
      valueElement.className = "attribute-value";
      valueElement.textContent = value;
      row.append(keyElement, valueElement);
      attributeList.append(row);
    });
}

function getSelectedNode() {
  return state.graph && state.selectedIndex >= 0
    ? state.graph.nodes[state.selectedIndex]
    : null;
}

async function apiJson(url, options = {}) {
  const response = await fetch(url, {
    method: options.method ?? "GET",
    headers: { "Content-Type": "application/json" },
    body: options.body,
    signal: options.signal
  });

  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `HTTP ${response.status}`);
  }

  return response.status === 204 ? null : response.json();
}

function normalizeNode(node) {
  const globalId = node.globalId ?? node.name ?? "";
  const segments = globalId.split("/").filter(Boolean);
  const localId = node.localId ?? segments[segments.length - 1] ?? globalId;
  return {
    globalId,
    localId,
    attributes: node.attributes ?? {},
    edges: (node.edges ?? []).map(normalizeEdge),
    degree: 0,
    rank: 0,
    color: palette[0],
    x: 0,
    y: 0
  };
}

function normalizeEdge(edge) {
  return {
    sourceGlobalId: edge.sourceGlobalId,
    targetGlobalId: edge.targetGlobalId
  };
}

function parseRoots(value) {
  return value
    .split(",")
    .map(root => root.trim())
    .filter(Boolean);
}

function parseGlobalId(value) {
  return value.split("/").filter(Boolean);
}

function readDepth() {
  const value = Number.parseInt(depthInput.value, 10);
  return Number.isFinite(value) ? clamp(value, 0, 8) : 1;
}

function setBusy(value) {
  loadButton.disabled = value;
  rootsInput.disabled = value;
  depthInput.disabled = value;
}

function setStatus(message) {
  statusOutput.textContent = message;
}

function seedPositions(nodes, positions) {
  const goldenAngle = Math.PI * (3 - Math.sqrt(5));
  for (let index = 0; index < nodes.length; index += 1) {
    const radius = Math.sqrt(index + 1) * 28;
    const angle = index * goldenAngle + (hashString(nodes[index].globalId) % 4096) * 0.0007;
    positions[index * 2] = Math.cos(angle) * radius;
    positions[index * 2 + 1] = Math.sin(angle) * radius;
  }
}

function runEdgeRelaxation(positions, edges, nodeCount) {
  if (nodeCount === 0 || edges.length === 0) {
    return;
  }

  const iterations = clamp(Math.floor(2_000_000 / Math.max(1, edges.length)), 2, 32);
  const desired = 84;
  for (let iteration = 0; iteration < iterations; iteration += 1) {
    const strength = 0.016 * (1 - iteration / (iterations + 8));
    for (const edge of edges) {
      const source = edge.source * 2;
      const target = edge.target * 2;
      const dx = positions[target] - positions[source];
      const dy = positions[target + 1] - positions[source + 1];
      const distance = Math.max(0.001, Math.hypot(dx, dy));
      const shift = (distance - desired) * strength;
      const sx = dx / distance * shift;
      const sy = dy / distance * shift;
      positions[source] += sx;
      positions[source + 1] += sy;
      positions[target] -= sx;
      positions[target + 1] -= sy;
    }
  }
}

function computeRank(node, index, maxDegree, rootSet) {
  const degreeScore = node.degree / Math.max(1, maxDegree);
  const rootBoost = rootSet.has(node.globalId) ? 1 : 0;
  const nameScore = 1 / Math.max(1, node.globalId.split("/").length);
  const stableNoise = (hashString(node.globalId) % 997) / 997000;
  return rootBoost * 2 + degreeScore + nameScore * 0.2 + stableNoise + 1 / (index + 10);
}

function colorForNode(node) {
  const hex = node.attributes["projection.color"] || node.attributes.color;
  if (hex && /^#[0-9a-f]{6}$/i.test(hex)) {
    return hex;
  }

  return palette[hashString(node.globalId) % palette.length];
}

function computeBounds(positions) {
  if (positions.length === 0) {
    return { minX: -1, minY: -1, maxX: 1, maxY: 1 };
  }

  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (let index = 0; index < positions.length; index += 2) {
    const x = positions[index];
    const y = positions[index + 1];
    minX = Math.min(minX, x);
    minY = Math.min(minY, y);
    maxX = Math.max(maxX, x);
    maxY = Math.max(maxY, y);
  }

  return {
    minX: minX - 60,
    minY: minY - 60,
    maxX: maxX + 60,
    maxY: maxY + 60
  };
}

function locateTile(x, y, bounds, tileWidth, tileHeight, gridSize) {
  return {
    x: clampIndex(Math.floor((x - bounds.minX) / tileWidth), 0, gridSize - 1),
    y: clampIndex(Math.floor((y - bounds.minY) / tileHeight), 0, gridSize - 1)
  };
}

function tilesForSegment(source, target, bounds, tileWidth, tileHeight, gridSize) {
  const minX = Math.min(source.x, target.x);
  const maxX = Math.max(source.x, target.x);
  const minY = Math.min(source.y, target.y);
  const maxY = Math.max(source.y, target.y);
  const tileMinX = clampIndex(Math.floor((minX - bounds.minX) / tileWidth), 0, gridSize - 1);
  const tileMaxX = clampIndex(Math.floor((maxX - bounds.minX) / tileWidth), 0, gridSize - 1);
  const tileMinY = clampIndex(Math.floor((minY - bounds.minY) / tileHeight), 0, gridSize - 1);
  const tileMaxY = clampIndex(Math.floor((maxY - bounds.minY) / tileHeight), 0, gridSize - 1);
  const keys = [];

  for (let y = tileMinY; y <= tileMaxY; y += 1) {
    for (let x = tileMinX; x <= tileMaxX; x += 1) {
      const rect = {
        minX: bounds.minX + x * tileWidth,
        minY: bounds.minY + y * tileHeight,
        maxX: bounds.minX + (x + 1) * tileWidth,
        maxY: bounds.minY + (y + 1) * tileHeight
      };
      if (lineIntersectsRect(source, target, rect)) {
        keys.push(tileKey(x, y));
      }
    }
  }

  return keys;
}

function lineIntersectsRect(a, b, rect) {
  if (pointInRect(a, rect) || pointInRect(b, rect)) {
    return true;
  }

  const corners = [
    { x: rect.minX, y: rect.minY },
    { x: rect.maxX, y: rect.minY },
    { x: rect.maxX, y: rect.maxY },
    { x: rect.minX, y: rect.maxY }
  ];
  return segmentsIntersect(a, b, corners[0], corners[1]) ||
    segmentsIntersect(a, b, corners[1], corners[2]) ||
    segmentsIntersect(a, b, corners[2], corners[3]) ||
    segmentsIntersect(a, b, corners[3], corners[0]);
}

function pointInRect(point, rect) {
  return point.x >= rect.minX && point.x <= rect.maxX &&
    point.y >= rect.minY && point.y <= rect.maxY;
}

function segmentsIntersect(a, b, c, d) {
  const ab = orientation(a, b, c) * orientation(a, b, d);
  const cd = orientation(c, d, a) * orientation(c, d, b);
  return ab <= 0 && cd <= 0;
}

function orientation(a, b, c) {
  return Math.sign((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x));
}

function boxesIntersect(a, b) {
  return a.x1 < b.x2 && a.x2 > b.x1 && a.y1 < b.y2 && a.y2 > b.y1;
}

function tileKey(x, y) {
  return `${x}:${y}`;
}

function parseTileKey(key) {
  const [x, y] = key.split(":").map(Number);
  return [x, y];
}

function worldToScreen(x, y) {
  return {
    x: x * state.view.scale + state.view.x,
    y: y * state.view.scale + state.view.y
  };
}

function screenToWorld(x, y) {
  return {
    x: (x - state.view.x) / state.view.scale,
    y: (y - state.view.y) / state.view.scale
  };
}

function resizeCanvas() {
  const rect = canvas.getBoundingClientRect();
  const ratio = Math.max(1, Math.min(window.devicePixelRatio || 1, maxDevicePixelRatio));
  const width = Math.max(1, Math.floor(rect.width * ratio));
  const height = Math.max(1, Math.floor(rect.height * ratio));
  if (canvas.width === width && canvas.height === height) {
    return;
  }

  canvas.width = width;
  canvas.height = height;
  ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
}

function roundRect(context, x, y, width, height, radius) {
  context.beginPath();
  context.moveTo(x + radius, y);
  context.arcTo(x + width, y, x + width, y + height, radius);
  context.arcTo(x + width, y + height, x, y + height, radius);
  context.arcTo(x, y + height, x, y, radius);
  context.arcTo(x, y, x + width, y, radius);
  context.closePath();
}

function hashString(value) {
  let hash = 2166136261;
  for (let index = 0; index < value.length; index += 1) {
    hash ^= value.charCodeAt(index);
    hash = Math.imul(hash, 16777619);
  }
  return hash >>> 0;
}

function formatCount(value) {
  return new Intl.NumberFormat("en-US").format(value);
}

function clamp(value, min, max) {
  return Math.min(max, Math.max(min, value));
}

function clampIndex(value, min, max) {
  if (!Number.isFinite(value)) {
    return min;
  }

  return Math.trunc(clamp(value, min, max));
}
