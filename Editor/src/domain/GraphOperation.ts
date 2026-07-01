export type GraphSelectionSnapshot = {
  readonly nodeCount: number;
  readonly edgeCount: number;
};

export class GraphSelection {
  readonly nodeCount: number;
  readonly edgeCount: number;

  constructor(snapshot: GraphSelectionSnapshot) {
    this.nodeCount = snapshot.nodeCount;
    this.edgeCount = snapshot.edgeCount;
  }

  get totalCount(): number {
    return this.nodeCount + this.edgeCount;
  }

  hasNodes(): boolean {
    return this.nodeCount > 0;
  }

  hasEdges(): boolean {
    return this.edgeCount > 0;
  }

  hasOnlyNodes(): boolean {
    return this.edgeCount === 0;
  }

  hasOnlyEdges(): boolean {
    return this.nodeCount === 0 && this.edgeCount > 0;
  }

  hasExactlyOneNode(): boolean {
    return this.nodeCount === 1 && this.edgeCount === 0;
  }

  isEmpty(): boolean {
    return this.totalCount === 0;
  }

  allows(operation: GraphOperation): boolean {
    return operation.isAvailable(this);
  }
}

export abstract class GraphOperation {
  readonly id: string;

  protected constructor(id: string) {
    this.id = id;
  }

  abstract isAvailable(selection: GraphSelection): boolean;
}

export class CreateNodeOperation extends GraphOperation {
  constructor() {
    super("create-node");
  }

  override isAvailable(selection: GraphSelection): boolean {
    return selection.hasOnlyNodes();
  }
}

export class ClearSelectionOperation extends GraphOperation {
  constructor() {
    super("clear-selection");
  }

  override isAvailable(selection: GraphSelection): boolean {
    return !selection.isEmpty();
  }
}

export class DeleteNodesOperation extends GraphOperation {
  constructor() {
    super("delete-nodes");
  }

  override isAvailable(selection: GraphSelection): boolean {
    return selection.hasNodes();
  }
}

export class ConnectNodesOperation extends GraphOperation {
  constructor() {
    super("connect-nodes");
  }

  override isAvailable(selection: GraphSelection): boolean {
    return selection.hasExactlyOneNode();
  }
}

export class AssignNodeTypeOperation extends GraphOperation {
  constructor() {
    super("assign-node-type");
  }

  override isAvailable(selection: GraphSelection): boolean {
    return selection.hasNodes();
  }
}

export class AssignEdgeTypeOperation extends GraphOperation {
  constructor() {
    super("assign-edge-type");
  }

  override isAvailable(selection: GraphSelection): boolean {
    return selection.hasEdges();
  }
}

export class GraphOperations {
  readonly createNode = new CreateNodeOperation();
  readonly clearSelection = new ClearSelectionOperation();
  readonly deleteNodes = new DeleteNodesOperation();
  readonly connectNodes = new ConnectNodesOperation();
  readonly assignNodeType = new AssignNodeTypeOperation();
  readonly assignEdgeType = new AssignEdgeTypeOperation();
}
