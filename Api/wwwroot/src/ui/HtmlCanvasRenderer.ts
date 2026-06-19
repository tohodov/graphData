import type { GraphRenderer, GraphRendererHost, GraphRenderMemory, GraphView } from "./GraphRenderer.js";

type HtmlCanvasElement = HTMLCanvasElement & {
  layoutSubtree?: boolean;
  requestPaint?: () => void;
};

type HtmlCanvasContext2D = CanvasRenderingContext2D & {
  drawElementImage?: (
    element: Element,
    dx: number,
    dy: number,
    dwidth?: number,
    dheight?: number
  ) => DOMMatrix;
  reset?: () => void;
};

export class HtmlCanvasRenderer implements GraphRenderer {
  [key: string]: any;

  mode = "html-canvas";

  constructor({ document, window, surface }: GraphRendererHost) {
    this.document = document;
    this.window = window;
    this.surface = surface;
    this.canvas = this.document.createElement("canvas") as HtmlCanvasElement;
    this.canvas.classList.add("graph-render-layer", "graph-html-canvas-layer");
    this.canvas.setAttribute("aria-hidden", "true");
    this.canvas.setAttribute("layoutsubtree", "");
    this.canvas.layoutSubtree = true;
    this.context = null;
    this.memory = null;
    this.nodeElements = [];
    this.edgeElements = [];
    this.nodeKeys = [];
    this.edgeKeys = [];
    this.pixelRatio = 1;
    this.pendingView = { x: 0, y: 0, scale: 1 };
    this.paintListener = () => this.paint();

    this.surface.append(this.canvas);
  }

  async init() {
    this.context = this.canvas.getContext("2d") as HtmlCanvasContext2D | null;
    if (!this.context) {
      throw new Error("HTML-in-Canvas renderer requires a 2D canvas context.");
    }

    if (typeof this.context.drawElementImage !== "function"
      || typeof this.canvas.requestPaint !== "function") {
      throw new Error(
        "HTML-in-Canvas is unavailable: enable chrome://flags/#canvas-draw-element in Chromium.");
    }

    this.canvas.addEventListener("paint", this.paintListener);
    this.resize();
  }

  setGraph(memory: GraphRenderMemory | null) {
    this.memory = memory;
    this.nodeElements = [];
    this.edgeElements = [];
    this.nodeKeys = [];
    this.edgeKeys = [];
    this.canvas.replaceChildren();

    if (!memory) {
      return;
    }

    const fragment = this.document.createDocumentFragment();
    memory.edges.forEach((edge, index) => {
      const element = this.document.createElement("div");
      element.className = "html-canvas-edge";
      this.edgeElements.push(element);
      this.edgeKeys.push(this.edgeKey(edge));
      fragment.append(element);
      this.updateEdgeElement(memory, index);
    });

    memory.nodes.forEach((node, index) => {
      const element = this.document.createElement("div");
      element.className = "html-canvas-node";
      element.title = node?.globalId ?? node?.name ?? "";
      this.nodeElements.push(element);
      this.nodeKeys.push(node?.name ?? String(index));
      fragment.append(element);
      this.updateNodeElement(memory, index);
    });

    this.canvas.append(fragment);
  }

  updateGraph(memory: GraphRenderMemory | null) {
    if (!memory || !this.hasSameElementKeys(memory)) {
      this.setGraph(memory);
      return;
    }

    this.memory = memory;
    memory.edges.forEach((_, index) => this.updateEdgeElement(memory, index));
    memory.nodes.forEach((_, index) => this.updateNodeElement(memory, index));
  }

  draw(view: GraphView) {
    this.resize();
    this.pendingView = { ...view };
    const start = this.window.performance.now();
    this.canvas.requestPaint?.();
    return this.window.performance.now() - start;
  }

  paint() {
    const memory = this.memory;
    const context = this.context;
    if (!memory || !context || typeof context.drawElementImage !== "function") {
      return;
    }

    this.resize();
    const view = this.pendingView;
    if (typeof context.reset === "function") {
      context.reset();
    } else {
      context.setTransform(1, 0, 0, 1, 0, 0);
      context.clearRect(0, 0, this.canvas.width, this.canvas.height);
    }
    context.setTransform(this.pixelRatio, 0, 0, this.pixelRatio, 0, 0);

    for (let index = 0; index < memory.edgeVertexData.length; index += 12) {
      const element = this.edgeElements[index / 12];
      if (!element) {
        continue;
      }

      const x1 = memory.edgeVertexData[index + 0] * view.scale + view.x;
      const y1 = memory.edgeVertexData[index + 1] * view.scale + view.y;
      const x2 = memory.edgeVertexData[index + 6] * view.scale + view.x;
      const y2 = memory.edgeVertexData[index + 7] * view.scale + view.y;
      const length = Math.hypot(x2 - x1, y2 - y1);
      if (length < 0.5) {
        continue;
      }

      const selected = Boolean(memory.edges[index / 12]?.selected);
      const thickness = selected ? 3.5 : 1.5;
      context.save();
      context.translate(x1, y1);
      context.rotate(Math.atan2(y2 - y1, x2 - x1));
      const transform = context.drawElementImage(element, 0, -thickness / 2, length, thickness);
      element.style.transform = transform?.toString?.() ?? "";
      context.restore();
    }

    for (let index = 0; index < memory.nodeVertexData.length; index += 8) {
      const element = this.nodeElements[index / 8];
      if (!element) {
        continue;
      }

      const selected = memory.nodeVertexData[index + 7] > 0.5;
      const radius = (memory.nodeVertexData[index + 6] + (selected ? 4 : 0)) * view.scale;
      const diameter = radius * 2;
      const x = memory.nodeVertexData[index + 0] * view.scale + view.x - radius;
      const y = memory.nodeVertexData[index + 1] * view.scale + view.y - radius;
      const transform = context.drawElementImage(element, x, y, diameter, diameter);
      element.style.transform = transform?.toString?.() ?? "";
    }
  }

  resize() {
    const bounds = this.surface.getBoundingClientRect();
    this.pixelRatio = Math.max(1, Math.min(this.window.devicePixelRatio || 1, 2));
    const width = Math.max(1, Math.floor(bounds.width * this.pixelRatio));
    const height = Math.max(1, Math.floor(bounds.height * this.pixelRatio));
    if (this.canvas.width === width && this.canvas.height === height) {
      return false;
    }

    this.canvas.width = width;
    this.canvas.height = height;
    return true;
  }

  dispose() {
    this.canvas.removeEventListener("paint", this.paintListener);
    this.memory = null;
    this.canvas.remove();
  }

  hasSameElementKeys(memory: GraphRenderMemory) {
    if (this.nodeKeys.length !== memory.nodes.length || this.edgeKeys.length !== memory.edges.length) {
      return false;
    }

    return memory.nodes.every((node, index) => this.nodeKeys[index] === (node?.name ?? String(index)))
      && memory.edges.every((edge, index) => this.edgeKeys[index] === this.edgeKey(edge));
  }

  updateNodeElement(memory: GraphRenderMemory, index: number) {
    const element = this.nodeElements[index];
    if (!element) {
      return;
    }

    const base = index * 8;
    const selected = memory.nodeVertexData[base + 7] > 0.5;
    const radius = memory.nodeVertexData[base + 6] + (selected ? 4 : 0);
    const diameter = `${radius * 2}px`;
    element.classList.toggle("selected", selected);
    element.style.width = diameter;
    element.style.height = diameter;
    element.style.setProperty("--node-stroke", rgba(
      memory.nodeVertexData[base + 2],
      memory.nodeVertexData[base + 3],
      memory.nodeVertexData[base + 4],
      memory.nodeVertexData[base + 5]));
  }

  updateEdgeElement(memory: GraphRenderMemory, index: number) {
    const element = this.edgeElements[index];
    if (!element) {
      return;
    }

    const base = index * 12;
    element.classList.toggle("selected", Boolean(memory.edges[index]?.selected));
    element.style.setProperty("--edge-color", rgba(
      memory.edgeVertexData[base + 2],
      memory.edgeVertexData[base + 3],
      memory.edgeVertexData[base + 4],
      memory.edgeVertexData[base + 5]));
  }

  edgeKey(edge) {
    return edge?.key
      ?? `${edge?.sourceGlobalId ?? ""}\0${edge?.targetGlobalId ?? ""}\0${edge?.relationGlobalId ?? edge?.typeGlobalId ?? ""}`;
  }
}

function rgba(r: number, g: number, b: number, a: number) {
  return `rgba(${Math.round(clamp(r) * 255)}, ${Math.round(clamp(g) * 255)}, ${Math.round(clamp(b) * 255)}, ${clamp(a)})`;
}

function clamp(value: number) {
  return Math.min(1, Math.max(0, value));
}
