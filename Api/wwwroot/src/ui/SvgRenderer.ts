import type { GraphRenderer, GraphRendererHost, GraphRenderMemory, GraphView } from "./GraphRenderer.js";

const svgNs = "http://www.w3.org/2000/svg";

export class SvgRenderer implements GraphRenderer {
  [key: string]: any;

  mode = "svg";

  constructor({ document, window, surface }: GraphRendererHost) {
    this.document = document;
    this.window = window;
    this.surface = surface;
    this.memory = null;
    this.width = 0;
    this.height = 0;

    this.svg = this.document.createElementNS(svgNs, "svg");
    this.svg.classList.add("graph-render-layer", "graph-svg-layer");
    this.svg.setAttribute("aria-hidden", "true");
    this.svg.setAttribute("focusable", "false");

    this.edgeLayer = this.document.createElementNS(svgNs, "g");
    this.edgeLayer.classList.add("graph-svg-edges");
    this.nodeLayer = this.document.createElementNS(svgNs, "g");
    this.nodeLayer.classList.add("graph-svg-nodes");
    this.svg.append(this.edgeLayer, this.nodeLayer);
    this.surface.append(this.svg);
  }

  async init() {
    this.resize();
  }

  setGraph(memory: GraphRenderMemory | null) {
    this.memory = memory;
  }

  updateGraph(memory: GraphRenderMemory | null) {
    this.memory = memory;
  }

  draw(view: GraphView) {
    this.resize();
    const start = this.window.performance.now();
    const memory = this.memory;
    if (!memory) {
      this.edgeLayer.replaceChildren();
      this.nodeLayer.replaceChildren();
      return this.window.performance.now() - start;
    }

    this.drawEdges(memory, view);
    this.drawNodes(memory, view);
    return this.window.performance.now() - start;
  }

  drawEdges(memory: GraphRenderMemory, view: GraphView) {
    const fragment = this.document.createDocumentFragment();
    const data = memory.edgeVertexData;

    for (let index = 0; index < data.length; index += 12) {
      const line = this.createSvg("line", {
        class: "graph-svg-edge",
        x1: data[index + 0] * view.scale + view.x,
        y1: data[index + 1] * view.scale + view.y,
        x2: data[index + 6] * view.scale + view.x,
        y2: data[index + 7] * view.scale + view.y,
        stroke: rgba(data[index + 2], data[index + 3], data[index + 4], data[index + 5])
      });
      fragment.append(line);
    }

    this.edgeLayer.replaceChildren(fragment);
  }

  drawNodes(memory: GraphRenderMemory, view: GraphView) {
    const fragment = this.document.createDocumentFragment();
    const data = memory.nodeVertexData;

    for (let index = 0; index < data.length; index += 8) {
      const node = memory.nodes[index / 8];
      const selected = data[index + 7] > 0.5;
      const radius = (data[index + 6] + (selected ? 4 : 0)) * view.scale;
      const group = this.createSvg("g", {
        class: `graph-svg-node${selected ? " selected" : ""}`,
        transform: `translate(${data[index + 0] * view.scale + view.x} ${data[index + 1] * view.scale + view.y})`
      });
      const title = this.createSvg("title", {});
      title.textContent = node?.globalId ?? node?.name ?? "";
      const circle = this.createSvg("circle", {
        class: "graph-svg-node-shell",
        r: radius,
        cx: 0,
        cy: 0,
        stroke: selected
          ? "#f6c447"
          : rgba(data[index + 2], data[index + 3], data[index + 4], data[index + 5])
      });
      group.append(title, circle);
      fragment.append(group);
    }

    this.nodeLayer.replaceChildren(fragment);
  }

  resize() {
    const bounds = this.surface.getBoundingClientRect();
    const width = Math.max(1, Math.floor(bounds.width));
    const height = Math.max(1, Math.floor(bounds.height));
    if (this.width === width && this.height === height) {
      return false;
    }

    this.width = width;
    this.height = height;
    this.svg.setAttribute("viewBox", `0 0 ${width} ${height}`);
    this.svg.setAttribute("width", String(width));
    this.svg.setAttribute("height", String(height));
    return true;
  }

  dispose() {
    this.memory = null;
    this.svg.remove();
  }

  createSvg(name: string, attrs: Record<string, unknown>) {
    const element = this.document.createElementNS(svgNs, name);
    Object.entries(attrs).forEach(([key, value]) => {
      if (value !== null && value !== undefined) {
        element.setAttribute(key, String(value));
      }
    });
    return element;
  }
}

function rgba(r: number, g: number, b: number, a: number) {
  return `rgba(${Math.round(clamp(r) * 255)}, ${Math.round(clamp(g) * 255)}, ${Math.round(clamp(b) * 255)}, ${clamp(a)})`;
}

function clamp(value: number) {
  return Math.min(1, Math.max(0, value));
}
