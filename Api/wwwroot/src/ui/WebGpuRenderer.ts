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

  let alpha = smoothstep(1.0, 0.78, distance);
  if (in.selected > 0.5 && distance > 0.70) {
    return vec4<f32>(1.0, 0.78, 0.24, alpha);
  }

  if (distance > 0.82) {
    return vec4<f32>(in.color.rgb, in.color.a * alpha);
  }

  return vec4<f32>(1.0, 1.0, 1.0, 0.98);
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

export class WebGpuRenderer {
  [key: string]: any;

  constructor({ canvas, window }) {
    this.canvas = canvas;
    this.window = window;
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
    if (!this.window.isSecureContext) {
      const origin = this.window.location?.origin ?? "unknown origin";
      throw new Error(`WebGPU requires HTTPS or localhost. Current origin is not secure: ${origin}`);
    }

    const gpu = this.window.navigator.gpu;
    if (!gpu) {
      throw new Error("WebGPU is unavailable: this browser context did not expose navigator.gpu.");
    }

    const adapter = await gpu.requestAdapter({ powerPreference: "high-performance" });
    if (!adapter) {
      throw new Error("WebGPU adapter was not found");
    }

    this.device = await adapter.requestDevice();
    this.format = gpu.getPreferredCanvasFormat();
    const usage = this.gpuBufferUsage();
    this.uniformBuffer = this.device.createBuffer({
      size: 32,
      usage: usage.UNIFORM | usage.COPY_DST
    });

    const uniformBindGroupLayout = this.device.createBindGroupLayout({
      entries: [{
        binding: 0,
        visibility: this.gpuShaderStage().VERTEX,
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
    this.context = this.canvas.getContext("webgpu");
    if (!this.context) {
      throw new Error("WebGPU canvas context was not created");
    }

    this.resize();
  }

  createEdgePipeline(layout) {
    return this.device.createRenderPipeline({
      label: "graph-edge-pipeline",
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
      label: "graph-node-pipeline",
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
    if (!this.context || !this.device) {
      return false;
    }

    const bounds = this.canvas.getBoundingClientRect();
    this.pixelRatio = Math.max(1, Math.min(this.window.devicePixelRatio || 1, 2));
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
      alphaMode: "premultiplied"
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

    if (!memory || memory.nodeCount === 0 || !this.device) {
      return;
    }

    const usage = this.gpuBufferUsage();
    this.nodeBuffer = this.createBuffer(memory.nodeVertexData, usage.VERTEX | usage.COPY_DST);
    if (memory.edgeVertexData.length > 0) {
      this.edgeBuffer = this.createBuffer(memory.edgeVertexData, usage.VERTEX | usage.COPY_DST);
    }
  }

  updateGraph(memory) {
    const nextEdgeVertexCount = memory?.edgeVertexData.length / 6 ?? 0;
    if (!memory
      || this.nodeCount !== memory.nodeCount
      || this.edgeVertexCount !== nextEdgeVertexCount
      || !this.nodeBuffer
      || (nextEdgeVertexCount > 0 && !this.edgeBuffer)) {
      this.setGraph(memory);
      return;
    }

    this.device.queue.writeBuffer(this.nodeBuffer, 0, memory.nodeVertexData);
    if (this.edgeBuffer && memory.edgeVertexData.length > 0) {
      this.device.queue.writeBuffer(this.edgeBuffer, 0, memory.edgeVertexData);
    }
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
    if (!this.device || !this.context || !this.uniformBuffer || !this.uniformBindGroup) {
      return 0;
    }

    this.resize();
    const start = this.window.performance.now();
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
        clearValue: { r: 0, g: 0, b: 0, a: 0 },
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
    return this.window.performance.now() - start;
  }

  dispose() {
    this.nodeBuffer?.destroy();
    this.edgeBuffer?.destroy();
    this.uniformBuffer?.destroy();
  }

  gpuBufferUsage() {
    const usage = this.window.GPUBufferUsage ?? globalThis["GPUBufferUsage"];
    if (!usage) {
      throw new Error("WebGPU buffer usage constants are unavailable");
    }
    return usage;
  }

  gpuShaderStage() {
    const stage = this.window.GPUShaderStage ?? globalThis["GPUShaderStage"];
    if (!stage) {
      throw new Error("WebGPU shader stage constants are unavailable");
    }
    return stage;
  }
}

function align4(value) {
  return Math.ceil(value / 4) * 4;
}
