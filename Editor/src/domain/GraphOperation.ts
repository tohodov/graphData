export type GraphSelectionSnapshot = {
  readonly nodeCount: number;
  readonly edgeCount: number;
  readonly nodeNames?: readonly string[];
  readonly edgeObjects?: readonly unknown[];
};

export class GraphSelection {
  readonly nodeCount: number;
  readonly edgeCount: number;
  readonly nodeNames: readonly string[];
  readonly edgeObjects: readonly unknown[];

  constructor(snapshot: GraphSelectionSnapshot) {
    this.nodeCount = snapshot.nodeCount;
    this.edgeCount = snapshot.edgeCount;
    this.nodeNames = snapshot.nodeNames ?? [];
    this.edgeObjects = snapshot.edgeObjects ?? [];
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

export type CreateNodeOperationInput = {
  readonly localId: string;
  readonly typeGlobalId: string;
};

export type ConnectNodesOperationInput = {
  readonly targetNodeGlobalId: string;
  readonly typeGlobalId: string;
  readonly relationLocalId: string;
};

export type AssignTypeOperationInput = {
  readonly typeGlobalId: string;
};

export type GraphOperationContext = {
  readonly selection: GraphSelection;
  confirm(message: string): boolean;
  clearSelection(): void;
  deleteSelection(nodeNames: readonly string[], edges: readonly unknown[]): Promise<void>;
  createNode(localId: string, options: { typeGlobalId?: string; linkedNodeNames?: readonly string[] }): Promise<void>;
  connectNodes(
    node1InternalId: string,
    node2InternalId: string,
    options: { typeGlobalId?: string; relationLocalId?: string }
  ): Promise<void>;
  assignNodeType(nodeNames: readonly string[], typeGlobalId: string): Promise<void>;
  assignEdgeType(edges: readonly unknown[], typeGlobalId: string): Promise<void>;
  setStatus(message: string): void;
};

export abstract class GraphOperation {
  readonly id: string;

  protected constructor(id: string) {
    this.id = id;
  }

  abstract isAvailable(selection: GraphSelection): boolean;

  protected ensureAvailable(context: GraphOperationContext, message: string): boolean {
    if (context.selection.allows(this)) {
      return true;
    }

    context.setStatus(message);
    return false;
  }
}

export class CreateNodeOperation extends GraphOperation {
  constructor() {
    super("create-node");
  }

  override isAvailable(selection: GraphSelection): boolean {
    return selection.hasOnlyNodes();
  }

  execute(context: GraphOperationContext, input: CreateNodeOperationInput): Promise<void> {
    if (!this.ensureAvailable(context, "Создание узла недоступно, когда выбраны связи")) {
      return Promise.resolve();
    }

    if (!input.localId) {
      context.setStatus("Введите LocalId нового узла");
      return Promise.resolve();
    }

    return context.createNode(input.localId, {
      typeGlobalId: input.typeGlobalId,
      linkedNodeNames: context.selection.nodeNames
    });
  }
}

export class ClearSelectionOperation extends GraphOperation {
  constructor() {
    super("clear-selection");
  }

  override isAvailable(selection: GraphSelection): boolean {
    return !selection.isEmpty();
  }

  execute(context: GraphOperationContext): void {
    if (this.ensureAvailable(context, "")) {
      context.clearSelection();
    }
  }
}

export class DeleteSelectionOperation extends GraphOperation {
  constructor() {
    super("delete-selection");
  }

  override isAvailable(selection: GraphSelection): boolean {
    return !selection.isEmpty();
  }

  execute(context: GraphOperationContext): Promise<void> {
    if (!this.ensureAvailable(context, "Нет выбранных элементов для удаления")) {
      return Promise.resolve();
    }

    const selection = context.selection;
    const parts = [
      selection.nodeCount > 0 ? `узлов: ${selection.nodeCount}` : "",
      selection.edgeCount > 0 ? `связей: ${selection.edgeCount}` : ""
    ].filter(Boolean).join(", ");
    if (!context.confirm(`Удалить выбранные элементы (${parts})? Это действие нельзя отменить.`)) {
      return Promise.resolve();
    }

    return context.deleteSelection(selection.nodeNames, selection.edgeObjects);
  }
}

export class ConnectNodesOperation extends GraphOperation {
  constructor() {
    super("connect-nodes");
  }

  override isAvailable(selection: GraphSelection): boolean {
    return selection.hasExactlyOneNode();
  }

  execute(context: GraphOperationContext, input: ConnectNodesOperationInput): Promise<void> {
    if (!this.ensureAvailable(context, "Выберите ровно один узел и укажите цель связи")) {
      return Promise.resolve();
    }

    const sourceNodeName = context.selection.nodeNames[0] ?? "";
    if (!sourceNodeName || !input.targetNodeGlobalId) {
      context.setStatus("Выберите ровно один узел и укажите цель связи");
      return Promise.resolve();
    }

    return context.connectNodes(sourceNodeName, input.targetNodeGlobalId, {
      typeGlobalId: input.typeGlobalId,
      relationLocalId: input.relationLocalId
    });
  }
}

export class AssignNodeTypeOperation extends GraphOperation {
  constructor() {
    super("assign-node-type");
  }

  override isAvailable(selection: GraphSelection): boolean {
    return selection.hasNodes();
  }

  execute(context: GraphOperationContext, input: AssignTypeOperationInput): Promise<void> {
    if (!this.ensureAvailable(context, "Выберите узлы и тип узла") || !input.typeGlobalId) {
      context.setStatus("Выберите узлы и тип узла");
      return Promise.resolve();
    }

    return context.assignNodeType(context.selection.nodeNames, input.typeGlobalId);
  }
}

export class AssignEdgeTypeOperation extends GraphOperation {
  constructor() {
    super("assign-edge-type");
  }

  override isAvailable(selection: GraphSelection): boolean {
    return selection.hasEdges();
  }

  execute(context: GraphOperationContext, input: AssignTypeOperationInput): Promise<void> {
    if (!this.ensureAvailable(context, "Выберите связи и тип связи") || !input.typeGlobalId) {
      context.setStatus("Выберите связи и тип связи");
      return Promise.resolve();
    }

    return context.assignEdgeType(context.selection.edgeObjects, input.typeGlobalId);
  }
}

export class GraphOperations {
  readonly createNode = new CreateNodeOperation();
  readonly clearSelection = new ClearSelectionOperation();
  readonly deleteSelection = new DeleteSelectionOperation();
  readonly connectNodes = new ConnectNodesOperation();
  readonly assignNodeType = new AssignNodeTypeOperation();
  readonly assignEdgeType = new AssignEdgeTypeOperation();
}
