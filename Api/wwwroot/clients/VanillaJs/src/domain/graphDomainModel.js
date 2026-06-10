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

export const graphDomainModel = {
  parseGlobalId(value) {
  return value.split("/").filter(Boolean);

  },

  toGlobalIdQuery(value) {
  return this.parseGlobalId(value)
    .map(segment => `globalId=${encodeURIComponent(segment)}`)
    .join("&");

  },

  normalizeNodeResponse(node) {
  const globalId = node.globalId ?? node.name;
  const localId = node.localId ?? node.name;
  return {
    ...node,
    name: globalId,
    globalId,
    localId,
    displayName: localId,
    edges: (node.edges ?? []).map(edge => this.normalizeEdgeResponse(edge))
  };

  },

  normalizeEdgeResponse(edge) {
  const sourceGlobalId = edge.sourceGlobalId;
  const targetGlobalId = edge.targetGlobalId;
  const sourceLocalId = edge.sourceLocalId;
  const targetLocalId = edge.targetLocalId;
  const neighborLocalId = edge.neighborLocalId;
  return {
    ...edge,
    sourceGlobalId,
    targetGlobalId,
    sourceLocalId,
    targetLocalId,
    neighborLocalId
  };

  },

  displayName(globalId) {
  return this.state.loaded.get(globalId)?.displayName ?? globalId;

  },

  edgeEndpointDisplayName(edge, globalId) {
  if (edge.sourceGlobalId === globalId) {
    return edge.sourceLocalId ?? this.displayName(globalId);
  }
  if (edge.targetGlobalId === globalId) {
    return edge.targetLocalId ?? this.displayName(globalId);
  }
  return this.displayName(globalId);

  },

  edgeNeighborLocalId(edge, anchorName) {
  if (edge.neighborLocalId) {
    return edge.neighborLocalId;
  }

  return edge.sourceGlobalId === anchorName
    ? edge.targetLocalId
    : edge.sourceLocalId;

  },

  buildGraph() {
  const physical = this.buildPhysicalGraph();
  if (this.state.schema.projectionBasis === "empty") {
    return physical;
  }

  return this.buildProjectedGraph(physical);

  },

  buildPhysicalGraph() {
  const nodes = new Map();
  const edges = new Map();

  for (const expansion of this.state.loaded.values()) {
    nodes.set(expansion.name, {
      name: expansion.name,
      displayName: expansion.displayName,
      localId: expansion.localId,
      globalId: expansion.globalId,
      attributes: expansion.attributes ?? {}
    });

    (expansion.edges ?? []).forEach(edge => {
      const key = this.edgeKey(edge.sourceGlobalId, edge.targetGlobalId);
      if (!edges.has(key)) {
        edges.set(key, {
          key,
          sourceGlobalId: edge.sourceGlobalId,
          targetGlobalId: edge.targetGlobalId,
          sourceLocalId: edge.sourceLocalId,
          targetLocalId: edge.targetLocalId
        });
      }
    });
  }

  return { nodes: [...nodes.values()], edges: [...edges.values()] };

  },

  buildProjectedGraph(physical) {
  const relationInstances = this.discoverRelationInstances(physical);
  const hidden = new Set();

  for (const relation of relationInstances) {
    hidden.add(relation.relationGlobalId);
    relation.portGlobalIds.forEach(name => hidden.add(name));
  }

  for (const type of [...state.schema.nodeTypes.values(), ...state.schema.edgeTypes.values()]) {
    hidden.add(type.globalId);
  }

  const nodeTypeAssignments = this.getNodeTypeAssignments(physical);
  const visibleNodes = physical.nodes
    .filter(node => !hidden.has(node.name))
    .filter(node => !this.isSchemaRootNode(node.name))
    .filter(node => nodeTypeAssignments.get(node.name)?.visible !== false);
  const visibleNodeIds = new Set(visibleNodes.map(node => node.name));
  const typedNodes = visibleNodes.map(node => {
    const nodeType = nodeTypeAssignments.get(node.name);
    return {
      ...node,
      typeGlobalId: nodeType?.globalId,
      typeLabel: nodeType?.label,
      typeRank: nodeType?.rank,
      color: nodeType?.color,
      displayName: this.formatProjectedNodeName(node, nodeType)
    };
  });

  const hiddenPhysicalEdges = new Set();
  for (const relation of relationInstances) {
    relation.physicalEdgeKeys.forEach(key => hiddenPhysicalEdges.add(key));
  }
  for (const [nodeId, nodeType] of nodeTypeAssignments) {
    hiddenPhysicalEdges.add(this.edgeKey(nodeId, nodeType.globalId));
  }

  const physicalEdges = physical.edges.filter(edge => {
    return visibleNodeIds.has(edge.sourceGlobalId)
      && visibleNodeIds.has(edge.targetGlobalId)
      && !hiddenPhysicalEdges.has(edge.key ?? this.edgeKey(edge.sourceGlobalId, edge.targetGlobalId));
  });

  const projectedEdges = relationInstances
    .filter(relation => visibleNodeIds.has(relation.sourceGlobalId) && visibleNodeIds.has(relation.targetGlobalId))
    .filter(relation => relation.type?.visible !== false)
    .map(relation => ({
      key: `projected:${relation.relationGlobalId}`,
      sourceGlobalId: relation.sourceGlobalId,
      targetGlobalId: relation.targetGlobalId,
      sourceLocalId: this.getLocalId(relation.sourceGlobalId),
      targetLocalId: this.getLocalId(relation.targetGlobalId),
      relationGlobalId: relation.relationGlobalId,
      typeGlobalId: relation.type?.globalId,
      label: relation.type?.labelVisible === false ? "" : relation.type?.label ?? relation.displayName,
      color: relation.type?.color,
      directed: relation.type?.directed ?? false,
      typeRank: relation.type?.rank,
      projected: true
    }));

  return this.applyBasisRanks({
    nodes: typedNodes,
    edges: [...physicalEdges, ...projectedEdges]
  });

  },

  applyBasisRanks(graph) {
  const nodeStats = new Map(graph.nodes.map(node => [node.name, {
    weightedDegree: 0,
    focusBoost: 0,
    reasons: []
  }]));

  const rankedEdges = graph.edges.map(edge => {
    const edgeType = edge.typeGlobalId ? this.state.schema.edgeTypes.get(edge.typeGlobalId) : null;
    const basisWeight = this.readRank(edge.typeRank ?? edgeType?.rank, edge.projected ? 35 : 8);
    const rank = this.roundRank(basisWeight);
    const sourceStats = nodeStats.get(edge.sourceGlobalId);
    const targetStats = nodeStats.get(edge.targetGlobalId);

    if (sourceStats) {
      sourceStats.weightedDegree += basisWeight;
    }
    if (targetStats) {
      targetStats.weightedDegree += basisWeight;
    }
    if (this.state.selectedName === edge.sourceGlobalId && targetStats) {
      targetStats.focusBoost += Math.min(25, basisWeight * 0.35);
    }
    if (this.state.selectedName === edge.targetGlobalId && sourceStats) {
      sourceStats.focusBoost += Math.min(25, basisWeight * 0.35);
    }

    return {
      ...edge,
      viewRank: rank,
      viewRankReason: edgeType?.label
        ? `edge type ${edgeType.label}: ${this.formatRank(rank)}`
        : `physical edge: ${this.formatRank(rank)}`
    };
  });

  const rankedNodes = graph.nodes.map(node => {
    const stats = nodeStats.get(node.name);
    const typePriority = this.readRank(node.typeRank, node.typeGlobalId ? 50 : 20);
    const degreeScore = Math.log1p(stats?.weightedDegree ?? 0) * 8;
    const rootBoost = node.name === this.state.rootName ? 18 : 0;
    const selectedBoost = node.name === this.state.selectedName ? 30 : 0;
    const rank = this.roundRank(typePriority + degreeScore + rootBoost + selectedBoost + (stats?.focusBoost ?? 0));
    const reasons = [
      node.typeLabel ? `type ${node.typeLabel}: ${this.formatRank(typePriority)}` : `untyped: ${this.formatRank(typePriority)}`,
      `links: ${this.formatRank(degreeScore)}`
    ];
    if (rootBoost) {
      reasons.push(`root: ${this.formatRank(rootBoost)}`);
    }
    if (selectedBoost) {
      reasons.push(`selected: ${this.formatRank(selectedBoost)}`);
    }
    if (stats?.focusBoost) {
      reasons.push(`focus: ${this.formatRank(stats.focusBoost)}`);
    }

    return {
      ...node,
      viewRank: rank,
      viewRadius: this.rankToRadius(rank),
      viewRankReason: reasons.join("; ")
    };
  });

  rankedNodes.sort((left, right) => right.viewRank - left.viewRank || left.displayName.localeCompare(right.displayName, "ru"));
  rankedEdges.sort((left, right) => (right.viewRank ?? 0) - (left.viewRank ?? 0));

  return {
    nodes: rankedNodes,
    edges: rankedEdges
  };

  },

  formatProjectedNodeName(node, nodeType) {
  const parts = [node.displayName];
  if (nodeType?.label) {
    parts.push(nodeType.label);
  }
  if (nodeType?.infoAttribute && node.attributes?.[nodeType.infoAttribute]) {
    parts.push(node.attributes[nodeType.infoAttribute]);
  }
  return parts.join(" : ");

  },

  discoverRelationInstances(physical) {
  const nodesByName = new Map(physical.nodes.map(node => [node.name, node]));
  const edgesByNode = new Map();
  physical.edges.forEach(edge => {
    [edge.sourceGlobalId, edge.targetGlobalId].forEach(name => {
      if (!edgesByNode.has(name)) {
        edgesByNode.set(name, []);
      }
      edgesByNode.get(name).push(edge);
    });
  });

  return physical.nodes
    .filter(node => this.isRelationInstanceNode(node))
    .map(relation => {
      const relationEdges = edgesByNode.get(relation.name) ?? [];
      const type = relationEdges
        .map(edge => this.getOtherEndpoint(edge, relation.name))
        .map(name => this.state.schema.edgeTypes.get(name))
        .find(Boolean) ?? null;
      const ports = physical.nodes
        .filter(node => this.isChildOf(node.name, relation.name))
        .filter(node => this.getAttribute(node, graphKindAttribute) === "edge-port" || this.getAttribute(node, graphRoleAttribute));
      const sourcePort = ports.find(node => this.getAttribute(node, graphRoleAttribute) === "source" || node.localId === "source");
      const targetPort = ports.find(node => this.getAttribute(node, graphRoleAttribute) === "target" || node.localId === "target");
      const sourceGlobalId = sourcePort ? this.getPortEndpoint(sourcePort.name, edgesByNode, relation.name) : null;
      const targetGlobalId = targetPort ? this.getPortEndpoint(targetPort.name, edgesByNode, relation.name) : null;

      if (!sourceGlobalId || !targetGlobalId) {
        return null;
      }

      const physicalEdgeKeys = new Set(relationEdges.map(edge => edge.key ?? this.edgeKey(edge.sourceGlobalId, edge.targetGlobalId)));
      for (const port of ports) {
        physicalEdgeKeys.add(this.edgeKey(relation.name, port.name));
        for (const edge of edgesByNode.get(port.name) ?? []) {
          physicalEdgeKeys.add(edge.key ?? this.edgeKey(edge.sourceGlobalId, edge.targetGlobalId));
        }
      }

      return {
        relationGlobalId: relation.name,
        displayName: relation.displayName,
        sourceGlobalId,
        targetGlobalId,
        type,
        portGlobalIds: ports.map(port => port.name),
        physicalEdgeKeys
      };
    })
    .filter(Boolean);

  },

  getPortEndpoint(portGlobalId, edgesByNode, relationGlobalId) {
  for (const edge of edgesByNode.get(portGlobalId) ?? []) {
    const other = this.getOtherEndpoint(edge, portGlobalId);
    if (other !== relationGlobalId && !this.isChildOf(other, relationGlobalId)) {
      return other;
    }
  }

  return null;

  },

  getNodeTypeAssignments(physical) {
  const assignments = new Map();
  for (const edge of physical.edges) {
    const sourceType = this.state.schema.nodeTypes.get(edge.sourceGlobalId);
    const targetType = this.state.schema.nodeTypes.get(edge.targetGlobalId);
    if (sourceType && !targetType) {
      assignments.set(edge.targetGlobalId, sourceType);
    } else if (targetType && !sourceType) {
      assignments.set(edge.sourceGlobalId, targetType);
    }
  }
  return assignments;

  },

  isRelationInstanceNode(node) {
  if (this.getAttribute(node, graphKindAttribute) === "edge-instance") {
    return true;
  }

  return this.isChildOf(node.name, this.getBasis().relationRoot)
    && node.name !== this.getBasis().relationRoot
    && !node.name.slice(this.getBasis().relationRoot.length + 1).includes("/");

  },

  isSchemaRootNode(globalId) {
  const basis = this.getBasis();
  return globalId === "graphdata"
    || globalId === "graphdata/types"
    || globalId === basis.nodeTypeRoot
    || globalId === basis.edgeTypeRoot
    || globalId === basis.relationRoot;

  },

  isChildOf(globalId, parentGlobalId) {
  return Boolean(parentGlobalId)
    && globalId.length > parentGlobalId.length
    && globalId.startsWith(`${parentGlobalId}/`);

  },

  getAttribute(node, key) {
  return node.attributes?.[key] ?? node.attributes?.[key.toLowerCase()] ?? "";

  },

  getLocalId(globalId) {
  const segments = this.parseGlobalId(globalId);
  return segments.length > 0 ? segments[segments.length - 1] : globalId;

  },

  toGraphType(node, fallbackElement = "node") {
  const fallbackRank = fallbackElement === "edge" ? 30 : 50;
  return {
    globalId: node.globalId,
    localId: node.localId,
    label: node.attributes?.label || node.localId,
    color: this.normalizeColor(node.attributes?.[projectionColorAttribute] || node.attributes?.color),
    element: node.attributes?.[graphElementAttribute] || fallbackElement,
    visible: String(node.attributes?.[projectionVisibleAttribute] ?? "true").toLowerCase() !== "false",
    infoAttribute: node.attributes?.[projectionInfoAttribute] || "",
    labelVisible: String(node.attributes?.[projectionLabelVisibleAttribute] ?? "true").toLowerCase() !== "false",
    directed: String(node.attributes?.[projectionDirectedAttribute] ?? node.attributes?.directed ?? "").toLowerCase() === "true",
    rank: this.readRank(node.attributes?.[projectionRankAttribute] ?? node.attributes?.rank, fallbackRank),
    attributes: node.attributes ?? {}
  };

  },

  readRank(value, fallback = 0) {
  const parsed = Number.parseFloat(value);
  return Number.isFinite(parsed) ? Math.max(0, parsed) : fallback;

  },

  roundRank(value) {
  return Math.round(value * 10) / 10;

  },

  formatRank(value) {
  return this.roundRank(value).toFixed(1);

  },

  formatRankInput(value) {
  return Number.isFinite(value) ? String(this.roundRank(value)) : "";

  },

  rankToRadius(rank) {
  return Math.round(Math.max(28, Math.min(48, nodeRadius + (rank - 55) * 0.14)));

  },

  normalizeColor(value) {
  if (!value || !/^#[0-9a-f]{6}$/i.test(value.trim())) {
    return "";
  }

  return value.trim();

  },

  edgeKey(a, b) {
  return a.localeCompare(b, "ru") < 0 ? `${a}\u0000${b}` : `${b}\u0000${a}`;

  },

  getOtherEndpoint(edge, nodeName) {
  return edge.sourceGlobalId === nodeName ? edge.targetGlobalId : edge.sourceGlobalId;

  },

  trimName(name, limit) {
  return name.length > limit ? `${name.slice(0, limit - 1)}…` : name;

  },

  parseCsv(value) {
  return value
    .split(",")
    .map(item => item.trim())
    .filter(Boolean);

  },

  readNumber(selector, fallback) {
  const value = Number.parseInt(this.document.querySelector(selector).value, 10);
  return Number.isFinite(value) ? value : fallback;

  },

  pointOnCircle(anchor, target, radius) {
  let dx = target.x - anchor.x;
  let dy = target.y - anchor.y;
  let distance = Math.hypot(dx, dy);
  if (distance < 0.01) {
    dx = 1;
    dy = 0;
    distance = 1;
  }

  return {
    x: anchor.x + (dx / distance) * radius,
    y: anchor.y + (dy / distance) * radius
  };

  }
};
