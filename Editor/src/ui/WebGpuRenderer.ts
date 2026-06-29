import edgeShader from "./shaders/edge.wgsl";
import nodeShader from "./shaders/node.wgsl";
import type { GraphRenderer, GraphRendererHost, GraphRenderMemory, GraphVertexData, GraphView } from "./GraphRenderer.js";

type WebGpuBufferUsageConstants = {
  readonly COPY_DST: GPUBufferUsageFlags;
  readonly UNIFORM: GPUBufferUsageFlags;
  readonly VERTEX: GPUBufferUsageFlags;
};

type WebGpuShaderStageConstants = {
  readonly FRAGMENT: GPUShaderStageFlags;
  readonly VERTEX: GPUShaderStageFlags;
};

type WebGpuGlobal = typeof globalThis & {
  GPUBufferUsage?: WebGpuBufferUsageConstants;
  GPUShaderStage?: WebGpuShaderStageConstants;
};

export class WebGpuRenderer implements GraphRenderer {
  mode = "webgpu";
  document: Document;
  window: Window;
  surface: HTMLElement | undefined;
  canvas: HTMLCanvasElement;
  ownsCanvas: boolean;
  device: GPUDevice | null;
  context: GPUCanvasContext | null;
  format: GPUTextureFormat | null;
  pixelRatio: number;
  uniformBuffer: GPUBuffer | null;
  uniformBindGroup: GPUBindGroup | null;
  nodeBuffer: GPUBuffer | null;
  edgeBuffer: GPUBuffer | null;
  nodeCount: number;
  edgeVertexCount: number;
  nodePipeline: GPURenderPipeline | null;
  edgePipeline: GPURenderPipeline | null;

  constructor({ document, window, surface, canvas }: GraphRendererHost & { canvas?: HTMLCanvasElement }) {
    this.document = document;
    this.window = window;
    this.surface = surface ?? canvas?.parentElement;
    this.canvas = canvas ?? this.document.createElement("canvas");
    this.ownsCanvas = !canvas;
    this.canvas.classList.add("graph-render-layer", "graph-webgpu-layer");
    this.canvas.setAttribute("aria-hidden", "true");
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

    if (this.ownsCanvas) {
      this.surface.append(this.canvas);
    }
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
    this.context = this.canvas.getContext("webgpu") as GPUCanvasContext | null;
    if (!this.context) {
      throw new Error("WebGPU canvas context was not created");
    }

    this.resize();
  }

  createEdgePipeline(layout: GPUPipelineLayout) {
    return this.device!.createRenderPipeline({
      label: "graph-edge-pipeline",
      layout,
      vertex: {
        module: this.device!.createShaderModule({ code: edgeShader }),
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
        module: this.device!.createShaderModule({ code: edgeShader }),
        entryPoint: "fs",
        targets: [{
          format: this.format!,
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

  createNodePipeline(layout: GPUPipelineLayout) {
    return this.device!.createRenderPipeline({
      label: "graph-node-pipeline",
      layout,
      vertex: {
        module: this.device!.createShaderModule({ code: nodeShader }),
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
        module: this.device!.createShaderModule({ code: nodeShader }),
        entryPoint: "fs",
        targets: [{
          format: this.format!,
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
      device: this.device as GPUDevice,
      format: (this.format ?? "bgra8unorm") as GPUTextureFormat,
      alphaMode: "premultiplied"
    });
    return true;
  }

  setGraph(memory: GraphRenderMemory | null) {
    this.nodeBuffer?.destroy();
    this.edgeBuffer?.destroy();
    this.nodeBuffer = null;
    this.edgeBuffer = null;
    this.nodeCount = memory?.nodeCount ?? 0;
    this.edgeVertexCount = memory ? memory.edgeVertexData.length / 6 : 0;

    if (!memory || memory.nodeCount === 0 || !this.device) {
      return;
    }

    const usage = this.gpuBufferUsage();
    this.nodeBuffer = this.createBuffer(memory.nodeVertexData, usage.VERTEX | usage.COPY_DST);
    if (memory.edgeVertexData.length > 0) {
      this.edgeBuffer = this.createBuffer(memory.edgeVertexData, usage.VERTEX | usage.COPY_DST);
    }
  }

  updateGraph(memory: GraphRenderMemory | null) {
    const nextEdgeVertexCount = memory ? memory.edgeVertexData.length / 6 : 0;
    if (!memory
      || this.nodeCount !== memory.nodeCount
      || this.edgeVertexCount !== nextEdgeVertexCount
      || !this.nodeBuffer
      || (nextEdgeVertexCount > 0 && !this.edgeBuffer)) {
      this.setGraph(memory);
      return;
    }

    this.device!.queue.writeBuffer(this.nodeBuffer, 0, memory.nodeVertexData);
    if (this.edgeBuffer && memory.edgeVertexData.length > 0) {
      this.device!.queue.writeBuffer(this.edgeBuffer, 0, memory.edgeVertexData);
    }
  }

  createBuffer(data: GraphVertexData, usage: GPUBufferUsageFlags) {
    if (!this.device) return null;
    const buffer = this.device.createBuffer({
      size: Math.max(4, align4(data.byteLength)),
      usage,
      mappedAtCreation: true
    });
    new Float32Array(buffer.getMappedRange()).set(data);
    buffer.unmap();
    return buffer;
  }

  draw(view: GraphView) {
    if (!this.device || !this.context || !this.uniformBuffer || !this.uniformBindGroup) {
      return 0;
    }

    this.resize();
    const start = this.window.performance.now();
    const uniforms: GraphVertexData = new Float32Array([
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
    if (this.edgePipeline && this.edgeBuffer && this.edgeVertexCount > 0) {
      pass.setPipeline(this.edgePipeline);
      pass.setVertexBuffer(0, this.edgeBuffer);
      pass.draw(this.edgeVertexCount);
    }
    if (this.nodePipeline && this.nodeBuffer && this.nodeCount > 0) {
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
    if (this.ownsCanvas) {
      this.canvas.remove();
    }
  }

  gpuBufferUsage() {
    const usage = (globalThis as WebGpuGlobal).GPUBufferUsage;
    if (!usage) {
      throw new Error("WebGPU buffer usage constants are unavailable");
    }
    return usage;
  }

  gpuShaderStage() {
    const stage = (globalThis as WebGpuGlobal).GPUShaderStage;
    if (!stage) {
      throw new Error("WebGPU shader stage constants are unavailable");
    }
    return stage;
  }
}

function align4(value: number) {
  return Math.ceil(value / 4) * 4;
}
