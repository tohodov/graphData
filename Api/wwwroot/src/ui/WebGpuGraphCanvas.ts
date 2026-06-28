import { nodeRadius } from "../domain/graphAttributes.js";
import { HtmlCanvasRenderer } from "./HtmlCanvasRenderer.js";
import { SvgRenderer } from "./SvgRenderer.js";
import { WebGpuRenderer } from "./WebGpuRenderer.js";
import { GraphNode } from "../domain/GraphNode.js";
import type { GraphRenderer, GraphRendererHost, GraphView, GraphRenderMemory } from "./GraphRenderer.js";

const tapMoveThreshold = 8;
const defaultEdgeColor = [0.20, 0.27, 0.30, 0.62];
const selectedEdgeColor = [0.96, 0.77, 0.28, 1];
const defaultNodeStrokeColor = [0.09, 0.13, 0.14, 1];
const maxLabels = 280;
const endpointControlPadding = 9;
const edgePickThreshold = 9;
const rendererModes = ["webgpu", "svg", "html-canvas"];

export class WebGpuGraphCanvas {
  document: Document;
  window: Window;
  canvas: HTMLCanvasElement;
  rendererSelect: HTMLSelectElement;
  labelLayer: HTMLElement;
  emptyState: HTMLElement;
  gpuWarning: HTMLElement | null;
  buildGraph: () => import("../domain/GraphModel.js").ProjectedGraph;
  positions: Map<string, { x: number; y: number; }>;
  velocities: Map<string, { x: number; y: number; }>;
  view: GraphView;
  callbacks: Record<string, Function>;
  renderer: GraphRenderer | null;
  rendererReady: boolean;
  rendererMode: string;
  rendererAvailability: Map<string, { available: boolean; error: Error | null; }> | null;
  rendererAvailabilityPromise: Promise<Map<string, { available: boolean; error: Error | null; }>> | null;
  webGpuError: Error | null;
  renderPending: boolean;
  memory: GraphRenderMemory | null;
  currentGraph: import("../domain/GraphModel.js").ProjectedGraph | null;
  dragging: { name: string; names: string[]; x: number; y: number; startX: number; startY: number; pointerType: string } | null;
  pointer: { x: number; y: number; startX: number; startY: number } | null;
  selectionBox: { startX: number; startY: number; x: number; y: number; append: boolean } | null;
  selectionBoxElement: HTMLElement;
  simulationHandle: number | null;
  resizeObserver: unknown | null;
  resizePending: boolean;

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
    isEdgeSelected,
    selectOnlyNode,
    addNodeToSelection,
    toggleNodeSelection,
    selectOnlyEdge,
    addEdgeToSelection,
    toggleEdgeSelection,
    selectGraphElements,
    calculateEdgeLabelPositions,
    activateEdgeControl,
    syncEdgeAngles,
    renderInspector,
    formatRank
  }: {
    document: Document;
    window: Window;
    canvas: HTMLCanvasElement;
    rendererSelect: HTMLSelectElement;
    labelLayer: HTMLElement;
    emptyState: HTMLElement;
    gpuWarning: HTMLElement | null;
    buildGraph: () => import("../domain/GraphModel.js").ProjectedGraph;
    positions: Map<string, { x: number; y: number; }>;
    velocities: Map<string, { x: number; y: number; }>;
    view: GraphView;
    isNodeSelected: (name: string) => boolean;
    isEdgeSelected: (key: string) => boolean;
    selectOnlyNode: (name: string) => void;
    addNodeToSelection: (name: string) => void;
    toggleNodeSelection: (name: string) => void;
    selectOnlyEdge: (key: string) => void;
    addEdgeToSelection: (key: string) => void;
    toggleEdgeSelection: (key: string) => void;
    selectGraphElements: (nodes: import("../domain/GraphModel.js").ProjectedGraphNode[], edges: import("../domain/GraphModel.js").ProjectedGraphEdge[], append: boolean) => void;
    calculateEdgeLabelPositions: (edges: import("../domain/GraphModel.js").ProjectedGraphEdge[], positions: Map<string, any>) => void;
    activateEdgeControl: (edge: import("../domain/GraphModel.js").ProjectedGraphEdge, control: any, pointer: any) => void;
    syncEdgeAngles: (edge: import("../domain/GraphModel.js").ProjectedGraphEdge, angle: number) => void;
    renderInspector: () => void;
    formatRank: (rank: number) => string;
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
      isEdgeSelected,
      selectOnlyNode,
      addNodeToSelection,
      toggleNodeSelection,
      selectOnlyEdge,
      addEdgeToSelection,
      toggleEdgeSelection,
      selectGraphElements,
      calculateEdgeLabelPositions,
      activateEdgeControl,
      syncEdgeAngles,
      renderInspector,
      formatRank
    };
    this.renderer = null as GraphRenderer | null;
    this.rendererReady = false;
    this.rendererMode = this.preferredRendererMode();
    this.rendererAvailability = null;
    this.rendererAvailabilityPromise = null;
    this.webGpuError = null;
    this.renderPending = false;
    this.memory = null;
    this.currentGraph = null;
    this.dragging = null;
    this.pointer = null;
    this.selectionBox = null;
    this.selectionBoxElement = this.createSelectionBoxElement();
    this.simulationHandle = null;
    this.resizeObserver = null;
    this.resizePending = false;

    this.bindRendererSelect();
    void this.initializeRenderer();
  }

  preferredRendererMode() {
    const params = new URLSearchParams(this.window.location.search);
    return normalizeRendererMode(params.get("renderer") ?? params.get("render"));
  }

  createRenderer(mode: string) {
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
    await this.checkRendererAvailability();

    let lastError = null;
    for (const mode of this.availableRendererOrder(preferred)) {
      try {
        await this.activateRenderer(mode);
        this.setGpuWarning(null);
        return;
      } catch (error) {
        lastError = error;
        this.setRendererAvailability(mode, false, error as Error);
      }
    }

    this.rendererReady = false;
    this.setGpuWarning(lastError as Error ?? new Error("No graph renderer is available"));
    this.renderLabels();
  }

  async activateRenderer(mode: string) {
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

    this.rendererSelect.disabled = true;
    this.syncRendererSelect();
    this.rendererSelect.addEventListener("change", () => {
      void this.changeRenderer(this.rendererSelect.value);
    });
  }

  async changeRenderer(mode: string) {
    const targetMode = normalizeRendererMode(mode);
    await this.checkRendererAvailability();

    if (!this.isRendererAvailable(targetMode)) {
      this.setGpuWarning(this.rendererAvailability?.get(targetMode)?.error as Error | null);
      this.syncRendererSelect();
      return;
    }

    if (targetMode === this.rendererMode && this.rendererReady) {
      this.syncRendererSelect();
      return;
    }

    try {
      await this.activateRenderer(targetMode);
      this.setGpuWarning(null);
      this.writeRendererModeToUrl(targetMode);
    } catch (error) {
      this.setRendererAvailability(targetMode, false, error as Error);
      this.setGpuWarning(error as Error | null);
    }
    this.syncRendererSelect();
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

  async checkRendererAvailability() {
    if (!this.rendererAvailabilityPromise) {
      this.rendererAvailabilityPromise = this.detectRendererAvailability();
    }

    this.rendererAvailability = await this.rendererAvailabilityPromise;
    this.syncRendererAvailability();
    return this.rendererAvailability;
  }

  async detectRendererAvailability() {
    const availability = new Map();
    await Promise.all(rendererModes.map(async mode => {
      try {
        await this.assertRendererAvailable(mode);
        availability.set(mode, { available: true, error: null });
      } catch (error) {
        availability.set(mode, { available: false, error });
      }
    }));
    return availability;
  }

  async assertRendererAvailable(mode: string) {
    if (mode === "svg") {
      return;
    }

    if (mode === "html-canvas") {
      const canvas = this.document.createElement("canvas") as HTMLCanvasElement & {
        layoutSubtree?: boolean;
        requestPaint?: () => void;
      };
      canvas.setAttribute("layoutsubtree", "");
      canvas.layoutSubtree = true;
      const context = canvas.getContext("2d") as (CanvasRenderingContext2D & {
        drawElementImage?: (...args: unknown[]) => DOMMatrix;
      }) | null;
      if (!context) {
        throw new Error("HTML-in-Canvas renderer requires a 2D canvas context.");
      }

      if (typeof context.drawElementImage !== "function"
        || typeof canvas.requestPaint !== "function") {
        throw new Error(
          "HTML-in-Canvas is unavailable: enable chrome://flags/#canvas-draw-element in Chromium.");
      }
      return;
    }

    if (!this.window.isSecureContext) {
      const origin = this.window.location?.origin ?? "unknown origin";
      throw new Error(`WebGPU requires HTTPS or localhost. Current origin is not secure: ${origin}`);
    }

    const gpu = this.window.navigator.gpu;
    if (!gpu) {
      throw new Error("WebGPU is unavailable: this browser context did not expose navigator.gpu.");
    }

    if (!globalThis.GPUBufferUsage) {
      throw new Error("WebGPU buffer usage constants are unavailable");
    }

    if (!globalThis.GPUShaderStage) {
      throw new Error("WebGPU shader stage constants are unavailable");
    }

    const adapter = await gpu.requestAdapter({ powerPreference: "high-performance" });
    if (!adapter) {
      throw new Error("WebGPU adapter was not found");
    }
  }

  syncRendererAvailability() {
    if (!this.rendererSelect) {
      return;
    }

    this.rendererSelect.disabled = !this.rendererAvailability;
    [...this.rendererSelect.options].forEach(option => {
      const mode = normalizeRendererMode(option.value);
      const status = this.rendererAvailability?.get(mode);
      const available = status?.available === true;
      option.disabled = !available;
      option.title = available ? "" : (status?.error?.message ?? String(status?.error ?? "Renderer unavailable"));
    });
    this.syncRendererSelect();
  }

  setRendererAvailability(mode: string, available: boolean, error: Error | null = null) {
    if (!this.rendererAvailability) {
      this.rendererAvailability = new Map();
    }

    this.rendererAvailability.set(normalizeRendererMode(mode), { available, error });
    this.syncRendererAvailability();
  }

  isRendererAvailable(mode: string) {
    return this.rendererAvailability?.get(normalizeRendererMode(mode))?.available === true;
  }

  availableRendererOrder(preferred: string) {
    const mode = normalizeRendererMode(preferred);
    const order = mode === "webgpu"
      ? ["webgpu", "svg", "html-canvas"]
      : [mode, "webgpu", "svg", "html-canvas"];
    return [...new Set(order)].filter(candidate => this.isRendererAvailable(candidate));
  }

  writeRendererModeToUrl(mode: string) {
    const url = new URL(this.window.location.href);
    url.searchParams.set("renderer", mode);
    this.window.history.replaceState({}, "", url);
  }

  createSelectionBoxElement() {
    const element = this.document.createElement("div");
    element.className = "graph-selection-box";
    element.hidden = true;
    (this.document.body ?? this.canvas).append(element);
    return element;
  }

  bindGraphSurface() {
    this.bindCanvasResizeObserver();

    this.canvas.addEventListener("pointerdown", event => {
      if (event.button !== 0) {
        return;
      }

      this.canvas.setPointerCapture(event.pointerId);
      if (event.shiftKey) {
        event.preventDefault();
        this.selectionBox = {
          startX: event.clientX,
          startY: event.clientY,
          x: event.clientX,
          y: event.clientY,
          append: event.ctrlKey || event.metaKey
        };
        this.updateSelectionBoxElement();
        this.canvas.classList.add("selecting");
        return;
      }

      const hit = this.pickNearest(event.clientX, event.clientY);
      if (hit) {
        this.beginNodeDrag(hit, event);
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
      if (this.selectionBox) {
        event.preventDefault();
        this.selectionBox.x = event.clientX;
        this.selectionBox.y = event.clientY;
        this.updateSelectionBoxElement();
        return;
      }

      if (this.dragging) {
        this.dragSelectedNodes(event);
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
      const selectionBox = this.selectionBox;
      this.dragging = null;
      this.pointer = null;
      this.selectionBox = null;
      this.canvas.classList.remove("dragging");
      this.canvas.classList.remove("selecting");
      this.hideSelectionBoxElement();

      if (selectionBox) {
        const moved = Math.hypot(event.clientX - selectionBox.startX, event.clientY - selectionBox.startY);
        if (moved > tapMoveThreshold) {
          this.selectElementsInBox(selectionBox);
        }
        return;
      }

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
            this.selectNode(hit.name ?? "", event, event.pointerType ?? "mouse");
          } else {
            const edgeHit = this.pickNearestEdge(event.clientX, event.clientY);
            if (edgeHit) {
              this.selectEdge(edgeHit, event, event.pointerType ?? "mouse");
            }
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
      this.selectionBox = null;
      this.hideSelectionBoxElement();
      this.canvas.classList.remove("dragging");
      this.canvas.classList.remove("selecting");
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

  beginNodeDrag(hit: any, event: any) {
    this.dragging = {
      name: hit.name,
      names: this.dragNodeNames(hit.name ?? ""),
      x: event.clientX,
      y: event.clientY,
      startX: event.clientX,
      startY: event.clientY,
      pointerType: event.pointerType ?? "mouse"
    };
  }

  dragNodeNames(name: string) {
    if (!this.callbacks.isNodeSelected?.(name)) {
      return [name];
    }

    const selectedNames = this.memory?.nodes
      ?.map(node => node.name ?? "")
      .filter(nodeName => nodeName && this.positions.has(nodeName) && this.callbacks.isNodeSelected?.(nodeName))
      ?? [];
    return selectedNames.length > 0 ? [...new Set(selectedNames)] : [name];
  }

  dragSelectedNodes(event: any) {
    if (!this.dragging) {
      return;
    }

    const dx = (event.clientX - this.dragging.x) / this.view.scale;
    const dy = (event.clientY - this.dragging.y) / this.view.scale;
    const names = this.dragging.names ?? [this.dragging.name];
    let moved = false;

    names.forEach(name => {
      const position = this.positions.get(name);
      if (!position) {
        return;
      }

      position.x += dx;
      position.y += dy;
      moved = true;
    });

    this.dragging.x = event.clientX;
    this.dragging.y = event.clientY;
    if (moved) {
      this.updateDynamicGraph();
    }
  }

  bindCanvasResizeObserver() {
    if (this.resizeObserver || typeof (this.window as unknown as { ResizeObserver: unknown }).ResizeObserver !== "function") {
      return;
    }

    const ResizeObserverCtor = (this.window as unknown as { ResizeObserver: any }).ResizeObserver;
    this.resizeObserver = new ResizeObserverCtor(() => this.scheduleResizeRefresh());
    (this.resizeObserver as any).observe(this.canvas);
  }

  render() {
    const graph = this.buildGraph();
    this.currentGraph = graph;
    this.emptyState.classList.toggle("hidden", graph.nodes.length > 0);
    this.memory = this.buildRenderMemory(graph);
    this.callbacks.renderInspector();
    this.renderLabels();
    this.updateRendererGraph();
    this.requestDraw();
  }

  runSimulation(frames: number, onComplete: (() => void) | null = null) {
    this.stopSimulation();

    if (!this.currentGraph) {
      this.render();
    }

    let remaining = frames;
    const tick = () => {
      this.simulationHandle = null;
      this.simulateStep();
      this.updateDynamicGraph();
      remaining -= 1;
      if (remaining > 0) {
        this.simulationHandle = this.window.requestAnimationFrame(tick);
      } else {
        onComplete?.();
      }
    };

    this.simulationHandle = this.window.requestAnimationFrame(tick);
  }

  stopSimulation() {
    if (this.simulationHandle === null || this.simulationHandle === undefined) {
      return;
    }

    this.window.cancelAnimationFrame(this.simulationHandle);
    this.simulationHandle = null;
  }

  simulateStep() {
    const graph = this.currentGraph ?? this.buildGraph();
    const forces = new Map<string, { x: number; y: number; }>(graph.nodes.map(node => [node.name as string, { x: 0, y: 0 }]));

    graph.edges.forEach(edge => {
      if (edge.collapsed) {
        return;
      }

      const source = this.positions.get(edge.node1InternalId ?? "");
      const target = this.positions.get(edge.node2InternalId ?? "");
      const sourceForce = forces.get(edge.node1InternalId ?? "");
      const targetForce = forces.get(edge.node2InternalId ?? "");
      if (!source || !target || !sourceForce || !targetForce) {
        return;
      }

      let dx = target.x - source.x;
      let dy = target.y - source.y;
      let distance = Math.max(1, Math.hypot(dx, dy));
      const desired = nodeRadius * 3 + Math.min(90, Math.max(0, (Number(edge.viewRank) || 0) * 0.3));
      const strength = (distance - desired) * 0.018;
      const fx = (dx / distance) * strength;
      const fy = (dy / distance) * strength;
      sourceForce.x += fx;
      sourceForce.y += fy;
      targetForce.x -= fx;
      targetForce.y -= fy;
    });

    graph.nodes.forEach(node => {
      const position = this.positions.get(node.name ?? "");
      const velocity = this.velocities.get(node.name ?? "") ?? { x: 0, y: 0 };
      const force = forces.get(node.name ?? "");
      if (!position || !force) {
        return;
      }

      force.x += -position.x * 0.0018;
      force.y += -position.y * 0.0018;
      velocity.x = (velocity.x + force.x) * 0.82;
      velocity.y = (velocity.y + force.y) * 0.82;
      position.x += velocity.x;
      position.y += velocity.y;
      this.velocities.set(node.name ?? "", velocity);
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
      .map(node => this.positions.get(node.name ?? ""))
      .filter(Boolean) as {x: number, y: number}[];
    if (points.length === 0) {
      return;
    }

    const padding = nodeRadius * 2;
    const minX = Math.min(...points.map(point => point.x)) - padding;
    const maxX = Math.max(...points.map(point => point.x)) + padding;
    const minY = Math.min(...points.map(point => point.y)) - padding;
    const maxY = Math.max(...points.map(point => point.y)) + padding;
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

  scheduleResizeRefresh() {
    if (this.resizePending) {
      return;
    }

    this.resizePending = true;
    this.window.requestAnimationFrame(() => {
      this.resizePending = false;
      this.applyView();
    });
  }

  screenToGraph(x: number, y: number) {
    return {
      x: (x - this.view.x) / this.view.scale,
      y: (y - this.view.y) / this.view.scale
    };
  }

  buildRenderMemory(graph: import("../domain/GraphModel.js").ProjectedGraph): import("./GraphRenderer.js").GraphRenderMemory {
    const nodes = graph.nodes.filter(node => this.positions.has(node.name ?? ""));
    const nodeIndexByName = new Map<string, number>(nodes.map((node, index) => [node.name ?? "", index]));
    const edgeMap = new Map<string, import("../domain/GraphModel.js").ProjectedGraphEdge & { sourceIndex?: number; targetIndex?: number }>();

    graph.edges.forEach(edge => {
      if (edge.collapsed) {
        return;
      }

      const source = nodeIndexByName.get(edge.node1InternalId);
      const target = nodeIndexByName.get(edge.node2InternalId);
      if (source === undefined || target === undefined || source === target) {
        return;
      }

      const key = edge.key ?? edgeKey(edge.node1InternalId, edge.node2InternalId, edge.relationGlobalId ?? edge.typeGlobalId ?? "");
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

  writeVertexData(memory: GraphRenderMemory) {
    memory.nodes.forEach((node, index) => {
      const position = this.positions.get(node.name ?? "") ?? { x: 0, y: 0 };
      const color = parseColor(node.color, defaultNodeStrokeColor);
      const radius = (typeof node.viewRadius === "number" && Number.isFinite(node.viewRadius)) ? node.viewRadius : nodeRadius;
      const base = index * 8;
      memory.nodeVertexData[base + 0] = position.x;
      memory.nodeVertexData[base + 1] = position.y;
      memory.nodeVertexData[base + 2] = color[0];
      memory.nodeVertexData[base + 3] = color[1];
      memory.nodeVertexData[base + 4] = color[2];
      memory.nodeVertexData[base + 5] = color[3];
      memory.nodeVertexData[base + 6] = radius;
      memory.nodeVertexData[base + 7] = this.callbacks.isNodeSelected?.(node.name ?? "") ? 1 : 0;
    });

    memory.edges.forEach((edge, index) => {
      const source = this.positions.get(edge.node1InternalId) ?? { x: 0, y: 0 };
      const target = this.positions.get(edge.node2InternalId) ?? { x: 0, y: 0 };
      const selected = this.callbacks.isEdgeSelected?.(edge.key) === true;
      edge.selected = selected;
      const color = selected ? selectedEdgeColor : parseColor(edge.color, defaultEdgeColor);
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

    this.callbacks.syncEdgeAngles?.();
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
        const position = this.positions.get(node.name ?? "");
        if (!position) {
          return false;
        }

        const x = position.x * this.view.scale + this.view.x;
        const y = position.y * this.view.scale + this.view.y;
        const radius = screenNodeRadius(node, this.view.scale);
        const margin = Math.max(80, radius + 40);
        return x >= -margin && x <= rect.width + margin && y >= -margin && y <= rect.height + margin;
      })
      .sort((left, right) =>
        Number(this.callbacks.isNodeSelected(right.name ?? "")) - Number(this.callbacks.isNodeSelected(left.name ?? "")))
      .slice(0, maxLabels);

    labelledNodes.forEach(node => {
      const position = this.positions.get(node.name ?? "");
      if (!position) {
        return;
      }

      const radius = screenNodeRadius(node, this.view.scale);
      const x = position.x * this.view.scale + this.view.x;
      const y = position.y * this.view.scale + this.view.y;
      const selected = this.callbacks.isNodeSelected(node.name ?? "");
      const label = this.document.createElement("div");
      label.className = `graph-node-label${selected ? " selected" : ""}`;
      label.textContent = node.displayName ?? node.localId ?? node.name ?? "";
      label.title = node.path ?? node.name ?? "";
      label.style.width = `${Math.max(28, radius * 2 - 16)}px`;
      label.style.transform = `translate(${x}px, ${y}px) translate(-50%, -50%)`;
      fragment.append(label);
    });

    this.labelLayer.append(fragment);
  }

  renderEdgeEndpointControls(fragment: DocumentFragment, rect: DOMRect) {
    const graph = this.currentGraph ?? this.memory?.graph;
    if (!graph?.edges?.length) {
      return;
    }

    const nodesByName = new Map((graph.nodes ?? []).map(node => [node.name ?? "", node]));
    const rendered = new Set<string>();
    graph.edges.forEach(edge => {
      (edge.controls ?? []).forEach(control => {
        this.renderEdgeEndpointControl(fragment, rect, edge, control, nodesByName, rendered);
      });
    });
  }

  renderEdgeEndpointControl(fragment: DocumentFragment, rect: DOMRect, edge: any, control: any, nodesByName: Map<string, any>, rendered: Set<string>) {
    const anchorName = control?.anchorName;
    const otherName = control?.otherName;
    if (!anchorName || !otherName) {
      return;
    }

    const anchorPosition = this.positions.get(anchorName);
    if (!anchorPosition) {
      return;
    }

    const anchorNode = nodesByName.get(anchorName);
    const radius = screenNodeRadius(anchorNode, this.view.scale);
    const anchor = this.graphToScreen(anchorPosition);
    const point = Number.isFinite(control.angle)
      ? pointAtAngle(anchor, control.angle, radius + endpointControlPadding)
      : this.edgeEndpointPoint(anchor, otherName, radius + endpointControlPadding);
    if (!point) {
      return;
    }
    const x = point.x;
    const y = point.y;
    const margin = 36;
    if (x < -margin || x > rect.width + margin || y < -margin || y > rect.height + margin) {
      return;
    }

    const key = control.key ?? `${edge.key ?? ""}\0${anchorName}\0${otherName}\0${control.kind ?? ""}`;
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
    button.dataset.edgeAction = control.action ?? "";
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
      this.callbacks.activateEdgeControl?.(edge, control);
    });
    fragment.append(button);
  }

  edgeEndpointPoint(anchor: {x: number, y: number}, otherName: string, radius: number) {
    const other = this.positions.get(otherName);
    return other ? pointOnCircle(anchor, this.graphToScreen(other), radius) : null;
  }

  graphToScreen(position: {x: number, y: number}) {
    return {
      x: position.x * this.view.scale + this.view.x,
      y: position.y * this.view.scale + this.view.y
    };
  }

  updateSelectionBoxElement() {
    const box = this.selectionBox;
    const element = this.selectionBoxElement;
    if (!box || !element) {
      return;
    }

    const rect = normalizeClientRect(box);
    element.hidden = false;
    element.style.left = `${rect.left}px`;
    element.style.top = `${rect.top}px`;
    element.style.width = `${rect.right - rect.left}px`;
    element.style.height = `${rect.bottom - rect.top}px`;
  }

  hideSelectionBoxElement() {
    if (this.selectionBoxElement) {
      this.selectionBoxElement.hidden = true;
    }
  }

  selectElementsInBox(box: { startX: number, startY: number, x: number, y: number, append: boolean }) {
    if (!this.memory) {
      return;
    }

    const clientRect = normalizeClientRect(box);
    const canvasRect = this.canvas.getBoundingClientRect();
    const rect = {
      left: clientRect.left - canvasRect.left,
      top: clientRect.top - canvasRect.top,
      right: clientRect.right - canvasRect.left,
      bottom: clientRect.bottom - canvasRect.top
    };
    const selectedNodes: import("../domain/GraphModel.js").ProjectedGraphNode[] = [];
    const selectedEdges: import("../domain/GraphModel.js").ProjectedGraphEdge[] = [];

    this.memory.nodes.forEach(node => {
      const position = this.positions.get(node.name ?? "");
      if (!position) {
        return;
      }

      const center = this.graphToScreen(position);
      const radius = Math.max(4, screenNodeRadius(node, this.view.scale));
      if (circleIntersectsRect(center, radius, rect)) {
        selectedNodes.push(node);
      }
    });

    this.memory.edges.forEach(edge => {
      const source = this.positions.get(edge.node1InternalId ?? "");
      const target = this.positions.get(edge.node2InternalId ?? "");
      if (!source || !target) {
        return;
      }

      if (segmentIntersectsRect(this.graphToScreen(source), this.graphToScreen(target), rect)) {
        selectedEdges.push(edge);
      }
    });

    this.callbacks.selectGraphElements?.(selectedNodes, selectedEdges, box.append);
  }

  pickNearest(clientX: number, clientY: number) {
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
      const position = this.positions.get(node.name ?? "");
      if (!position) {
        continue;
      }

      const sx = position.x * this.view.scale + this.view.x;
      const sy = position.y * this.view.scale + this.view.y;
      const radius = Math.max(12, screenNodeRadius(node, this.view.scale) + 10);
      const distance = (sx - x) ** 2 + (sy - y) ** 2;
      if (distance <= radius ** 2 && distance < bestDistance) {
        bestDistance = distance;
        best = node;
      }
    }

    return best;
  }

  pickNearestEdge(clientX: number, clientY: number) {
    if (!this.memory || this.memory.edgeCount === 0) {
      return null;
    }

    const rect = this.canvas.getBoundingClientRect();
    const point = {
      x: clientX - rect.left,
      y: clientY - rect.top
    };
    let best = null;
    let bestDistance = Infinity;

    for (const edge of this.memory.edges) {
      const source = this.positions.get(edge.node1InternalId);
      const target = this.positions.get(edge.node2InternalId);
      if (!source || !target) {
        continue;
      }

      const distance = pointToSegmentDistance(point, this.graphToScreen(source), this.graphToScreen(target));
      if (distance <= edgePickThreshold && distance < bestDistance) {
        bestDistance = distance;
        best = edge;
      }
    }

    return best;
  }

  selectNode(name: string, event: PointerEvent, pointerType: string) {
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

  selectEdge(edge: any, event: PointerEvent, pointerType: string) {
    if (pointerType !== "mouse") {
      this.callbacks.toggleEdgeSelection?.(edge);
      return;
    }

    if (event.ctrlKey || event.metaKey) {
      this.callbacks.addEdgeToSelection?.(edge);
      return;
    }

    this.callbacks.selectOnlyEdge?.(edge);
  }

  setGpuWarning(error: Error | null) {
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

function parseColor(value: any, fallback: number[]) {
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

function edgeKey(source: string, target: string, discriminator: string) {
  return String(source).localeCompare(String(target), "ru") < 0
    ? `${source}\0${target}\0${discriminator}`
    : `${target}\0${source}\0${discriminator}`;
}

function pointOnCircle(anchor: {x: number, y: number}, target: {x: number, y: number}, radius: number) {
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

function pointAtAngle(anchor: {x: number, y: number}, angle: number, radius: number) {
  return {
    x: anchor.x + Math.cos(angle) * radius,
    y: anchor.y + Math.sin(angle) * radius
  };
}

function screenNodeRadius(node: any, scale: number) {
  const radius = Number.isFinite(node?.viewRadius) ? node.viewRadius : nodeRadius;
  return radius * scale;
}

function normalizeClientRect(box: { startX: number, startY: number, x: number, y: number }) {
  return {
    left: Math.min(box.startX, box.x),
    top: Math.min(box.startY, box.y),
    right: Math.max(box.startX, box.x),
    bottom: Math.max(box.startY, box.y)
  };
}

function circleIntersectsRect(center: {x: number, y: number}, radius: number, rect: any) {
  const closestX = clamp(center.x, rect.left, rect.right);
  const closestY = clamp(center.y, rect.top, rect.bottom);
  return (center.x - closestX) ** 2 + (center.y - closestY) ** 2 <= radius ** 2;
}

function segmentIntersectsRect(a: {x: number, y: number}, b: {x: number, y: number}, rect: any) {
  if (pointInRect(a, rect) || pointInRect(b, rect)) {
    return true;
  }

  const topLeft = { x: rect.left, y: rect.top };
  const topRight = { x: rect.right, y: rect.top };
  const bottomRight = { x: rect.right, y: rect.bottom };
  const bottomLeft = { x: rect.left, y: rect.bottom };
  return segmentsIntersect(a, b, topLeft, topRight)
    || segmentsIntersect(a, b, topRight, bottomRight)
    || segmentsIntersect(a, b, bottomRight, bottomLeft)
    || segmentsIntersect(a, b, bottomLeft, topLeft);
}

function pointInRect(point: {x: number, y: number}, rect: any) {
  return point.x >= rect.left && point.x <= rect.right && point.y >= rect.top && point.y <= rect.bottom;
}

function pointToSegmentDistance(point: {x: number, y: number}, a: {x: number, y: number}, b: {x: number, y: number}) {
  const dx = b.x - a.x;
  const dy = b.y - a.y;
  const lengthSquared = dx * dx + dy * dy;
  if (lengthSquared <= 0.000001) {
    return Math.hypot(point.x - a.x, point.y - a.y);
  }

  const t = clamp(((point.x - a.x) * dx + (point.y - a.y) * dy) / lengthSquared, 0, 1);
  const x = a.x + t * dx;
  const y = a.y + t * dy;
  return Math.hypot(point.x - x, point.y - y);
}

function segmentsIntersect(a: {x: number, y: number}, b: {x: number, y: number}, c: {x: number, y: number}, d: {x: number, y: number}) {
  const abC = cross(a, b, c);
  const abD = cross(a, b, d);
  const cdA = cross(c, d, a);
  const cdB = cross(c, d, b);
  const epsilon = 0.000001;

  if (((abC > epsilon && abD < -epsilon) || (abC < -epsilon && abD > epsilon))
    && ((cdA > epsilon && cdB < -epsilon) || (cdA < -epsilon && cdB > epsilon))) {
    return true;
  }

  return Math.abs(abC) <= epsilon && pointOnSegment(c, a, b)
    || Math.abs(abD) <= epsilon && pointOnSegment(d, a, b)
    || Math.abs(cdA) <= epsilon && pointOnSegment(a, c, d)
    || Math.abs(cdB) <= epsilon && pointOnSegment(b, c, d);
}

function pointOnSegment(point: {x: number, y: number}, a: {x: number, y: number}, b: {x: number, y: number}) {
  const epsilon = 0.000001;
  return point.x >= Math.min(a.x, b.x) - epsilon
    && point.x <= Math.max(a.x, b.x) + epsilon
    && point.y >= Math.min(a.y, b.y) - epsilon
    && point.y <= Math.max(a.y, b.y) + epsilon;
}

function cross(a: {x: number, y: number}, b: {x: number, y: number}, c: {x: number, y: number}) {
  return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
}

function clamp(value: number, min: number, max: number) {
  return Math.min(max, Math.max(min, value));
}

function normalizeRendererMode(mode: string | null) {
  const text = String(mode ?? "").toLowerCase().replace(/[_\s]/g, "-");
  if (text === "svg") {
    return "svg";
  }

  if (text === "html" || text === "htmlcanvas" || text === "html-in-canvas" || text === "html-canvas") {
    return "html-canvas";
  }

  return "webgpu";
}
