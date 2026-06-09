import { defaultBasis } from "./graphConstants.js";

export class GraphWorkspaceState {
  constructor() {
    this.rootName = null;
    this.selectedName = null;
    this.loaded = new Map();
    this.parentByNode = new Map();
    this.positions = new Map();
    this.velocities = new Map();
    this.view = { x: 0, y: 0, scale: 1 };
    this.dragging = null;
    this.pointer = null;
    this.simulationHandle = null;
    this.searchAbort = null;
    this.busy = false;
    this.schema = {
      projectionBasis: "empty",
      basis: { ...defaultBasis },
      nodeTypes: new Map(),
      edgeTypes: new Map()
    };
  }

  resetGraph() {
    this.rootName = null;
    this.selectedName = null;
    this.loaded.clear();
    this.parentByNode.clear();
    this.positions.clear();
    this.velocities.clear();
  }
}
