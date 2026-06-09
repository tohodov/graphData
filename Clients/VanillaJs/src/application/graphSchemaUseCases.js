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

export const graphSchemaUseCases = {
  async loadBasis() {
  const basisName = this.basisNodeInput.value.trim();
  if (!basisName) {
    this.readBasisInputs();
    await this.refreshTypes();
    this.render();
    return;
  }

  this.setBusy(true);
  try {
    const basisNode = this.normalizeNodeResponse(await this.apiJson(`/api/graph/nodes?${this.toGlobalIdQuery(basisName)}`));
    this.state.schema.basis = {
      nodeTypeRoot: basisNode.attributes?.nodeTypeRoot || defaultBasis.nodeTypeRoot,
      edgeTypeRoot: basisNode.attributes?.edgeTypeRoot || defaultBasis.edgeTypeRoot,
      relationRoot: basisNode.attributes?.relationRoot || defaultBasis.relationRoot
    };
    this.syncBasisInputs();
    this.storeNodeExpansion(basisNode, null, { select: false });
    await this.refreshTypes({ preserveBusy: true });
    this.render();
    this.setStatus(`Базис загружен: ${basisName}`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  },

  async ensureDefaultBasis() {
  this.setBusy(true);
  try {
    this.readBasisInputs();
    const basis = this.getBasis();
    await this.ensurePath(basis.nodeTypeRoot, { [graphKindAttribute]: "type-root", [graphElementAttribute]: "node" });
    await this.ensurePath(basis.edgeTypeRoot, { [graphKindAttribute]: "type-root", [graphElementAttribute]: "edge" });
    await this.ensurePath(basis.relationRoot, { [graphKindAttribute]: "relation-root", [graphElementAttribute]: "edge" });

    await this.upsertGraphType(basis.nodeTypeRoot, "Type", "Type", "#334155", "node", false, 90);
    await this.upsertGraphType(basis.nodeTypeRoot, "Instance", "Instance", "#0f766e", "node", false, 70);
    await this.upsertGraphType(basis.edgeTypeRoot, "Type", "Type", "#7c2d12", "edge", true, 60);
    await this.upsertGraphType(basis.edgeTypeRoot, "Instance", "Instance", "#b45309", "edge", true, 50);

    const basisName = this.basisNodeInput.value.trim();
    if (basisName) {
      const segments = this.parseGlobalId(basisName);
      const localId = segments[segments.length - 1];
      const parent = segments.length > 1 ? segments.slice(0, -1).join("/") : null;
      if (parent) {
        await this.ensurePath(parent);
      }
      await this.createGraphNode(localId, parent, {
        [graphKindAttribute]: "basis",
        nodeTypeRoot: basis.nodeTypeRoot,
        edgeTypeRoot: basis.edgeTypeRoot,
        relationRoot: basis.relationRoot
      });
    }

    await this.refreshTypes({ preserveBusy: true });
    this.render();
    this.setStatus("Базовый базис создан или обновлен");
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  },

  async refreshTypes(options = {}) {
  if (!options.preserveBusy) {
    this.setBusy(true);
  }

  try {
    this.readBasisInputs();
    const basis = this.getBasis();
    const response = await this.loadSubgraphForRoots([basis.nodeTypeRoot, basis.edgeTypeRoot], 4);
    this.mergeSubgraphIntoViewer(response, { select: false });
    const nodes = (response.nodes ?? []).map(node => this.normalizeNodeResponse(node));
    this.state.schema.nodeTypes = new Map();
    this.state.schema.edgeTypes = new Map();

    nodes
      .filter(node => node.globalId !== basis.nodeTypeRoot && node.globalId !== basis.edgeTypeRoot)
      .forEach(node => {
        if (this.isChildOf(node.globalId, basis.nodeTypeRoot)) {
          this.state.schema.nodeTypes.set(node.globalId, this.toGraphType(node, "node"));
        } else if (this.isChildOf(node.globalId, basis.edgeTypeRoot)) {
          this.state.schema.edgeTypes.set(node.globalId, this.toGraphType(node, "edge"));
        }
      });

    this.renderTypeControls();
    this.render();
    this.setStatus(`Типы: ${this.state.schema.nodeTypes.size} узлов, ${this.state.schema.edgeTypes.size} связей`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    if (!options.preserveBusy) {
      this.setBusy(false);
    }
  }

  },

  async loadRelationInstances() {
  this.setBusy(true);
  try {
    this.readBasisInputs();
    const matches = await this.searchNodeMatches({
      return: ["n"],
      where: {
        kind: "attribute",
        node: this.variableSelector("n"),
        key: graphKindAttribute,
        operator: "equals",
        value: "edge-instance"
      },
      limit: 500
    });
    const relationIds = matches
      .map(match => this.normalizeNodeResponse(match.node).globalId)
      .filter(globalId => this.isChildOf(globalId, this.getBasis().relationRoot));

    for (const relationId of relationIds) {
      const subgraph = await this.loadSubgraphForRoots([relationId], 2);
      this.mergeSubgraphIntoViewer(subgraph, { select: false });
    }

    this.renderTypeControls();
    this.render();
    this.runSimulation(32);
    this.setStatus(`Инстансы связей загружены: ${relationIds.length}`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  },

  async assignSelectedNodeType() {
  const nodeName = this.state.selectedName;
  const typeGlobalId = this.assignNodeType.value;
  if (!nodeName || !this.state.loaded.has(nodeName) || !typeGlobalId) {
    this.setStatus("Выберите загруженный узел и тип узла");
    return;
  }

  this.setBusy(true);
  try {
    await this.connectGraphNodes(nodeName, typeGlobalId);
    await this.loadNode(nodeName, null, { select: true });
    this.render();
    this.setStatus(`Тип ${this.displayName(typeGlobalId)} назначен узлу ${this.displayName(nodeName)}`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  },

  async createTypedEdgeRelation(source, target, typeGlobalId, relationLocalId = "") {
  this.readBasisInputs();
  await this.ensurePath(this.getBasis().relationRoot);
  const relation = await this.createGraphNode(relationLocalId || this.createRelationLocalId(typeGlobalId), this.getBasis().relationRoot, {
    [graphKindAttribute]: "edge-instance",
    [graphElementAttribute]: "edge",
    [graphTypeNameAttribute]: typeGlobalId
  });
  const sourcePort = await this.createGraphNode("source", relation.globalId, {
    [graphKindAttribute]: "edge-port",
    [graphRoleAttribute]: "source"
  });
  const targetPort = await this.createGraphNode("target", relation.globalId, {
    [graphKindAttribute]: "edge-port",
    [graphRoleAttribute]: "target"
  });

  await this.connectGraphNodes(relation.globalId, typeGlobalId);
  await this.connectGraphNodes(sourcePort.globalId, source);
  await this.connectGraphNodes(targetPort.globalId, target);
  return relation;

  },

  async upsertGraphType(rootGlobalId, localId, label, color, element, directed = false, rank = element === "node" ? 50 : 30) {
  const attrs = {
    [graphKindAttribute]: "type",
    [graphElementAttribute]: element,
    label,
    color,
    [projectionRankAttribute]: String(rank)
  };
  if (element === "edge") {
    attrs.directed = directed ? "true" : "false";
  }
  return this.createGraphNode(localId, rootGlobalId, attrs);

  },

  async ensurePath(globalId, leafAttributes = null) {
  const segments = this.parseGlobalId(globalId);
  let parent = null;
  let current = "";
  for (let index = 0; index < segments.length; index += 1) {
    const segment = segments[index];
    current = current ? `${current}/${segment}` : segment;
    const attrs = index === segments.length - 1 ? leafAttributes : null;
    await this.createGraphNode(segment, parent, attrs);
    parent = current;
  }

  },

  async saveTypeProjectionRules(globalId, element, rules) {
  this.setBusy(true);
  try {
    const loaded = await this.getLoadedNode(globalId);
    const attributes = { ...(loaded.attributes ?? {}) };
    attributes[graphKindAttribute] = attributes[graphKindAttribute] || "type";
    attributes[graphElementAttribute] = element;
    attributes[projectionVisibleAttribute] = rules.visible ? "true" : "false";
    if (rules.color) {
      attributes[projectionColorAttribute] = rules.color;
      attributes.color = rules.color;
    } else {
      delete attributes[projectionColorAttribute];
      delete attributes.color;
    }

    if (rules.rank) {
      attributes[projectionRankAttribute] = String(this.readRank(rules.rank, element === "edge" ? 30 : 50));
    } else {
      delete attributes[projectionRankAttribute];
    }

    if (element === "node") {
      if (rules.infoAttribute) {
        attributes[projectionInfoAttribute] = rules.infoAttribute;
      } else {
        delete attributes[projectionInfoAttribute];
      }
    } else {
      attributes[projectionDirectedAttribute] = rules.directed ? "true" : "false";
      attributes.directed = rules.directed ? "true" : "false";
      attributes[projectionLabelVisibleAttribute] = rules.labelVisible ? "true" : "false";
    }

    await this.updateGraphNodeAttributes(globalId, attributes);
    loaded.attributes = attributes;
    this.state.loaded.set(globalId, loaded);
    if (element === "node") {
      this.state.schema.nodeTypes.set(globalId, this.toGraphType(loaded, "node"));
    } else {
      this.state.schema.edgeTypes.set(globalId, this.toGraphType(loaded, "edge"));
    }
    this.render();
    this.renderTypeControls();
    this.setStatus(`Правила сохранены: ${this.displayName(globalId)}`);
  } catch (error) {
    this.setStatus(error.message);
  } finally {
    this.setBusy(false);
  }

  },

  getBasis() {
  return this.state.schema.basis;

  },

  readBasisInputs() {
  this.state.schema.basis = {
    nodeTypeRoot: this.nodeTypeRootInput.value.trim() || defaultBasis.nodeTypeRoot,
    edgeTypeRoot: this.edgeTypeRootInput.value.trim() || defaultBasis.edgeTypeRoot,
    relationRoot: this.relationRootInput.value.trim() || defaultBasis.relationRoot
  };

  },

  syncBasisInputs() {
  this.nodeTypeRootInput.value = this.state.schema.basis.nodeTypeRoot;
  this.edgeTypeRootInput.value = this.state.schema.basis.edgeTypeRoot;
  this.relationRootInput.value = this.state.schema.basis.relationRoot;
  this.projectionBasis.value = this.state.schema.projectionBasis;

  },

  createRelationLocalId(typeGlobalId) {
  const typeName = this.getLocalId(typeGlobalId).replace(/[^A-Za-z0-9._ -]/g, "-");
  return `${typeName}-${Date.now().toString(36)}`;

  }
};
