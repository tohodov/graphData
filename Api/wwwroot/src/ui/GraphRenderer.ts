import type { ProjectedGraph } from "../domain/GraphModel.js";

export type GraphView = {
  x: number;
  y: number;
  scale: number;
};

export type GraphRenderMemory = {
  graph: ProjectedGraph;
  nodes: import("../domain/GraphModel.js").ProjectedGraphNode[];
  edges: (import("../domain/GraphModel.js").ProjectedGraphEdge & { sourceIndex?: number; targetIndex?: number })[];
  nodeIndexByName: Map<string, number>;
  nodeCount: number;
  edgeCount: number;
  nodeVertexData: Float32Array;
  edgeVertexData: Float32Array;
  byteLength: number;
};

export type GraphRendererHost = {
  document: Document;
  window: Window;
  surface: HTMLElement;
};

export interface GraphRenderer {
  mode: string;
  init(): Promise<void>;
  setGraph(memory: GraphRenderMemory | null): void;
  updateGraph(memory: GraphRenderMemory | null): void;
  draw(view: GraphView): number;
  dispose(): void;
}
