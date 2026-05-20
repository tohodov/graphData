const svg = document.querySelector("#graph");
const viewport = document.querySelector("#viewport");
const rootForm = document.querySelector("#root-form");
const rootInput = document.querySelector("#root-input");
const fitButton = document.querySelector("#fit-button");
const resetButton = document.querySelector("#reset-button");
const statusOutput = document.querySelector("#status");
const emptyState = document.querySelector("#empty-state");
const selectedName = document.querySelector("#selected-name");
const nodeMeta = document.querySelector("#node-meta");
const neighborList = document.querySelector("#neighbor-list");

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
  busy: false
};

const params = new URLSearchParams(window.location.search);
const initialNode = params.get("node");
if (initialNode) {
  rootInput.value = initialNode;
  loadRoot(initialNode);
}

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
    setStatus("Введите имя узла");
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
  url.searchParams.set("node", name);
  window.history.replaceState({}, "", url);
  fitView();
}

async function loadNode(name, fromName) {
  setBusy(true);
  try {
    const response = await fetch(`/api/graph-viewer/nodes?name=${encodeURIComponent(name)}`);
    if (response.status === 404) {
      setStatus(`Узел "${name}" не найден`);
      return;
    }

    if (!response.ok) {
      const text = await response.text();
      throw new Error(text || `HTTP ${response.status}`);
    }

    const expansion = await response.json();
    state.loaded.set(expansion.node.name, expansion);
    state.selectedName = expansion.node.name;
    seedPosition(expansion.node.name, fromName, 0);
    if (fromName && !state.parentByNode.has(expansion.node.name)) {
      state.parentByNode.set(expansion.node.name, fromName);
    }

    expansion.edges.forEach((edge, index) => {
      seedPosition(edge.targetName, expansion.node.name, index);
    });

    render();
    runSimulation(34);
    setStatus(`Развернуто узлов: ${state.loaded.size}`);
  } catch (error) {
    setStatus(error instanceof Error ? error.message : "Не удалось загрузить узел");
  } finally {
    setBusy(false);
  }
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
  removed.forEach(nodeName => {
    state.loaded.delete(nodeName);
    state.parentByNode.delete(nodeName);
  });

  state.selectedName = fallbackSelection;
  render();
  runSimulation(18);
  setStatus(`Развернуто узлов: ${state.loaded.size}`);
}

function handleEndpointClick(edge, anchorName) {
  const otherName = edge.sourceName === anchorName ? edge.targetName : edge.sourceName;
  const anchorLoaded = state.loaded.has(anchorName);
  const otherLoaded = state.loaded.has(otherName);

  if (anchorLoaded && !otherLoaded) {
    loadNode(otherName, anchorName);
    return;
  }

  if (!anchorLoaded && otherLoaded) {
    loadNode(anchorName, otherName);
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
    const source = expansion.node;
    nodes.set(source.name, {
      name: source.name,
      attributes: source.attributes ?? {}
    });

    expansion.edges.forEach(edge => {
      const key = edgeKey(edge.sourceName, edge.targetName);
      if (!edges.has(key)) {
        edges.set(key, {
          key,
          sourceName: edge.sourceName,
          targetName: edge.targetName
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
  const sourceLoaded = state.loaded.has(edge.sourceName);
  const targetLoaded = state.loaded.has(edge.targetName);
  const source = state.positions.get(edge.sourceName);
  const target = state.positions.get(edge.targetName);

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
    renderEndpointButton(buttonLayer, sourceButton, edge, edge.sourceName, false);
    renderEndpointButton(buttonLayer, targetButton, edge, edge.targetName, false);
    return;
  }

  const anchorName = sourceLoaded ? edge.sourceName : edge.targetName;
  const hiddenName = sourceLoaded ? edge.targetName : edge.sourceName;
  const anchor = sourceLoaded ? source : target;
  const hidden = sourceLoaded ? target : source;
  const buttonPoint = pointOnCircle(anchor, hidden, endpointOffset);
  renderEndpointButton(buttonLayer, buttonPoint, edge, anchorName, true, hiddenName);
}

function renderEndpointButton(layer, point, edge, anchorName, collapsed, hiddenName = null) {
  const otherName = hiddenName ?? (edge.sourceName === anchorName ? edge.targetName : edge.sourceName);
  const group = createSvg("g", {
    class: `edge-button ${collapsed ? "collapsed" : "expanded"}`,
    transform: `translate(${point.x} ${point.y})`,
    role: "button",
    tabindex: "0",
    "aria-label": collapsed
      ? `Развернуть ${otherName}`
      : `Свернуть или выбрать ${otherName}`
  });
  const title = createSvg("title", {});
  title.textContent = collapsed
    ? `Развернуть ${otherName}`
    : `Свернуть или выбрать ${otherName}`;

  const hit = createSvg("circle", {
    class: "edge-button-hit",
    r: 17,
    cx: 0,
    cy: 0
  });
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
    tabindex: "0"
  });

  const circle = createSvg("circle", {
    class: "node-shell",
    r: nodeRadius,
    cx: 0,
    cy: 0
  });

  const label = createSvg("text", {
    class: "node-label",
    x: 0,
    y: 0
  });
  label.textContent = trimName(node.name, 18);

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

  group.append(circle, label);
  layer.append(group);
}

function renderInspector(graph) {
  const selected = graph.nodes.find(node => node.name === state.selectedName);
  selectedName.textContent = selected?.name ?? "-";
  nodeMeta.replaceChildren();
  neighborList.replaceChildren();

  if (!selected) {
    return;
  }

  const entries = Object.entries(selected.attributes ?? {});
  if (entries.length === 0) {
    const row = document.createElement("div");
    const term = document.createElement("dt");
    const detail = document.createElement("dd");
    term.textContent = "attributes";
    detail.textContent = "-";
    row.append(term, detail);
    nodeMeta.append(row);
  } else {
    entries.forEach(([key, value]) => {
      const row = document.createElement("div");
      const term = document.createElement("dt");
      const detail = document.createElement("dd");
      term.textContent = key;
      detail.textContent = value;
      row.append(term, detail);
      nodeMeta.append(row);
    });
  }

  const neighbors = graph.edges
    .filter(edge => edge.sourceName === selected.name || edge.targetName === selected.name)
    .map(edge => edge.sourceName === selected.name ? edge.targetName : edge.sourceName)
    .sort((a, b) => a.localeCompare(b, "ru"));

  neighbors.forEach(name => {
    const edge = graph.edges.find(candidate =>
      (candidate.sourceName === selected.name && candidate.targetName === name) ||
      (candidate.sourceName === name && candidate.targetName === selected.name));
    const row = document.createElement("button");
    row.type = "button";
    row.className = `neighbor-row${state.loaded.has(name) ? " loaded" : ""}`;
    const dot = document.createElement("span");
    dot.className = "neighbor-dot";
    const text = document.createElement("span");
    text.textContent = name;
    row.append(dot, text);
    row.addEventListener("click", () => {
      if (edge) {
        handleEndpointClick(edge, selected.name);
      }
    });
    neighborList.append(row);
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
    .filter(edge => state.loaded.has(edge.sourceName) && state.loaded.has(edge.targetName))
    .forEach(edge => {
      const source = state.positions.get(edge.sourceName);
      const target = state.positions.get(edge.targetName);
      if (!source || !target) {
        return;
      }

      let dx = target.x - source.x;
      let dy = target.y - source.y;
      let distance = Math.max(1, Math.hypot(dx, dy));
      const strength = (distance - 185) * 0.018;
      const fx = (dx / distance) * strength;
      const fy = (dy / distance) * strength;
      forces.get(edge.sourceName).x += fx;
      forces.get(edge.sourceName).y += fy;
      forces.get(edge.targetName).x -= fx;
      forces.get(edge.targetName).y -= fy;
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

function trimName(name, limit) {
  return name.length > limit ? `${name.slice(0, limit - 1)}…` : name;
}

function setStatus(message) {
  statusOutput.value = message;
  statusOutput.textContent = message;
}

function setBusy(value) {
  state.busy = value;
  rootForm.querySelector("button").disabled = value;
}
