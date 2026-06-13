export class Canvas2DRenderer {
  constructor({ canvas, window }) {
    this.canvas = canvas;
    this.window = window;
    this.context = null;
    this.pixelRatio = 1;
    this.memory = null;
    this.mode = "canvas2d";
  }

  async init() {
    this.context = this.canvas.getContext("2d");
    if (!this.context) {
      throw new Error("Canvas 2D context was not created");
    }

    this.resize();
  }

  setGraph(memory) {
    this.memory = memory;
  }

  updateGraph(memory) {
    this.memory = memory;
  }

  resize() {
    const bounds = this.canvas.getBoundingClientRect();
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

  draw(view) {
    if (!this.context) {
      return 0;
    }

    this.resize();
    const start = this.window.performance.now();
    const context = this.context;
    const width = this.canvas.width / this.pixelRatio;
    const height = this.canvas.height / this.pixelRatio;
    context.setTransform(this.pixelRatio, 0, 0, this.pixelRatio, 0, 0);
    context.clearRect(0, 0, width, height);
    context.fillStyle = "#f6f7f9";
    context.fillRect(0, 0, width, height);

    const memory = this.memory;
    if (!memory) {
      return this.window.performance.now() - start;
    }

    this.drawEdges(context, memory, view);
    this.drawNodes(context, memory, view);
    return this.window.performance.now() - start;
  }

  drawEdges(context, memory, view) {
    const data = memory.edgeVertexData;
    context.lineCap = "round";
    context.lineWidth = 1.5;

    for (let index = 0; index < data.length; index += 12) {
      context.strokeStyle = rgba(data[index + 2], data[index + 3], data[index + 4], data[index + 5]);
      context.beginPath();
      context.moveTo(data[index + 0] * view.scale + view.x, data[index + 1] * view.scale + view.y);
      context.lineTo(data[index + 6] * view.scale + view.x, data[index + 7] * view.scale + view.y);
      context.stroke();
    }
  }

  drawNodes(context, memory, view) {
    const data = memory.nodeVertexData;

    for (let index = 0; index < data.length; index += 8) {
      const selected = data[index + 7] > 0.5;
      const radius = data[index + 6] + (selected ? 4 : 0);
      const x = data[index + 0] * view.scale + view.x;
      const y = data[index + 1] * view.scale + view.y;

      context.beginPath();
      context.arc(x, y, radius, 0, Math.PI * 2);
      context.fillStyle = rgba(data[index + 2], data[index + 3], data[index + 4], data[index + 5]);
      context.fill();

      if (selected) {
        context.lineWidth = 3;
        context.strokeStyle = "#f6c447";
        context.stroke();
      }
    }
  }

  dispose() {
    this.memory = null;
  }
}

function rgba(r, g, b, a) {
  return `rgba(${Math.round(r * 255)}, ${Math.round(g * 255)}, ${Math.round(b * 255)}, ${a})`;
}
