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

export const graphNodeUseCases = {
  async loadRoot(name) {
  if (!name) {
    this.setStatus("Введите GlobalId узла");
    return;
  }

  this.state.rootName = name;
  this.state.selectedName = name;
  this.state.loaded.clear();
  this.state.parentByNode.clear();
  this.state.positions.clear();
  this.state.velocities.clear();
  this.seedPosition(name, null, 0);
  await this.loadNode(name, null);
  const url = new URL(this.window.location.href);
  url.searchParams.set("globalId", this.state.rootName ?? name);
  this.window.history.replaceState({}, "", url);
  this.fitView();

  },

  async loadNode(name, fromName, options = {}) {
  const select = options.select ?? true;
  this.setBusy(true);
  try {
    const expansion = this.normalizeNodeResponse(await this.apiJson(`/api/graph/nodes?${this.toGlobalIdQuery(name)}`));
    this.storeNodeExpansion(expansion, fromName, { select });

    this.render();
    this.renderTypeControls();
    this.runSimulation(34);
    this.setStatus(`Развернуто узлов: ${this.state.loaded.size}`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  },

  async loadNeighbor(anchorName, neighborLocalId) {
  if (!anchorName || !neighborLocalId) {
    this.setStatus("Не удалось определить соседа для раскрытия");
    return;
  }

  this.setBusy(true);
  try {
    const expansion = this.normalizeNodeResponse(await this.apiJson(
      `/api/graph/nodes/${encodeURIComponent(anchorName)}/neighbor/${encodeURIComponent(neighborLocalId)}`));
    const alreadyLoaded = this.state.loaded.has(expansion.name);
    this.storeNodeExpansion(expansion, anchorName, { select: true });

    this.render();
    this.renderTypeControls();
    this.runSimulation(alreadyLoaded ? 18 : 34);
    this.setStatus(alreadyLoaded
      ? `Узел "${expansion.displayName}" уже был загружен, связь добавлена`
      : `Развернуто узлов: ${this.state.loaded.size}`);
  } catch (error) {
    this.setStatus(this.formatNeighborError(error, neighborLocalId));
  } finally {
    this.setBusy(false);
  }

  },

  storeNodeExpansion(expansion, fromName, options = {}) {
  const select = options.select ?? true;
  const existing = this.state.loaded.get(expansion.name);
  const stored = existing ? this.mergeNodeResponses(existing, expansion) : expansion;
  this.state.loaded.set(expansion.name, stored);

  if (!fromName && this.state.rootName && !this.state.loaded.has(this.state.rootName)) {
    this.state.rootName = expansion.name;
  }

  if (select) {
    this.state.selectedName = expansion.name;
  }

  this.seedPosition(expansion.name, fromName, 0);
  if (fromName && fromName !== expansion.name && !this.state.parentByNode.has(expansion.name)) {
    this.state.parentByNode.set(expansion.name, fromName);
  }

  stored.edges.forEach((edge, index) => {
    this.seedPosition(this.getOtherEndpoint(edge, expansion.name), expansion.name, index);
  });

  return stored;

  },

  mergeNodeResponses(existing, expansion) {
  return {
    ...existing,
    ...expansion,
    attributes: expansion.attributes ?? existing.attributes ?? {},
    edges: this.mergeEdges(existing.edges, expansion.edges)
  };

  },

  mergeEdges(left = [], right = []) {
  const edges = new Map();
  [...left, ...right].forEach(edge => {
    if (edge.sourceGlobalId && edge.targetGlobalId) {
      edges.set(this.edgeKey(edge.sourceGlobalId, edge.targetGlobalId), edge);
    }
  });
  return [...edges.values()];

  },

  async createNode(name, options = {}) {
  const parentGlobalId = options.parentGlobalId || null;
  const typeGlobalId = options.typeGlobalId || "";
  this.setBusy(true);
  try {
    const attributes = typeGlobalId
      ? {
          [graphKindAttribute]: "instance",
          [graphElementAttribute]: "node",
          [graphTypeNameAttribute]: typeGlobalId
        }
      : null;
    const created = await this.createGraphNode(name, parentGlobalId, attributes);
    if (typeGlobalId) {
      await this.connectGraphNodes(created.globalId, typeGlobalId);
    }
    this.createNodeName.value = "";
    this.createNodeParent.value = "";
    this.state.rootName = this.state.rootName ?? created.name;
    this.state.selectedName = created.name;
    this.seedPosition(created.name, this.state.rootName === created.name ? null : this.state.rootName, this.state.loaded.size);
    if (typeGlobalId) {
      const expanded = this.normalizeNodeResponse(await this.apiJson(`/api/graph/nodes?${this.toGlobalIdQuery(created.globalId)}`));
      this.storeNodeExpansion(expanded, null, { select: true });
    } else {
      this.state.loaded.set(created.name, created);
    }
    this.render();
    this.renderTypeControls();
    this.setActiveTab("node");
    this.setStatus(`Создан узел "${created.displayName}"`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  },

  async createGraphNode(localId, parentGlobalId = null, attributes = null) {
  return this.normalizeNodeResponse(await this.apiJson("/api/graph/nodes", {
    method: "POST",
    body: JSON.stringify({
      localId,
      parentGlobalId: parentGlobalId ? this.parseGlobalId(parentGlobalId) : null,
      attributes
    })
  }));

  },

  async saveSelectedNode() {
  const nodeName = this.state.selectedName;
  if (!nodeName) {
    this.setStatus("Узел не выбран");
    return;
  }

  this.setBusy(true);
  try {
    await this.apiJson(`/api/graph/nodes?${this.toGlobalIdQuery(nodeName)}`, {
      method: "PUT",
      body: JSON.stringify({ attributes: this.readAttributeEditor() }),
      expectJson: false
    });
    await this.loadNode(nodeName, null, { select: true });
    this.setStatus(`Сохранен узел "${this.displayName(nodeName)}"`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  },

  async deleteSelectedNode() {
  const nodeName = this.state.selectedName;
  if (!nodeName) {
    this.setStatus("Узел не выбран");
    return;
  }

  this.setBusy(true);
  try {
    await this.apiJson(`/api/graph/nodes?${this.toGlobalIdQuery(nodeName)}`, {
      method: "DELETE",
      expectJson: false
    });
    this.removeLocalNode(nodeName);
    this.render();
    this.renderTypeControls();
    this.setStatus(`Удален узел "${this.displayName(nodeName)}"`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  },

  async connectNodes(sourceGlobalId, targetGlobalId, options = {}) {
  const typeGlobalId = options.typeGlobalId || "";
  this.setBusy(true);
  try {
    if (typeGlobalId) {
      const relation = await this.createTypedEdgeRelation(
        sourceGlobalId,
        targetGlobalId,
        typeGlobalId,
        options.relationLocalId || "");
      const subgraph = await this.loadSubgraphForRoots([relation.globalId, sourceGlobalId, targetGlobalId, typeGlobalId], 2);
      this.mergeSubgraphIntoViewer(subgraph, { select: false });
      this.state.selectedName = sourceGlobalId;
    } else {
      await this.connectGraphNodes(sourceGlobalId, targetGlobalId);
      await this.loadNode(sourceGlobalId, null, { select: true });
    }
    this.connectTargetName.value = "";
    this.connectEdgeName.value = "";
    this.render();
    this.renderTypeControls();
    this.runSimulation(32);
    this.setStatus(`Связаны "${this.displayName(sourceGlobalId)}" и "${this.displayName(targetGlobalId)}"`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  },

  async connectGraphNodes(sourceGlobalId, targetGlobalId) {
  await this.apiJson("/api/graph/connections", {
    method: "POST",
    body: JSON.stringify({
      sourceGlobalId: this.parseGlobalId(sourceGlobalId),
      targetGlobalId: this.parseGlobalId(targetGlobalId)
    }),
    expectJson: false
  });

  },

  collapseNode(name) {
  if (!name || name === this.state.rootName || !this.state.loaded.has(name)) {
    return;
  }

  const fallbackSelection = this.state.parentByNode.get(name) ?? this.state.rootName;
  const removed = new Set();
  const visit = current => {
    removed.add(current);
    for (const [child, parent] of this.state.parentByNode.entries()) {
      if (parent === current) {
        visit(child);
      }
    }
  };

  visit(name);
  removed.forEach(nodeName => this.removeLocalNode(nodeName, false, false));
  this.state.selectedName = fallbackSelection;
  this.render();
  this.runSimulation(18);
  this.setStatus(`Развернуто узлов: ${this.state.loaded.size}`);

  },

  removeLocalNode(name, selectFallback = true, pruneEdges = true) {
  this.state.loaded.delete(name);
  if (pruneEdges) {
    this.state.positions.delete(name);
    this.state.velocities.delete(name);
  }
  this.state.parentByNode.delete(name);
  for (const [child, parent] of [...state.parentByNode.entries()]) {
    if (parent === name) {
      this.state.parentByNode.delete(child);
    }
  }

  if (pruneEdges) {
    for (const expansion of this.state.loaded.values()) {
      expansion.edges = (expansion.edges ?? [])
        .filter(edge => edge.sourceGlobalId !== name && edge.targetGlobalId !== name);
    }
  }

  if (selectFallback && this.state.selectedName === name) {
    this.state.selectedName = this.state.loaded.keys().next().value ?? null;
    this.state.rootName = this.state.selectedName;
  }

  },

  handleEndpointClick(edge, anchorName) {
  const otherName = edge.sourceGlobalId === anchorName ? edge.targetGlobalId : edge.sourceGlobalId;
  const anchorLoaded = this.state.loaded.has(anchorName);
  const otherLoaded = this.state.loaded.has(otherName);

  if (anchorLoaded && !otherLoaded) {
    this.loadNeighbor(anchorName, this.edgeNeighborLocalId(edge, anchorName));
    return;
  }

  if (!anchorLoaded && otherLoaded) {
    this.loadNeighbor(otherName, this.edgeNeighborLocalId(edge, otherName));
    return;
  }

  if (anchorLoaded && otherLoaded) {
    if (this.state.parentByNode.get(otherName) === anchorName) {
      this.collapseNode(otherName);
      return;
    }

    if (this.state.parentByNode.get(anchorName) === otherName && anchorName !== this.state.rootName) {
      this.collapseNode(anchorName);
      return;
    }

    this.state.selectedName = otherName;
    this.render();
  }

  },

  async ensureNodeLoaded(globalId) {
  if (this.state.loaded.has(globalId)) {
    return;
  }

  const node = this.normalizeNodeResponse(await this.apiJson(`/api/graph/nodes?${this.toGlobalIdQuery(globalId)}`));
  this.storeNodeExpansion(node, null, { select: false });

  },

  async getLoadedNode(globalId) {
  await this.ensureNodeLoaded(globalId);
  return this.state.loaded.get(globalId);

  },

  async updateGraphNodeAttributes(globalId, attributes) {
  await this.apiJson(`/api/graph/nodes?${this.toGlobalIdQuery(globalId)}`, {
    method: "PUT",
    body: JSON.stringify({ attributes }),
    expectJson: false
  });

  },

  formatNeighborError(error, neighborLocalId) {
  if (error.status === 404) {
    return `Сосед "${neighborLocalId}" не найден`;
  }

  if (error.status === 409) {
    return `Сосед "${neighborLocalId}" неоднозначен`;
  }

  return error.message;

  }
};
