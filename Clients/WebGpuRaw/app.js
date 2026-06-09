const canvas = document.querySelector("#graph-canvas");
const loadForm = document.querySelector("#load-form");
const rootsInput = document.querySelector("#roots-input");
const depthInput = document.querySelector("#depth-input");
const loadButton = document.querySelector("#load-button");
const fitButton = document.querySelector("#fit-button");
const resetButton = document.querySelector("#reset-button");
const statusOutput = document.querySelector("#status");
const gpuWarning = document.querySelector("#gpu-warning");
const loadSelectedButton = document.querySelector("#load-selected-button");

const statNodes = document.querySelector("#stat-nodes");
const statEdges = document.querySelector("#stat-edges");
const statMemory = document.querySelector("#stat-memory");
const statFrame = document.querySelector("#stat-frame");
const selectedTitle = document.querySelector("#selected-title");
const selectedGlobal = document.querySelector("#selected-global");
const selectedDegree = document.querySelector("#selected-degree");
const attributeList = document.querySelector("#attribute-list");
const pipelineData = document.querySelector("#pipeline-data");
const pipelineLayout = document.querySelector("#pipeline-layout");
const pipelineRenderer = document.querySelector("#pipeline-renderer");

const state = {
  renderer: null,
  memory: null,
  selectedIndex: -1,
  view: { x: 0, y: 0, scale: 1 },
  pointer: null,
  renderPending: false,
  abort: null
};

const edgeColor = [0.64, 0.67, 0.62, 0.16];
const palette = [
  [0.47, 0.82, 0.64],
  [0.91, 0.71, 0.38],
  [0.57, 0.73, 0.95],
  [0.88, 0.52, 0.47],
  [0.75, 0.62, 0.91],
  [0.78, 0.80, 0.54],
  [0.44, 0.76, 0.78],
  [0.94, 0.60, 0.75]
];

const nodeShader = `
struct Uniforms {
  viewport: vec2<f32>,
  scale: f32,
  pixelRatio: f32,
  offset: vec2<f32>,
  _pad: vec2<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: Uniforms;

struct VertexOut {
  @builtin(position) position: vec4<f32>,
  @location(0) color: vec4<f32>,
  @location(1) unit: vec2<f32>,
  @location(2) selected: f32,
};

@vertex
fn vs(
  @builtin(vertex_index) vertexIndex: u32,
  @location(0) world: vec2<f32>,
  @location(1) color: vec4<f32>,
  @location(2) size: f32,
  @location(3) flags: f32
) -> VertexOut {
  var corners = array<vec2<f32>, 6>(
    vec2<f32>(-1.0, -1.0),
    vec2<f32>(1.0, -1.0),
    vec2<f32>(-1.0, 1.0),
    vec2<f32>(-1.0, 1.0),
    vec2<f32>(1.0, -1.0),
    vec2<f32>(1.0, 1.0)
  );

  let unit = corners[vertexIndex];
  let selected = select(0.0, 1.0, flags > 0.5);
  let radius = size + selected * 4.0;
  let css = world * uniforms.scale + uniforms.offset + unit * radius;
  let screen = css * uniforms.pixelRatio;
  let clip = vec2<f32>(
    screen.x / uniforms.viewport.x * 2.0 - 1.0,
    1.0 - screen.y / uniforms.viewport.y * 2.0
  );

  var out: VertexOut;
  out.position = vec4<f32>(clip, 0.0, 1.0);
  out.color = color;
  out.unit = unit;
  out.selected = selected;
  return out;
}

@fragment
fn fs(in: VertexOut) -> @location(0) vec4<f32> {
  let distance = length(in.unit);
  if (distance > 1.0) {
    discard;
  }

  let fade = smoothstep(1.0, 0.78, distance);
  if (in.selected > 0.5 && distance > 0.70) {
    return vec4<f32>(1.0, 0.86, 0.38, fade);
  }

  return vec4<f32>(in.color.rgb, in.color.a * fade);
}
`;

const edgeShader = `
struct Uniforms {
  viewport: vec2<f32>,
  scale: f32,
  pixelRatio: f32,
  offset: vec2<f32>,
  _pad: vec2<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: Uniforms;

struct VertexOut {
  @builtin(position) position: vec4<f32>,
  @location(0) color: vec4<f32>,
};

@vertex
fn vs(
  @location(0) world: vec2<f32>,
  @location(1) color: vec4<f32>
) -> VertexOut {
  let css = world * uniforms.scale + uniforms.offset;
  let screen = css * uniforms.pixelRatio;
  let clip = vec2<f32>(
    screen.x / uniforms.viewport.x * 2.0 - 1.0,
    1.0 - screen.y / uniforms.viewport.y * 2.0
  );

  var out: VertexOut;
  out.position = vec4<f32>(clip, 0.0, 1.0);
  out.color = color;
  return out;
}

@fragment
fn fs(in: VertexOut) -> @location(0) vec4<f32> {
  return in.color;
}
`;

class WebGpuRenderer {
  constructor(targetCanvas) {
    this.canvas = targetCanvas;
    this.device = null;
    this.context = null;
    this.format = null;
    this.pixelRatio = 1;
    this.uniformBuffer = null;
    this.uniformBindGroup = null;
    this.nodeBuffer = null;
    this.edgeBuffer = null;
    this.nodeCount = 0;
    this.edgeVertexCount = 0;
    this.nodePipeline = null;
    this.edgePipeline = null;
  }

  async init() {
    if (!navigator.gpu) {
      throw new Error("WebGPU is not available");
    }

    const adapter = await navigator.gpu.requestAdapter({ powerPreference: "high-performance" });
    if (!adapter) {
      throw new Error("WebGPU adapter was not found");
    }

    this.device = await adapter.requestDevice();
    this.context = this.canvas.getContext("webgpu");
    this.format = navigator.gpu.getPreferredCanvasFormat();
    this.uniformBuffer = this.device.createBuffer({
      size: 32,
      usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST
    });

    const uniformBindGroupLayout = this.device.createBindGroupLayout({
      entries: [{
        binding: 0,
        visibility: GPUShaderStage.VERTEX,
        buffer: { type: "uniform" }
      }]
    });
    const pipelineLayout = this.device.createPipelineLayout({
      bindGroupLayouts: [uniformBindGroupLayout]
    });
    this.uniformBindGroup = this.device.createBindGroup({
      layout: uniformBindGroupLayout,
      entries: [{ binding: 0, resource: { buffer: this.uniformBuffer } }]
    });

    this.edgePipeline = this.createEdgePipeline(pipelineLayout);
    this.nodePipeline = this.createNodePipeline(pipelineLayout);
    this.resize();
  }

  createEdgePipeline(layout) {
    return this.device.createRenderPipeline({
      label: "edge-pipeline",
      layout,
      vertex: {
        module: this.device.createShaderModule({ code: edgeShader }),
        entryPoint: "vs",
        buffers: [{
          arrayStride: 24,
          attributes: [
            { shaderLocation: 0, offset: 0, format: "float32x2" },
            { shaderLocation: 1, offset: 8, format: "float32x4" }
          ]
        }]
      },
      fragment: {
        module: this.device.createShaderModule({ code: edgeShader }),
        entryPoint: "fs",
        targets: [{
          format: this.format,
          blend: {
            color: {
              srcFactor: "src-alpha",
              dstFactor: "one-minus-src-alpha",
              operation: "add"
            },
            alpha: {
              srcFactor: "one",
              dstFactor: "one-minus-src-alpha",
              operation: "add"
            }
          }
        }]
      },
      primitive: {
        topology: "line-list"
      }
    });
  }

  createNodePipeline(layout) {
    return this.device.createRenderPipeline({
      label: "node-pipeline",
      layout,
      vertex: {
        module: this.device.createShaderModule({ code: nodeShader }),
        entryPoint: "vs",
        buffers: [{
          arrayStride: 32,
          stepMode: "instance",
          attributes: [
            { shaderLocation: 0, offset: 0, format: "float32x2" },
            { shaderLocation: 1, offset: 8, format: "float32x4" },
            { shaderLocation: 2, offset: 24, format: "float32" },
            { shaderLocation: 3, offset: 28, format: "float32" }
          ]
        }]
      },
      fragment: {
        module: this.device.createShaderModule({ code: nodeShader }),
        entryPoint: "fs",
        targets: [{
          format: this.format,
          blend: {
            color: {
              srcFactor: "src-alpha",
              dstFactor: "one-minus-src-alpha",
              operation: "add"
            },
            alpha: {
              srcFactor: "one",
              dstFactor: "one-minus-src-alpha",
              operation: "add"
            }
          }
        }]
      },
      primitive: {
        topology: "triangle-list"
      }
    });
  }

  resize() {
    const bounds = this.canvas.getBoundingClientRect();
    this.pixelRatio = Math.max(1, Math.min(window.devicePixelRatio || 1, 2));
    const width = Math.max(1, Math.floor(bounds.width * this.pixelRatio));
    const height = Math.max(1, Math.floor(bounds.height * this.pixelRatio));
    if (this.canvas.width === width && this.canvas.height === height) {
      return false;
    }

    this.canvas.width = width;
    this.canvas.height = height;
    this.context.configure({
      device: this.device,
      format: this.format,
      alphaMode: "opaque"
    });
    return true;
  }

  setGraph(memory) {
    this.nodeBuffer?.destroy();
    this.edgeBuffer?.destroy();
    this.nodeBuffer = null;
    this.edgeBuffer = null;
    this.nodeCount = memory?.nodeCount ?? 0;
    this.edgeVertexCount = memory?.edgeVertexData.length / 6 ?? 0;

    if (!memory || memory.nodeCount === 0) {
      return;
    }

    this.nodeBuffer = this.createBuffer(memory.nodeVertexData, GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST);
    if (memory.edgeVertexData.length > 0) {
      this.edgeBuffer = this.createBuffer(memory.edgeVertexData, GPUBufferUsage.VERTEX | GPUBufferUsage.COPY_DST);
    }
  }

  updateNodes(nodeVertexData) {
    if (!this.nodeBuffer || nodeVertexData.length === 0) {
      return;
    }

    this.device.queue.writeBuffer(this.nodeBuffer, 0, nodeVertexData);
  }

  updateEdges(edgeVertexData) {
    if (!this.edgeBuffer || edgeVertexData.length === 0) {
      return;
    }

    this.device.queue.writeBuffer(this.edgeBuffer, 0, edgeVertexData);
  }

  createBuffer(data, usage) {
    const buffer = this.device.createBuffer({
      size: Math.max(4, align4(data.byteLength)),
      usage,
      mappedAtCreation: true
    });
    new data.constructor(buffer.getMappedRange()).set(data);
    buffer.unmap();
    return buffer;
  }

  draw(view) {
    if (!this.device || !this.context) {
      return 0;
    }

    this.resize();
    const start = performance.now();
    const uniforms = new Float32Array([
      this.canvas.width,
      this.canvas.height,
      view.scale,
      this.pixelRatio,
      view.x,
      view.y,
      0,
      0
    ]);
    this.device.queue.writeBuffer(this.uniformBuffer, 0, uniforms);

    const encoder = this.device.createCommandEncoder();
    const pass = encoder.beginRenderPass({
      colorAttachments: [{
        view: this.context.getCurrentTexture().createView(),
        clearValue: { r: 0.067, g: 0.071, b: 0.059, a: 1 },
        loadOp: "clear",
        storeOp: "store"
      }]
    });

    pass.setBindGroup(0, this.uniformBindGroup);
    if (this.edgeBuffer && this.edgeVertexCount > 0) {
      pass.setPipeline(this.edgePipeline);
      pass.setVertexBuffer(0, this.edgeBuffer);
      pass.draw(this.edgeVertexCount);
    }
    if (this.nodeBuffer && this.nodeCount > 0) {
      pass.setPipeline(this.nodePipeline);
      pass.setVertexBuffer(0, this.nodeBuffer);
      pass.draw(6, this.nodeCount);
    }

    pass.end();
    this.device.queue.submit([encoder.finish()]);
    return performance.now() - start;
  }
}

async function main() {
  attachEvents();
  try {
    state.renderer = new WebGpuRenderer(canvas);
    await state.renderer.init();
    pipelineRenderer.textContent = "ready";
    gpuWarning.hidden = true;
    requestRender();
  } catch (error) {
    pipelineRenderer.textContent = "unavailable";
    gpuWarning.hidden = false;
    setStatus(error.message);
  }

  const initialGlobalId = new URLSearchParams(window.location.search).get("globalId");
  if (initialGlobalId) {
    rootsInput.value = initialGlobalId;
    await loadRoots([initialGlobalId], readDepth());
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
    const id = selectedGlobal.textContent;
    if (id && id !== "-") {
      rootsInput.value = id;
      loadRoots([id], readDepth());
    }
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
    const nextScale = clamp(state.view.scale * Math.exp(-event.deltaY * 0.0012), 0.02, 18);
    state.view.scale = nextScale;
    state.view.x = x - before.x * nextScale;
    state.view.y = y - before.y * nextScale;
    requestRender();
  }, { passive: false });

  window.addEventListener("resize", requestRender);
}

async function loadRoots(roots, depth) {
  if (roots.length === 0) {
    setStatus("Enter at least one root GlobalId");
    return;
  }

  state.abort?.abort();
  state.abort = new AbortController();
  setBusy(true);
  pipelineData.textContent = "loading";
  pipelineLayout.textContent = "waiting";
  setStatus("Loading subgraph...");

  try {
    const response = await apiJson("/api/graph/subgraph", {
      method: "POST",
      signal: state.abort.signal,
      body: JSON.stringify({
        globalIds: roots.map(parseGlobalId),
        maxDepth: depth
      })
    });
    const buildStarted = performance.now();
    const memory = buildGraphMemory(response);
    pipelineData.textContent = `${formatCount(memory.nodeCount)} nodes`;
    pipelineLayout.textContent = "running";
    const layoutStarted = performance.now();
    runEdgeRelaxation(memory);
    buildVertexData(memory);
    const layoutMs = performance.now() - layoutStarted;
    state.memory = memory;
    state.selectedIndex = memory.nodeCount > 0 ? 0 : -1;
    setSelectedIndex(state.selectedIndex);
    state.renderer?.setGraph(memory);
    fitView();
    renderStats(memory, 0);
    pipelineLayout.textContent = `${layoutMs.toFixed(1)} ms`;
    setStatus(`Loaded in ${(performance.now() - buildStarted).toFixed(1)} ms`);
  } catch (error) {
    if (error.name !== "AbortError") {
      setStatus(error.message);
      pipelineData.textContent = "error";
    }
  } finally {
    state.abort = null;
    setBusy(false);
  }
}

function buildGraphMemory(response) {
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
      edgeMap.set(key, [source, target]);
    }
  };

  (response.edges ?? []).forEach(addEdge);
  nodes.forEach(node => (node.edges ?? []).forEach(addEdge));

  const edgePairs = [...edgeMap.values()];
  const nodeCount = nodes.length;
  const edgeCount = edgePairs.length;
  const positions = new Float32Array(nodeCount * 2);
  const degree = new Uint32Array(nodeCount);
  const edgeSource = new Uint32Array(edgeCount);
  const edgeTarget = new Uint32Array(edgeCount);

  edgePairs.forEach(([source, target], index) => {
    edgeSource[index] = source;
    edgeTarget[index] = target;
    degree[source] += 1;
    degree[target] += 1;
  });

  seedPositions(nodes, positions);

  return {
    nodes,
    idByGlobal,
    nodeCount,
    edgeCount,
    degree,
    positions,
    edgeSource,
    edgeTarget,
    nodeVertexData: new Float32Array(nodeCount * 8),
    edgeVertexData: new Float32Array(edgeCount * 12),
    bounds: computeBounds(positions),
    byteLength: positions.byteLength +
      degree.byteLength +
      edgeSource.byteLength +
      edgeTarget.byteLength +
      nodeCount * 8 * Float32Array.BYTES_PER_ELEMENT +
      edgeCount * 12 * Float32Array.BYTES_PER_ELEMENT
  };
}

function seedPositions(nodes, positions) {
  const goldenAngle = Math.PI * (3 - Math.sqrt(5));
  for (let index = 0; index < nodes.length; index += 1) {
    const idHash = hashString(nodes[index].globalId);
    const radius = Math.sqrt(index + 1) * 22;
    const angle = index * goldenAngle + (idHash % 4096) * 0.0007;
    positions[index * 2] = Math.cos(angle) * radius;
    positions[index * 2 + 1] = Math.sin(angle) * radius;
  }
}

function runEdgeRelaxation(memory) {
  if (memory.nodeCount === 0 || memory.edgeCount === 0) {
    memory.bounds = computeBounds(memory.positions);
    return;
  }

  const positions = memory.positions;
  const source = memory.edgeSource;
  const target = memory.edgeTarget;
  const iterations = clamp(Math.floor(2_400_000 / Math.max(1, memory.edgeCount)), 2, 36);
  const desired = 74;

  for (let iteration = 0; iteration < iterations; iteration += 1) {
    const strength = 0.018 * (1 - iteration / (iterations + 8));
    for (let edge = 0; edge < memory.edgeCount; edge += 1) {
      const a = source[edge];
      const b = target[edge];
      const ax = a * 2;
      const bx = b * 2;
      const dx = positions[bx] - positions[ax];
      const dy = positions[bx + 1] - positions[ax + 1];
      const distance = Math.max(0.001, Math.hypot(dx, dy));
      const shift = (distance - desired) * strength;
      const sx = dx / distance * shift;
      const sy = dy / distance * shift;
      positions[ax] += sx;
      positions[ax + 1] += sy;
      positions[bx] -= sx;
      positions[bx + 1] -= sy;
    }

    for (let node = 0; node < memory.nodeCount; node += 1) {
      positions[node * 2] *= 0.9992;
      positions[node * 2 + 1] *= 0.9992;
    }
  }

  memory.bounds = computeBounds(positions);
}

function buildVertexData(memory) {
  const positions = memory.positions;
  const nodes = memory.nodes;
  const nodeData = memory.nodeVertexData;
  for (let index = 0; index < memory.nodeCount; index += 1) {
    const color = colorForNode(nodes[index]);
    const base = index * 8;
    nodeData[base] = positions[index * 2];
    nodeData[base + 1] = positions[index * 2 + 1];
    nodeData[base + 2] = color[0];
    nodeData[base + 3] = color[1];
    nodeData[base + 4] = color[2];
    nodeData[base + 5] = 0.92;
    nodeData[base + 6] = clamp(4 + Math.sqrt(memory.degree[index]) * 1.8, 4, 18);
    nodeData[base + 7] = index === state.selectedIndex ? 1 : 0;
  }

  const edgeData = memory.edgeVertexData;
  for (let edge = 0; edge < memory.edgeCount; edge += 1) {
    const source = memory.edgeSource[edge];
    const target = memory.edgeTarget[edge];
    const out = edge * 12;
    writeEdgeVertex(edgeData, out, positions[source * 2], positions[source * 2 + 1]);
    writeEdgeVertex(edgeData, out + 6, positions[target * 2], positions[target * 2 + 1]);
  }
}

function writeEdgeVertex(edgeData, offset, x, y) {
  edgeData[offset] = x;
  edgeData[offset + 1] = y;
  edgeData[offset + 2] = edgeColor[0];
  edgeData[offset + 3] = edgeColor[1];
  edgeData[offset + 4] = edgeColor[2];
  edgeData[offset + 5] = edgeColor[3];
}

function setSelectedIndex(index) {
  state.selectedIndex = index;
  const memory = state.memory;
  if (!memory) {
    renderSelection();
    return;
  }

  for (let node = 0; node < memory.nodeCount; node += 1) {
    memory.nodeVertexData[node * 8 + 7] = node === index ? 1 : 0;
  }
  state.renderer?.updateNodes(memory.nodeVertexData);
  renderSelection();
  requestRender();
}

function selectNearest(clientX, clientY) {
  const memory = state.memory;
  if (!memory || memory.nodeCount === 0) {
    return;
  }

  const rect = canvas.getBoundingClientRect();
  const x = clientX - rect.left;
  const y = clientY - rect.top;
  let bestIndex = -1;
  let bestDistance = Infinity;
  for (let index = 0; index < memory.nodeCount; index += 1) {
    const base = index * 2;
    const sx = memory.positions[base] * state.view.scale + state.view.x;
    const sy = memory.positions[base + 1] * state.view.scale + state.view.y;
    const distance = Math.hypot(sx - x, sy - y);
    const threshold = Math.max(9, memory.nodeVertexData[index * 8 + 6] + 6);
    if (distance <= threshold && distance < bestDistance) {
      bestIndex = index;
      bestDistance = distance;
    }
  }

  if (bestIndex !== -1) {
    setSelectedIndex(bestIndex);
  }
}

function renderSelection() {
  const memory = state.memory;
  const node = memory && state.selectedIndex >= 0
    ? memory.nodes[state.selectedIndex]
    : null;

  loadSelectedButton.disabled = !node;
  selectedTitle.textContent = node?.localId ?? "-";
  selectedGlobal.textContent = node?.globalId ?? "-";
  selectedDegree.textContent = node ? String(memory.degree[state.selectedIndex]) : "-";
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

function renderStats(memory, frameMs) {
  statNodes.textContent = formatCount(memory?.nodeCount ?? 0);
  statEdges.textContent = formatCount(memory?.edgeCount ?? 0);
  statMemory.textContent = formatBytes(memory?.byteLength ?? 0);
  statFrame.textContent = `${frameMs.toFixed(2)} ms`;
}

function requestRender() {
  if (state.renderPending) {
    return;
  }

  state.renderPending = true;
  requestAnimationFrame(() => {
    state.renderPending = false;
    const frameMs = state.renderer?.draw(state.view) ?? 0;
    renderStats(state.memory, frameMs);
  });
}

function fitView() {
  const memory = state.memory;
  const rect = canvas.getBoundingClientRect();
  if (!memory || memory.nodeCount === 0 || rect.width <= 0 || rect.height <= 0) {
    state.view = { x: rect.width / 2, y: rect.height / 2, scale: 1 };
    requestRender();
    return;
  }

  const bounds = memory.bounds;
  const width = Math.max(1, bounds.maxX - bounds.minX);
  const height = Math.max(1, bounds.maxY - bounds.minY);
  const scale = clamp(Math.min((rect.width - 80) / width, (rect.height - 120) / height), 0.02, 12);
  state.view.scale = scale;
  state.view.x = rect.width / 2 - ((bounds.minX + bounds.maxX) / 2) * scale;
  state.view.y = rect.height / 2 - ((bounds.minY + bounds.maxY) / 2) * scale;
  requestRender();
}

function resetGraph() {
  state.abort?.abort();
  state.memory = null;
  state.selectedIndex = -1;
  state.view = { x: 0, y: 0, scale: 1 };
  state.renderer?.setGraph(null);
  pipelineData.textContent = "empty";
  pipelineLayout.textContent = "idle";
  renderSelection();
  renderStats(null, 0);
  setStatus("");
  requestRender();
}

function screenToWorld(x, y) {
  return {
    x: (x - state.view.x) / state.view.scale,
    y: (y - state.view.y) / state.view.scale
  };
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
  const localId = node.localId ?? globalId.split("/").filter(Boolean).at(-1) ?? globalId;
  return {
    globalId,
    localId,
    attributes: node.attributes ?? {},
    edges: (node.edges ?? []).map(normalizeEdge)
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

function colorForNode(node) {
  const hex = node.attributes["projection.color"] || node.attributes.color;
  const parsed = parseHexColor(hex);
  if (parsed) {
    return parsed;
  }

  return palette[hashString(node.globalId) % palette.length];
}

function parseHexColor(value) {
  if (!value || !/^#[0-9a-f]{6}$/i.test(value)) {
    return null;
  }

  const number = Number.parseInt(value.slice(1), 16);
  return [
    ((number >> 16) & 255) / 255,
    ((number >> 8) & 255) / 255,
    (number & 255) / 255
  ];
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
    minX: minX - 40,
    minY: minY - 40,
    maxX: maxX + 40,
    maxY: maxY + 40
  };
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

function formatBytes(value) {
  if (value < 1024) {
    return `${value} B`;
  }

  const units = ["KB", "MB", "GB"];
  let size = value / 1024;
  let unit = 0;
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit += 1;
  }
  return `${size.toFixed(size >= 10 ? 1 : 2)} ${units[unit]}`;
}

function clamp(value, min, max) {
  return Math.min(max, Math.max(min, value));
}

function align4(value) {
  return (value + 3) & ~3;
}

main();
