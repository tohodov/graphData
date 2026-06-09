import {
  svgNs,
  nodeRadius,
  endpointOffset,
  defaultBasis,
  graphKindAttribute,
  graphElementAttribute,
  graphRoleAttribute,
  graphTypeNameAttribute,
  projectionVisibleAttribute,
  projectionColorAttribute,
  projectionInfoAttribute,
  projectionDirectedAttribute,
  projectionLabelVisibleAttribute,
  projectionRankAttribute
} from "../domain/graphConstants.js";

export const graphLayoutSimulation = {
  seedPosition(name, fromName, index) {
  if (this.state.positions.has(name)) {
    return;
  }

  if (!fromName || !this.state.positions.has(fromName)) {
    this.state.positions.set(name, { x: 0, y: 0 });
    this.state.velocities.set(name, { x: 0, y: 0 });
    return;
  }

  const source = this.state.positions.get(fromName);
  const angle = index * 2.399963 + [...name].reduce((sum, char) => sum + char.charCodeAt(0), 0) * 0.017;
  const distance = 92;
  this.state.positions.set(name, {
    x: source.x + Math.cos(angle) * distance,
    y: source.y + Math.sin(angle) * distance
  });
  this.state.velocities.set(name, { x: 0, y: 0 });

  },

  seedSubgraphPosition(name, index, count) {
  const radius = Math.max(120, Math.min(320, count * 32));
  const angle = count <= 1 ? 0 : (Math.PI * 2 * index) / count;
  this.state.positions.set(name, {
    x: Math.cos(angle) * radius,
    y: Math.sin(angle) * radius
  });
  this.state.velocities.set(name, { x: 0, y: 0 });

  },

  runSimulation(frames) {
  if (this.state.simulationHandle) {
    cancelAnimationFrame(this.state.simulationHandle);
  }

  let remaining = frames;
  const tick = () => {
    this.simulateStep();
    this.render();
    remaining -= 1;
    if (remaining > 0) {
      this.state.simulationHandle = requestAnimationFrame(tick);
    }
  };

  this.state.simulationHandle = requestAnimationFrame(tick);

  },

  simulateStep() {
  const graph = this.buildGraph();
  const nodes = graph.nodes;
  const forces = new Map(nodes.map(node => [node.name, { x: 0, y: 0 }]));

  for (let i = 0; i < nodes.length; i += 1) {
    for (let j = i + 1; j < nodes.length; j += 1) {
      const a = nodes[i];
      const b = nodes[j];
      const pa = this.state.positions.get(a.name);
      const pb = this.state.positions.get(b.name);
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
    .filter(edge => this.state.loaded.has(edge.sourceGlobalId) && this.state.loaded.has(edge.targetGlobalId))
    .forEach(edge => {
      const source = this.state.positions.get(edge.sourceGlobalId);
      const target = this.state.positions.get(edge.targetGlobalId);
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
    const position = this.state.positions.get(node.name);
    const velocity = this.state.velocities.get(node.name) ?? { x: 0, y: 0 };
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
    this.state.velocities.set(node.name, velocity);
  });

  },

  fitView() {
  const graph = this.buildGraph();
  if (graph.nodes.length === 0) {
    this.state.view = { x: 0, y: 0, scale: 1 };
    this.applyView();
    return;
  }

  const rect = this.svg.getBoundingClientRect();
  const points = graph.nodes
    .map(node => this.state.positions.get(node.name))
    .filter(Boolean);
  const minX = Math.min(...points.map(point => point.x)) - 120;
  const maxX = Math.max(...points.map(point => point.x)) + 120;
  const minY = Math.min(...points.map(point => point.y)) - 120;
  const maxY = Math.max(...points.map(point => point.y)) + 120;
  const width = Math.max(1, maxX - minX);
  const height = Math.max(1, maxY - minY);
  const scale = Math.min(1.8, Math.max(0.32, Math.min(rect.width / width, rect.height / height)));

  this.state.view.scale = scale;
  this.state.view.x = rect.width / 2 - ((minX + maxX) / 2) * scale;
  this.state.view.y = rect.height / 2 - ((minY + maxY) / 2) * scale;
  this.applyView();

  },

  applyView() {
  this.viewport.setAttribute("transform", `translate(${this.state.view.x} ${this.state.view.y}) scale(${this.state.view.scale})`);

  },

  screenToGraph(x, y) {
  return {
    x: (x - this.state.view.x) / this.state.view.scale,
    y: (y - this.state.view.y) / this.state.view.scale
  };

  }
};
