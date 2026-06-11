import { nodeRadius, svgNs } from "../domain/graphAttributes.js";

const tapMoveThreshold = 8;
const nodeLabelBaseFontSize = 13;
const nodeLabelMinFontSize = 6;
const nodeLabelHorizontalPadding = 9;

export class GraphCanvas {
  [key: string]: any;

  constructor({
    document,
    window,
    graph,
    svg,
    viewport,
    emptyState,
    displayName,
    edgeEndpointDisplayName,
    handleEndpointClick,
    renderInspector,
    formatRank
  }) {
    this.document = document;
    this.window = window;
    this.graph = graph;
    this.svg = svg;
    this.viewport = viewport;
    this.emptyState = emptyState;
    this.callbacks = {
      displayName,
      edgeEndpointDisplayName,
      handleEndpointClick,
      renderInspector,
      formatRank
    };
  }

  buildGraph() {
    return this.graph.visibleGraph();
  }

  displayName(globalId) {
    return this.callbacks.displayName(globalId);
  }

  edgeEndpointDisplayName(edge, globalId) {
    return this.callbacks.edgeEndpointDisplayName(edge, globalId);
  }

  handleEndpointClick(edge, anchorName) {
    this.callbacks.handleEndpointClick(edge, anchorName);
  }

  renderInspector(graph) {
    this.callbacks.renderInspector(graph);
  }

  formatRank(value) {
    return this.callbacks.formatRank(value);
  }

  bindGraphSurface() {
    this.svg.addEventListener("pointerdown", event => {
      if (event.button !== 0 || event.target.closest(".node") || event.target.closest(".edge-button")) {
        return;
      }

      this.svg.setPointerCapture(event.pointerId);
      this.svg.classList.add("dragging");
      this.graph.pointer = { x: event.clientX, y: event.clientY };
    });

    this.svg.addEventListener("pointermove", event => {
      if (this.graph.dragging) {
        const position = this.graph.positions.get(this.graph.dragging.name);
        if (!position) {
          return;
        }

        const dx = (event.clientX - this.graph.dragging.x) / this.graph.view.scale;
        const dy = (event.clientY - this.graph.dragging.y) / this.graph.view.scale;
        position.x += dx;
        position.y += dy;
        this.graph.dragging.x = event.clientX;
        this.graph.dragging.y = event.clientY;
        this.render();
        return;
      }

      if (!this.graph.pointer) {
        return;
      }

      const dx = event.clientX - this.graph.pointer.x;
      const dy = event.clientY - this.graph.pointer.y;
      this.graph.view.x += dx;
      this.graph.view.y += dy;
      this.graph.pointer = { x: event.clientX, y: event.clientY };
      this.applyView();
    });

    this.svg.addEventListener("pointerup", event => {
      if (this.graph.dragging) {
        const dragging = this.graph.dragging;
        const moved = Math.hypot(event.clientX - dragging.startX, event.clientY - dragging.startY);
        this.svg.releasePointerCapture(event.pointerId);
        this.graph.dragging = null;
        if (dragging.pointerType !== "mouse") {
          this.graph.suppressedNodeClick = { name: dragging.name, until: Date.now() + 700 };
          if (moved <= tapMoveThreshold) {
            this.toggleNodeSelection(dragging.name);
          }
        }
        return;
      }

      if (this.graph.pointer) {
        this.svg.releasePointerCapture(event.pointerId);
      }

      this.graph.pointer = null;
      this.svg.classList.remove("dragging");
    });

    this.svg.addEventListener("wheel", event => {
      event.preventDefault();
      const rect = this.svg.getBoundingClientRect();
      const mouseX = event.clientX - rect.left;
      const mouseY = event.clientY - rect.top;
      const before = this.screenToGraph(mouseX, mouseY);
      const scale = Math.min(2.8, Math.max(0.25, this.graph.view.scale * Math.exp(-event.deltaY * 0.0012)));
      this.graph.view.scale = scale;
      this.graph.view.x = mouseX - before.x * scale;
      this.graph.view.y = mouseY - before.y * scale;
      this.applyView();
    }, { passive: false });
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
  const sourceLoaded = this.graph.loaded.has(edge.sourceGlobalId);
  const targetLoaded = this.graph.loaded.has(edge.targetGlobalId);
  const source = this.graph.positions.get(edge.sourceGlobalId);
  const target = this.graph.positions.get(edge.targetGlobalId);

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
  const position = this.graph.positions.get(node.name);
  if (!position) {
    return;
  }

  const group = this.createSvg("g", {
    class: `node${this.isNodeSelected(node.name) ? " selected" : ""}`,
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
  const radius = node.viewRadius ?? nodeRadius;
  const label = this.createSvg("text", { class: "node-label", x: 0, y: 0 });
  label.textContent = node.displayName ?? node.name;

  group.addEventListener("click", event => {
    event.stopPropagation();
    if (this.consumeSuppressedNodeClick(node.name)) {
      event.preventDefault();
      return;
    }

    const pointerType = this.window.PointerEvent && event instanceof this.window.PointerEvent
      ? event.pointerType
      : "mouse";
    if (pointerType !== "mouse") {
      this.toggleNodeSelection(node.name);
      return;
    }

    if (event.ctrlKey) {
      this.addNodeToSelection(node.name);
      return;
    }

    this.selectOnlyNode(node.name);
  });

  group.addEventListener("pointerdown", event => {
    event.stopPropagation();
    this.svg.setPointerCapture(event.pointerId);
    this.graph.dragging = {
      name: node.name,
      x: event.clientX,
      y: event.clientY,
      startX: event.clientX,
      startY: event.clientY,
      pointerType: event.pointerType ?? "mouse"
    };
  });

  group.append(title, circle, label);
  layer.append(group);
  this.fitNodeLabel(label, radius);

  }

  fitNodeLabel(label, radius) {
  const maxWidth = Math.max(10, radius * 2 - nodeLabelHorizontalPadding * 2);
  label.style.fontSize = `${nodeLabelBaseFontSize}px`;
  label.removeAttribute("textLength");
  label.removeAttribute("lengthAdjust");

  const width = this.measureSvgText(label);
  if (!width || width <= maxWidth) {
    return;
  }

  const fontSize = Math.max(nodeLabelMinFontSize, nodeLabelBaseFontSize * maxWidth / width);
  label.style.fontSize = `${fontSize.toFixed(2)}px`;

  if (this.measureSvgText(label) > maxWidth) {
    label.setAttribute("textLength", maxWidth.toFixed(2));
    label.setAttribute("lengthAdjust", "spacingAndGlyphs");
  }

  }

  measureSvgText(label) {
  if (typeof label.getComputedTextLength === "function") {
    return label.getComputedTextLength();
  }

  if (typeof label.getBBox === "function") {
    return label.getBBox().width;
  }

  return 0;

  }

  isNodeSelected(name) {
  return this.graph.isSelectedName ? this.graph.isSelectedName(name) : this.graph.selectedName === name;

  }

  selectOnlyNode(name) {
  this.graph.selectedName = name;
  this.render();

  }

  addNodeToSelection(name) {
  if (this.graph.addSelectedName) {
    this.graph.addSelectedName(name);
  } else {
    this.graph.selectedName = name;
  }
  this.render();

  }

  toggleNodeSelection(name) {
  if (this.graph.toggleSelectedName) {
    this.graph.toggleSelectedName(name);
  } else {
    this.graph.selectedName = this.graph.selectedName === name ? null : name;
  }
  this.render();

  }

  consumeSuppressedNodeClick(name) {
  const suppressed = this.graph.suppressedNodeClick;
  if (!suppressed || suppressed.name !== name || suppressed.until < Date.now()) {
    return false;
  }

  this.graph.suppressedNodeClick = null;
  return true;

  }

  runSimulation(frames) {
  if (this.graph.simulationHandle) {
    this.window.cancelAnimationFrame(this.graph.simulationHandle);
  }

  let remaining = frames;
  const tick = () => {
    this.simulateStep();
    this.render();
    remaining -= 1;
    if (remaining > 0) {
      this.graph.simulationHandle = this.window.requestAnimationFrame(tick);
    }
  };

  this.graph.simulationHandle = this.window.requestAnimationFrame(tick);

  }

  simulateStep() {
  const graph = this.buildGraph();
  const nodes = graph.nodes;
  const forces = new Map(nodes.map(node => [node.name, { x: 0, y: 0 }]));

  for (let i = 0; i < nodes.length; i += 1) {
    for (let j = i + 1; j < nodes.length; j += 1) {
      const a = nodes[i];
      const b = nodes[j];
      const pa = this.graph.positions.get(a.name);
      const pb = this.graph.positions.get(b.name);
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
    .filter(edge => this.graph.loaded.has(edge.sourceGlobalId) && this.graph.loaded.has(edge.targetGlobalId))
    .forEach(edge => {
      const source = this.graph.positions.get(edge.sourceGlobalId);
      const target = this.graph.positions.get(edge.targetGlobalId);
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
    const position = this.graph.positions.get(node.name);
    const velocity = this.graph.velocities.get(node.name) ?? { x: 0, y: 0 };
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
    this.graph.velocities.set(node.name, velocity);
  });

  }

  fitView() {
  const graph = this.buildGraph();
  if (graph.nodes.length === 0) {
    this.graph.view = { x: 0, y: 0, scale: 1 };
    this.applyView();
    return;
  }

  const rect = this.svg.getBoundingClientRect();
  const points = graph.nodes
    .map(node => this.graph.positions.get(node.name))
    .filter(Boolean);
  const minX = Math.min(...points.map(point => point.x)) - 120;
  const maxX = Math.max(...points.map(point => point.x)) + 120;
  const minY = Math.min(...points.map(point => point.y)) - 120;
  const maxY = Math.max(...points.map(point => point.y)) + 120;
  const width = Math.max(1, maxX - minX);
  const height = Math.max(1, maxY - minY);
  const scale = Math.min(1.8, Math.max(0.32, Math.min(rect.width / width, rect.height / height)));

  this.graph.view.scale = scale;
  this.graph.view.x = rect.width / 2 - ((minX + maxX) / 2) * scale;
  this.graph.view.y = rect.height / 2 - ((minY + maxY) / 2) * scale;
  this.applyView();

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
  this.viewport.setAttribute("transform", `translate(${this.graph.view.x} ${this.graph.view.y}) scale(${this.graph.view.scale})`);

  }

  screenToGraph(x, y) {
  return {
    x: (x - this.graph.view.x) / this.graph.view.scale,
    y: (y - this.graph.view.y) / this.graph.view.scale
  };

  }

  createSvg(name, attrs) {
  const element = this.document.createElementNS(svgNs, name);
  Object.entries(attrs).forEach(([key, value]) => element.setAttribute(key, value));
  return element;

  }
}
