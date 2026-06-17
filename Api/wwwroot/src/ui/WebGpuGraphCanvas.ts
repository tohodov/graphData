import { nodeRadius } from "../domain/graphAttributes.js";
import { HtmlCanvasRenderer } from "./HtmlCanvasRenderer.js";
import { SvgRenderer } from "./SvgRenderer.js";
import { WebGpuRenderer } from "./WebGpuRenderer.js";
import type { GraphRenderer } from "./GraphRenderer.js";

const tapMoveThreshold = 8;
const defaultEdgeColor = [0.20, 0.27, 0.30, 0.62];
const defaultNodeStrokeColor = [0.09, 0.13, 0.14, 1];
const maxLabels = 280;
const endpointControlPadding = 9;

export class WebGpuGraphCanvas {
  [key: string]: any;

  constructor({
    document,
    window,
    canvas,
    rendererSelect,
    labelLayer,
    emptyState,
    gpuWarning,
    buildGraph,
    positions,
    velocities,
    view,
    isNodeSelected,
    selectOnlyNode,
    addNodeToSelection,
    toggleNodeSelection,
    canCollapseNode,
    collapseNode,
    edgeEndpointControl,
    activateEdgeEndpoint,
    renderInspector,
    formatRank
  }) {
    this.document = document;
    this.window = window;
    this.canvas = canvas;
    this.rendererSelect = rendererSelect;
    this.labelLayer = labelLayer;
    this.emptyState = emptyState;
    this.gpuWarning = gpuWarning;
    this.buildGraph = buildGraph;
    this.positions = positions;
    this.velocities = velocities;
    this.view = view;
    this.callbacks = {
      isNodeSelected,
      selectOnlyNode,
      addNodeToSelection,
      toggleNodeSelection,
      canCollapseNode,
      collapseNode,
      edgeEndpointControl,
      activateEdgeEndpoint,
      renderInspector,
      formatRank
    };
    this.renderer = null as GraphRenderer | null;
    this.rendererReady = false;
    this.rendererMode = this.preferredRendererMode();
    this.webGpuError = null;
    this.renderPending = false;
    this.memory = null;
    this.currentGraph = null;
    this.dragging = null;
    this.pointer = null;
    this.simulationHandle = null;

    this.bindRendererSelect();
    void this.initializeRenderer();
  }

  preferredRendererMode() {
    const params = new URLSearchParams(this.window.location.search);
    return normalizeRendererMode(params.get("renderer") ?? params.get("render"));
  }

  createRenderer(mode) {
    const host = {
      document: this.document,
      window: this.window,
      surface: this.canvas
    };
    if (mode === "svg") {
      return new SvgRenderer(host);
    }

    if (mode === "html-canvas") {
      return new HtmlCanvasRenderer(host);
    }

    return new WebGpuRenderer(host);
  }

  async initializeRenderer() {
    const preferred = this.preferredRendererMode();
    try {
      await this.activateRenderer(preferred);
      this.setGpuWarning(null);
    } catch (error) {
      if (preferred !== "svg") {
        this.webGpuError = error;
        try {
          await this.activateRenderer("svg");
          this.setGpuWarning(null);
          return;
        } catch (fallbackError) {
          this.rendererReady = false;
          this.setGpuWarning(fallbackError);
          this.renderLabels();
          return;
        }
      }

      this.rendererReady = false;
      this.setGpuWarning(error);
      this.renderLabels();
    }
  }

  async activateRenderer(mode) {
    const targetMode = normalizeRendererMode(mode);
    const previousRenderer = this.renderer;
    const previousMode = this.rendererMode;
    const previousReady = this.rendererReady;
    this.rendererReady = false;
    const renderer = this.createRenderer(targetMode);
    this.renderer = renderer;
    this.rendererMode = targetMode;

    try {
      await renderer.init();
    } catch (error) {
      if (this.renderer === renderer) {
        renderer.dispose();
        this.renderer = previousRenderer;
        this.rendererMode = previousMode;
        this.rendererReady = previousReady;
      }
      throw error;
    }

    if (previousRenderer && previousRenderer !== renderer) {
      previousRenderer.dispose();
    }
    this.rendererMode = renderer.mode ?? targetMode;
    this.rendererReady = true;
    this.syncRendererSelect();
    this.updateRendererGraph();
    this.renderLabels();
    this.requestDraw();
  }

  bindRendererSelect() {
    if (!this.rendererSelect) {
      return;
    }

    this.syncRendererSelect();
    this.rendererSelect.addEventListener("change", () => {
      void this.changeRenderer(this.rendererSelect.value);
    });
  }

  async changeRenderer(mode) {
    const targetMode = normalizeRendererMode(mode);
    if (targetMode === this.rendererMode && this.rendererReady) {
      this.syncRendererSelect();
      return;
    }

    try {
      await this.activateRenderer(targetMode);
      this.setGpuWarning(null);
      this.writeRendererModeToUrl(targetMode);
    } catch (error) {
      this.setGpuWarning(error);
      this.syncRendererSelect();
    }
  }

  syncRendererSelect() {
    if (!this.rendererSelect) {
      return;
    }

    const option = [...this.rendererSelect.options].find(item => item.value === this.rendererMode);
    if (option) {
      this.rendererSelect.value = this.rendererMode;
    }
  }

  writeRendererModeToUrl(mode) {
    const url = new URL(this.window.location.href);
    url.searchParams.set("renderer", mode);
    this.window.history.replaceState({}, "", url);
  }

  bindGraphSurface() {
    this.canvas.addEventListener("pointerdown", event => {
      if (event.button !== 0) {
        return;
      }

      this.canvas.setPointerCapture(event.pointerId);
      const hit = this.pickNearest(event.clientX, event.clientY);
      if (hit) {
        this.dragging = {
          name: hit.name,
          x: event.clientX,
          y: event.clientY,
          startX: event.clientX,
          startY: event.clientY,
          pointerType: event.pointerType ?? "mouse"
        };
      } else {
        this.pointer = {
          x: event.clientX,
          y: event.clientY,
          startX: event.clientX,
          startY: event.clientY
        };
      }
      this.canvas.classList.add("dragging");
    });

    this.canvas.addEventListener("pointermove", event => {
      if (this.dragging) {
        const position = this.positions.get(this.dragging.name);
        if (!position) {
          return;
        }

        const dx = (event.clientX - this.dragging.x) / this.view.scale;
        const dy = (event.clientY - this.dragging.y) / this.view.scale;
        position.x += dx;
        position.y += dy;
        this.dragging.x = event.clientX;
        this.dragging.y = event.clientY;
        this.updateDynamicGraph();
        return;
      }

      if (!this.pointer) {
        return;
      }

      const dx = event.clientX - this.pointer.x;
      const dy = event.clientY - this.pointer.y;
      this.pointer.x = event.clientX;
      this.pointer.y = event.clientY;
      this.view.x += dx;
      this.view.y += dy;
      this.applyView();
    });

    this.canvas.addEventListener("pointerup", event => {
      try {
        this.canvas.releasePointerCapture(event.pointerId);
      } catch {
        // Pointer capture can already be gone after browser-level cancellation.
      }

      const dragging = this.dragging;
      const pointer = this.pointer;
      this.dragging = null;
      this.pointer = null;
      this.canvas.classList.remove("dragging");

      if (dragging) {
        const moved = Math.hypot(event.clientX - dragging.startX, event.clientY - dragging.startY);
        if (moved <= tapMoveThreshold) {
          this.selectNode(dragging.name, event, dragging.pointerType);
        }
        return;
      }

      if (pointer) {
        const moved = Math.hypot(event.clientX - pointer.startX, event.clientY - pointer.startY);
        if (moved <= tapMoveThreshold) {
          const hit = this.pickNearest(event.clientX, event.clientY);
          if (hit) {
            this.selectNode(hit.name, event, event.pointerType ?? "mouse");
          }
        }
      }
    });

    this.canvas.addEventListener("pointercancel", event => {
      try {
        this.canvas.releasePointerCapture(event.pointerId);
      } catch {
        // Ignore stale captures.
      }
      this.dragging = null;
      this.pointer = null;
      this.canvas.classList.remove("dragging");
    });

    this.canvas.addEventListener("wheel", event => {
      event.preventDefault();
      const rect = this.canvas.getBoundingClientRect();
      const mouseX = event.clientX - rect.left;
      const mouseY = event.clientY - rect.top;
      const before = this.screenToGraph(mouseX, mouseY);
      const scale = clamp(this.view.scale * Math.exp(-event.deltaY * 0.0012), 0.02, 18);
      this.view.scale = scale;
      this.view.x = mouseX - before.x * scale;
      this.view.y = mouseY - before.y * scale;
      this.applyView();
    }, { passive: false });

    this.window.addEventListener("resize", () => this.applyView());
  }

  render() {
    const graph = this.buildGraph();
    this.currentGraph = graph;
    this.emptyState.classList.toggle("hidden", graph.nodes.length > 0);
    this.memory = this.buildRenderMemory(graph);
    this.callbacks.renderInspector(graph);
    this.renderLabels();
    this.updateRendererGraph();
    this.requestDraw();
  }

  runSimulation(frames) {
    if (this.simulationHandle) {
      this.window.cancelAnimationFrame(this.simulationHandle);
    }

    if (!this.currentGraph) {
      this.render();
    }

    let remaining = frames;
    const tick = () => {
      this.simulateStep();
      this.updateDynamicGraph();
      remaining -= 1;
      if (remaining > 0) {
        this.simulationHandle = this.window.requestAnimationFrame(tick);
      }
    };

    this.simulationHandle = this.window.requestAnimationFrame(tick);
  }

  simulateStep() {
    const graph = this.currentGraph ?? this.buildGraph();
    const forces = new Map<any, any>(graph.nodes.map(node => [node.name, { x: 0, y: 0 }]));

    graph.edges.forEach(edge => {
      if (edge.collapsed) {
        return;
      }

      const source = this.positions.get(edge.sourceGlobalId);
      const target = this.positions.get(edge.targetGlobalId);
      const sourceForce = forces.get(edge.sourceGlobalId);
      const targetForce = forces.get(edge.targetGlobalId);
      if (!source || !target || !sourceForce || !targetForce) {
        return;
      }

      let dx = target.x - source.x;
      let dy = target.y - source.y;
      let distance = Math.max(1, Math.hypot(dx, dy));
      const desired = 130 + Math.min(90, Math.max(0, (edge.viewRank ?? 0) * 0.3));
      const strength = (distance - desired) * 0.018;
      const fx = (dx / distance) * strength;
      const fy = (dy / distance) * strength;
      sourceForce.x += fx;
      sourceForce.y += fy;
      targetForce.x -= fx;
      targetForce.y -= fy;
    });

    graph.nodes.forEach(node => {
      const position = this.positions.get(node.name);
      const velocity = this.velocities.get(node.name) ?? { x: 0, y: 0 };
      const force = forces.get(node.name);
      if (!position || !force) {
        return;
      }

      force.x += -position.x * 0.0018;
      force.y += -position.y * 0.0018;
      velocity.x = (velocity.x + force.x) * 0.82;
      velocity.y = (velocity.y + force.y) * 0.82;
      position.x += velocity.x;
      position.y += velocity.y;
      this.velocities.set(node.name, velocity);
    });
  }

  fitView() {
    const graph = this.currentGraph ?? this.buildGraph();
    if (graph.nodes.length === 0) {
      this.view.x = 0;
      this.view.y = 0;
      this.view.scale = 1;
      this.applyView();
      return;
    }

    const rect = this.canvas.getBoundingClientRect();
    const points = graph.nodes
      .map(node => this.positions.get(node.name))
      .filter(Boolean);
    if (points.length === 0) {
      return;
    }

    const minX = Math.min(...points.map(point => point.x)) - 120;
    const maxX = Math.max(...points.map(point => point.x)) + 120;
    const minY = Math.min(...points.map(point => point.y)) - 120;
    const maxY = Math.max(...points.map(point => point.y)) + 120;
    const width = Math.max(1, maxX - minX);
    const height = Math.max(1, maxY - minY);
    const scale = clamp(Math.min(rect.width / width, rect.height / height), 0.04, 2.2);

    this.view.scale = scale;
    this.view.x = rect.width / 2 - ((minX + maxX) / 2) * scale;
    this.view.y = rect.height / 2 - ((minY + maxY) / 2) * scale;
    this.applyView();
  }

  applyView() {
    this.renderLabels();
    this.requestDraw();
  }

  screenToGraph(x, y) {
    return {
      x: (x - this.view.x) / this.view.scale,
      y: (y - this.view.y) / this.view.scale
    };
  }

  buildRenderMemory(graph) {
    const nodes = graph.nodes.filter(node => this.positions.has(node.name));
    const nodeIndexByName = new Map(nodes.map((node, index) => [node.name, index]));
    const edgeMap = new Map();

    graph.edges.forEach(edge => {
      if (edge.collapsed) {
        return;
      }

      const source = nodeIndexByName.get(edge.sourceGlobalId);
      const target = nodeIndexByName.get(edge.targetGlobalId);
      if (source === undefined || target === undefined || source === target) {
        return;
      }

      const key = edge.key ?? edgeKey(edge.sourceGlobalId, edge.targetGlobalId, edge.relationGlobalId ?? edge.typeGlobalId ?? "");
      if (!edgeMap.has(key)) {
        edgeMap.set(key, { ...edge, sourceIndex: source, targetIndex: target });
      }
    });

    const edges = [...edgeMap.values()];
    const memory = {
      graph,
      nodes,
      edges,
      nodeIndexByName,
      nodeCount: nodes.length,
      edgeCount: edges.length,
      nodeVertexData: new Float32Array(nodes.length * 8),
      edgeVertexData: new Float32Array(edges.length * 12),
      byteLength: 0
    };
    memory.byteLength = memory.nodeVertexData.byteLength + memory.edgeVertexData.byteLength;
    this.writeVertexData(memory);
    return memory;
  }

  writeVertexData(memory) {
    memory.nodes.forEach((node, index) => {
      const position = this.positions.get(node.name) ?? { x: 0, y: 0 };
      const color = parseColor(node.color, defaultNodeStrokeColor);
      const radius = Number.isFinite(node.viewRadius) ? node.viewRadius : nodeRadius;
      const base = index * 8;
      memory.nodeVertexData[base + 0] = position.x;
      memory.nodeVertexData[base + 1] = position.y;
      memory.nodeVertexData[base + 2] = color[0];
      memory.nodeVertexData[base + 3] = color[1];
      memory.nodeVertexData[base + 4] = color[2];
      memory.nodeVertexData[base + 5] = color[3];
      memory.nodeVertexData[base + 6] = radius;
      memory.nodeVertexData[base + 7] = this.callbacks.isNodeSelected(node.name) ? 1 : 0;
    });

    memory.edges.forEach((edge, index) => {
      const source = this.positions.get(edge.sourceGlobalId) ?? { x: 0, y: 0 };
      const target = this.positions.get(edge.targetGlobalId) ?? { x: 0, y: 0 };
      const color = parseColor(edge.color, defaultEdgeColor);
      const base = index * 12;
      memory.edgeVertexData[base + 0] = source.x;
      memory.edgeVertexData[base + 1] = source.y;
      memory.edgeVertexData[base + 2] = color[0];
      memory.edgeVertexData[base + 3] = color[1];
      memory.edgeVertexData[base + 4] = color[2];
      memory.edgeVertexData[base + 5] = color[3];
      memory.edgeVertexData[base + 6] = target.x;
      memory.edgeVertexData[base + 7] = target.y;
      memory.edgeVertexData[base + 8] = color[0];
      memory.edgeVertexData[base + 9] = color[1];
      memory.edgeVertexData[base + 10] = color[2];
      memory.edgeVertexData[base + 11] = color[3];
    });
  }

  updateDynamicGraph() {
    if (!this.memory) {
      return;
    }

    this.writeVertexData(this.memory);
    this.renderLabels();
    this.updateRendererGraph();
    this.requestDraw();
  }

  updateRendererGraph() {
    if (!this.rendererReady || !this.memory) {
      return;
    }

    this.renderer?.updateGraph(this.memory);
  }

  requestDraw() {
    if (this.renderPending) {
      return;
    }

    this.renderPending = true;
    this.window.requestAnimationFrame(() => {
      this.renderPending = false;
      if (this.rendererReady && this.renderer) {
        this.renderer.draw(this.view);
      }
    });
  }

  renderLabels() {
    if (!this.labelLayer) {
      return;
    }

    this.labelLayer.replaceChildren();
    if (!this.rendererReady || !this.memory || this.memory.nodeCount === 0) {
      return;
    }

    const rect = this.canvas.getBoundingClientRect();
    const fragment = this.document.createDocumentFragment();
    this.renderEdgeEndpointControls(fragment, rect);
    const labelledNodes = this.memory.nodes
      .filter(node => {
        const position = this.positions.get(node.name);
        if (!position) {
          return false;
        }

        const x = position.x * this.view.scale + this.view.x;
        const y = position.y * this.view.scale + this.view.y;
        const radius = node.viewRadius ?? nodeRadius;
        const margin = Math.max(80, radius * this.view.scale + 40);
        return x >= -margin && x <= rect.width + margin && y >= -margin && y <= rect.height + margin;
      })
      .sort((left, right) =>
        Number(this.callbacks.isNodeSelected(right.name)) - Number(this.callbacks.isNodeSelected(left.name)))
      .slice(0, maxLabels);

    labelledNodes.forEach(node => {
      const position = this.positions.get(node.name);
      if (!position) {
        return;
      }

      const radius = node.viewRadius ?? nodeRadius;
      const x = position.x * this.view.scale + this.view.x;
      const y = position.y * this.view.scale + this.view.y;
      const selected = this.callbacks.isNodeSelected(node.name);
      const label = this.document.createElement("div");
      label.className = `graph-node-label${selected ? " selected" : ""}`;
      label.textContent = node.displayName ?? node.localId ?? node.name;
      label.title = node.globalId ?? node.name;
      label.style.width = `${Math.max(28, radius * 2 - 16)}px`;
      label.style.transform = `translate(${x}px, ${y}px) translate(-50%, -50%)`;
      fragment.append(label);

      if (this.callbacks.canCollapseNode?.(node.name)) {
        const collapseButton = this.document.createElement("button");
        const controlRadius = radius + (selected ? 4 : 0);
        collapseButton.type = "button";
        collapseButton.className = "graph-node-collapse";
        collapseButton.textContent = "-";
        collapseButton.title = "Свернуть узел";
        collapseButton.setAttribute("aria-label", "Свернуть узел");
        collapseButton.style.transform = `translate(${x + controlRadius * 0.72}px, ${y - controlRadius * 0.72}px) translate(-50%, -50%)`;
        collapseButton.addEventListener("pointerdown", event => {
          event.preventDefault();
          event.stopPropagation();
        });
        collapseButton.addEventListener("click", event => {
          event.preventDefault();
          event.stopPropagation();
          this.callbacks.collapseNode?.(node.name);
        });
        fragment.append(collapseButton);
      }
    });

    this.labelLayer.append(fragment);
  }

  renderEdgeEndpointControls(fragment, rect) {
    const graph = this.currentGraph ?? this.memory?.graph;
    if (!graph?.edges?.length) {
      return;
    }

    const nodesByName = new Map((graph.nodes ?? []).map(node => [node.name, node]));
    const rendered = new Set();
    graph.edges.forEach(edge => {
      this.renderEdgeEndpointControl(fragment, rect, edge, edge.sourceGlobalId, edge.targetGlobalId, nodesByName, rendered);
      this.renderEdgeEndpointControl(fragment, rect, edge, edge.targetGlobalId, edge.sourceGlobalId, nodesByName, rendered);
    });
  }

  renderEdgeEndpointControl(fragment, rect, edge, anchorName, otherName, nodesByName, rendered) {
    if (!anchorName || !otherName) {
      return;
    }

    const control = this.callbacks.edgeEndpointControl?.(edge, anchorName);
    if (!control) {
      return;
    }

    const anchor = this.positions.get(anchorName);
    const other = this.positions.get(otherName);
    if (!anchor || !other) {
      return;
    }

    const anchorNode = nodesByName.get(anchorName);
    const radius = Number.isFinite(anchorNode?.viewRadius) ? anchorNode.viewRadius : nodeRadius;
    const point = pointOnCircle(anchor, other, radius + endpointControlPadding);
    const x = point.x * this.view.scale + this.view.x;
    const y = point.y * this.view.scale + this.view.y;
    const margin = 36;
    if (x < -margin || x > rect.width + margin || y < -margin || y > rect.height + margin) {
      return;
    }

    const key = `${edge.key ?? ""}\0${anchorName}\0${otherName}\0${control.kind ?? ""}`;
    if (rendered.has(key)) {
      return;
    }
    rendered.add(key);

    const title = control.title ?? (control.kind === "expand" ? "Развернуть связь" : "Свернуть связь");
    const button = this.document.createElement("button");
    button.type = "button";
    button.className = `graph-edge-control ${control.kind === "expand" ? "collapsed" : "expanded"}`;
    button.textContent = control.text ?? (control.kind === "expand" ? "+" : "-");
    button.title = title;
    button.setAttribute("aria-label", title);
    button.dataset.edgeEndpoint = control.kind ?? "";
    button.dataset.anchorName = anchorName;
    button.dataset.otherName = control.otherName ?? otherName;
    button.style.transform = `translate(${x}px, ${y}px) translate(-50%, -50%)`;
    button.addEventListener("pointerdown", event => {
      event.preventDefault();
      event.stopPropagation();
    });
    button.addEventListener("click", event => {
      event.preventDefault();
      event.stopPropagation();
      this.callbacks.activateEdgeEndpoint?.(edge, anchorName);
    });
    fragment.append(button);
  }

  pickNearest(clientX, clientY) {
    if (!this.memory || this.memory.nodeCount === 0) {
      return null;
    }

    const rect = this.canvas.getBoundingClientRect();
    const x = clientX - rect.left;
    const y = clientY - rect.top;
    let best = null;
    let bestDistance = Infinity;

    for (let index = 0; index < this.memory.nodes.length; index += 1) {
      const node = this.memory.nodes[index];
      const position = this.positions.get(node.name);
      if (!position) {
        continue;
      }

      const sx = position.x * this.view.scale + this.view.x;
      const sy = position.y * this.view.scale + this.view.y;
      const radius = clamp((node.viewRadius ?? nodeRadius) + 10, 12, 56);
      const distance = (sx - x) ** 2 + (sy - y) ** 2;
      if (distance <= radius ** 2 && distance < bestDistance) {
        bestDistance = distance;
        best = node;
      }
    }

    return best;
  }

  selectNode(name, event, pointerType) {
    if (pointerType !== "mouse") {
      this.callbacks.toggleNodeSelection(name);
      return;
    }

    if (event.ctrlKey || event.metaKey) {
      this.callbacks.addNodeToSelection(name);
      return;
    }

    this.callbacks.selectOnlyNode(name);
  }

  setGpuWarning(error) {
    if (!this.gpuWarning) {
      return;
    }

    this.gpuWarning.replaceChildren();
    this.gpuWarning.hidden = !error;
    if (error) {
      const title = this.document.createElement("strong");
      title.textContent = "Renderer unavailable";
      const message = this.document.createElement("span");
      message.textContent = error.message ?? String(error);
      this.gpuWarning.append(title, message);
      this.gpuWarning.title = error.message ?? String(error);
    } else {
      this.gpuWarning.title = "";
    }
  }
}

function parseColor(value, fallback) {
  if (!value) {
    return fallback;
  }

  const text = String(value).trim();
  const hex = text.match(/^#([0-9a-f]{3}|[0-9a-f]{6})$/i);
  if (hex) {
    const raw = hex[1].length === 3
      ? [...hex[1]].map(char => char + char).join("")
      : hex[1];
    return [
      Number.parseInt(raw.slice(0, 2), 16) / 255,
      Number.parseInt(raw.slice(2, 4), 16) / 255,
      Number.parseInt(raw.slice(4, 6), 16) / 255,
      fallback[3] ?? 0.9
    ];
  }

  const rgba = text.match(/^rgba?\(([^)]+)\)$/i);
  if (rgba) {
    const parts = rgba[1].split(",").map(part => Number.parseFloat(part.trim()));
    if (parts.length >= 3 && parts.every(part => Number.isFinite(part))) {
      return [
        clamp(parts[0] / 255, 0, 1),
        clamp(parts[1] / 255, 0, 1),
        clamp(parts[2] / 255, 0, 1),
        parts.length > 3 ? clamp(parts[3], 0, 1) : (fallback[3] ?? 0.9)
      ];
    }
  }

  return fallback;
}

function edgeKey(source, target, discriminator) {
  return String(source).localeCompare(String(target), "ru") < 0
    ? `${source}\0${target}\0${discriminator}`
    : `${target}\0${source}\0${discriminator}`;
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

function clamp(value, min, max) {
  return Math.min(max, Math.max(min, value));
}

function normalizeRendererMode(mode) {
  const text = String(mode ?? "").toLowerCase().replace(/[_\s]/g, "-");
  if (text === "svg") {
    return "svg";
  }

  if (text === "html" || text === "htmlcanvas" || text === "html-in-canvas" || text === "html-canvas") {
    return "html-canvas";
  }

  return "webgpu";
}
