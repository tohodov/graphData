import { graphRoleAttribute } from "./graphAttributes.js";
import { GraphEdge } from "./GraphEdge.js";
import { GraphId } from "./GraphId.js";
import { GraphNode } from "./GraphNode.js";
import { GraphType } from "./GraphType.js";

import type { GraphModel, ProjectedGraph } from "./GraphModel.js";
import type { ProjectedGraphNode, ProjectedGraphNodeField } from "./GraphModel.js";

export type RelationInstance = {
  relationGlobalId: string;
  displayName: string;
  collapsed: boolean;
  node1InternalId: string | null;
  node2InternalId: string | null;
  type: GraphType | null | undefined;
  portGlobalIds: string[];
  physicalEdgeKeys: Set<string>;
};

type FieldValueConsumption = {
  nodeIds: Set<string>;
  edgeKeys: Set<string>;
};

export class GraphProjection {
  model: GraphModel;
  constructor(model: GraphModel) {
    this.model = model;
  }

  projectFromCache(): ProjectedGraph {
    return this.projectFromIterables(this.model.primitiveNodeViews(), this.model.primitiveEdgeViews());
  }

  project(physical: ProjectedGraph | null | undefined): ProjectedGraph {
    return this.projectFromIterables(physical?.nodes ?? [], physical?.edges ?? []);
  }

  projectFromIterables(nodes: Iterable<import("./GraphModel.js").ProjectedGraphNode | import("./GraphNode.js").GraphNode>, edges: Iterable<import("./GraphModel.js").ProjectedGraphEdge | import("./GraphEdge.js").GraphEdge>): ProjectedGraph {
    const source = this.visiblePrimitiveGraph(nodes, edges);
    if (!this.hasBasisRules()) {
      return this.rank(source);
    }

    const relationInstances = this.relations(source);
    const collapsedRelations = relationInstances.filter(relation => this.shouldCollapseRelation(relation));
    const hiddenRelations = relationInstances.filter(relation => this.shouldHideRelation(relation) || this.shouldCollapseRelation(relation));
    const nodeTypeAssignments = this.nodeTypeAssignments(source);
    const hidden = new Set();
    for (const relation of hiddenRelations) {
      hidden.add(relation.relationGlobalId);
      relation.portGlobalIds.forEach(name => hidden.add(name));
    }
    for (const type of [...this.model.schema.nodeTypes.values(), ...this.model.schema.edgeTypes.values()]) {
      if (type.collapsed === true || type.visible === false) {
        hidden.add(type.path);
        this.hiddenTypeDefinitionNodes(type.path, source.nodes).forEach(name => hidden.add(name));
      }
    }

    const consumedFieldValues: FieldValueConsumption = {
      nodeIds: new Set(),
      edgeKeys: new Set()
    };
    const recordFieldsByNode = new Map<string, ProjectedGraphNodeField[]>();
    for (const node of source.nodes) {
      const nodeName = node.name ?? "";
      const nodeFacets = nodeTypeAssignments.get(nodeName) ?? [];
      const collapsedFacets = nodeFacets.filter(type => type.visible !== false && type.collapsed === true);
      if (!nodeName || hidden.has(nodeName) || collapsedFacets.length === 0) {
        continue;
      }

      recordFieldsByNode.set(
        nodeName,
        this.projectedNodeTypeFields(node, collapsedFacets, source, nodeTypeAssignments, consumedFieldValues));
    }
    consumedFieldValues.nodeIds.forEach(name => hidden.add(name));

    const visibleNodes = source.nodes
      .filter(node => !hidden.has(node.name ?? ""))
      .filter(node => {
        const facets = nodeTypeAssignments.get(node.name ?? "") ?? [];
        return facets.length === 0 || facets.some(type => type.visible !== false);
      });
    const visibleNodeIds = new Set(visibleNodes.map(node => node.name ?? ""));
    const typedNodes = visibleNodes.map(node => {
      const nodeFacets = nodeTypeAssignments.get(node.name ?? "") ?? [];
      const visibleFacets = nodeFacets.filter(type => type.visible !== false);
      const collapsedFacets = visibleFacets.filter(type => type.collapsed === true);
      const primaryFacet = visibleFacets[0];
      const collapsedNodeType = collapsedFacets.length > 0;
      const typeFields = collapsedNodeType
        ? recordFieldsByNode.get(node.name ?? "") ?? []
        : [];
      return {
        ...node,
        typeGlobalId: primaryFacet?.path,
        typeLabel: visibleFacets.map(type => type.label).join(" · ") || undefined,
        typeRank: primaryFacet?.rank,
        color: primaryFacet?.color,
        viewShape: collapsedNodeType ? "record" as const : "circle" as const,
        typeFields: collapsedNodeType ? typeFields : undefined,
        displayName: this.model.formatProjectedNodeName(node as any, primaryFacet ?? {})
      };
    });

    const hiddenPhysicalEdges = new Set();
    for (const relation of hiddenRelations) {
      relation.physicalEdgeKeys.forEach(key => hiddenPhysicalEdges.add(key));
    }
    consumedFieldValues.edgeKeys.forEach(key => hiddenPhysicalEdges.add(key));
    for (const [nodeId, nodeFacets] of nodeTypeAssignments) {
      for (const nodeType of nodeFacets) {
        if (nodeType.collapsed === true || nodeType.visible === false) {
          hiddenPhysicalEdges.add(GraphEdge.keyFor(nodeId, nodeType.path));
        }
      }
    }

    const physicalEdges = source.edges.filter(edge => {
      const sourceVisible = visibleNodeIds.has(edge.node1InternalId);
      const targetVisible = visibleNodeIds.has(edge.node2InternalId);
      return (sourceVisible || targetVisible)
        && !hidden.has(edge.node1InternalId)
        && !hidden.has(edge.node2InternalId)
        && !hiddenPhysicalEdges.has(edge.key ?? GraphEdge.keyFor(edge.node1InternalId, edge.node2InternalId));
    });

    const projectedEdges = collapsedRelations
      .filter(relation => visibleNodeIds.has(relation.node1InternalId ?? "") && visibleNodeIds.has(relation.node2InternalId ?? ""))
      .filter(relation => relation.type?.visible !== false)
      .map(relation => {
        const key = "projected:" + relation.relationGlobalId;
        return {
          key,
          node1InternalId: relation.node1InternalId ?? "",
          node2InternalId: relation.node2InternalId ?? "",
          node1LocalId: GraphId.localId(relation.node1InternalId ?? ""),
          node2LocalId: GraphId.localId(relation.node2InternalId ?? ""),
          relationGlobalId: relation.relationGlobalId,
          typeGlobalId: relation.type?.path,
          label: relation.type?.labelVisible === false ? "" : relation.type?.label ?? relation.displayName,
          color: relation.type?.color,
          directed: relation.type?.directed ?? false,
          typeRank: relation.type?.rank,
          projected: true,
          collapsed: Boolean(relation.collapsed)
        };
      });

    return this.rank({ nodes: typedNodes, edges: [...physicalEdges, ...projectedEdges] });
  }

  hasBasisRules(): boolean {
    return (this.model.schema.nodeTypes?.size ?? 0) > 0
      || (this.model.schema.edgeTypes?.size ?? 0) > 0;
  }

  visiblePrimitiveGraph(nodes: Iterable<import("./GraphModel.js").ProjectedGraphNode | import("./GraphNode.js").GraphNode>, edges: Iterable<import("./GraphModel.js").ProjectedGraphEdge | import("./GraphEdge.js").GraphEdge>): ProjectedGraph {
    const primitiveNodes = [...nodes] as import("./GraphModel.js").ProjectedGraphNode[];
    const visibleNodeIds = new Set(
      primitiveNodes
        .filter(node => node.showed === true)
        .map(node => node.name ?? "")
    );
    return {
      nodes: primitiveNodes.filter(node => visibleNodeIds.has(node.name ?? "")),
      edges: ([...edges] as import("./GraphModel.js").ProjectedGraphEdge[]).filter(edge =>
        visibleNodeIds.has(edge.node1InternalId) || visibleNodeIds.has(edge.node2InternalId))
    };
  }

  relationsFromCache(): RelationInstance[] {
    return this.relations(this.visiblePrimitiveGraph(this.model.primitiveNodeViews(), this.model.primitiveEdgeViews()));
  }

  nodeTypeAssignmentsFromCache(): Map<string, GraphType[]> {
    return this.nodeTypeAssignments(this.visiblePrimitiveGraph(this.model.primitiveNodeViews(), this.model.primitiveEdgeViews()));
  }

  shouldCollapseRelation(relation: RelationInstance): boolean {
    return relation.collapsed === true || relation.type?.collapsed === true;
  }

  shouldHideRelation(relation: RelationInstance): boolean {
    return relation.type?.visible === false;
  }

  rank(graph: ProjectedGraph): ProjectedGraph {
    const nodeStats = new Map<string, { weightedDegree: number, focusBoost: number, reasons: string[] }>(graph.nodes.map((node: Record<string, unknown>) => [node.name as string, { weightedDegree: 0, focusBoost: 0, reasons: [] }]));
    const rankedEdges = graph.edges.map(edge => {
      const edgeType = edge.typeGlobalId ? this.model.schema.edgeTypes.get(edge.typeGlobalId) : null;
      const basisWeight = GraphType.readRank(edge.typeRank ?? edgeType?.rank, edge.projected ? 35 : 8);
      const rank = GraphType.roundRank(basisWeight);
      const sourceStats = nodeStats.get(edge.node1InternalId);
      const targetStats = nodeStats.get(edge.node2InternalId);
      if (sourceStats) sourceStats.weightedDegree += basisWeight;
      if (targetStats) targetStats.weightedDegree += basisWeight;
      if (this.model.selectedName === edge.node1InternalId && targetStats) targetStats.focusBoost += Math.min(25, basisWeight * 0.35);
      if (this.model.selectedName === edge.node2InternalId && sourceStats) sourceStats.focusBoost += Math.min(25, basisWeight * 0.35);
      return {
        ...edge,
        viewRank: rank,
        viewRankReason: edgeType?.label
          ? "edge type " + edgeType.label + ": " + GraphType.formatRank(rank)
          : "physical edge: " + GraphType.formatRank(rank)
      };
    });

    const rankedNodes = graph.nodes.map(node => {
      const stats = nodeStats.get(node.name ?? "");
      const typePriority = GraphType.readRank(node.typeRank, node.typeGlobalId ? 50 : 20);
      const degreeScore = Math.log1p(stats?.weightedDegree ?? 0) * 8;
      const rootBoost = node.name === this.model.rootName ? 18 : 0;
      const selectedBoost = node.name === this.model.selectedName ? 30 : 0;
      const rank = GraphType.roundRank(typePriority + degreeScore + rootBoost + selectedBoost + (stats?.focusBoost ?? 0));
      const typeFields = node.typeFields ?? [];
      const recordSize = node.viewShape === "record"
        ? this.recordNodeSize(node, typeFields)
        : null;
      const reasons = [
        node.typeLabel ? "type " + node.typeLabel + ": " + GraphType.formatRank(typePriority) : "untyped: " + GraphType.formatRank(typePriority),
        "links: " + GraphType.formatRank(degreeScore)
      ];
      if (rootBoost) reasons.push("root: " + GraphType.formatRank(rootBoost));
      if (selectedBoost) reasons.push("selected: " + GraphType.formatRank(selectedBoost));
      if (stats?.focusBoost) reasons.push("focus: " + GraphType.formatRank(stats.focusBoost));
      return {
        ...node,
        viewRank: rank,
        viewRadius: recordSize
          ? Math.ceil(Math.hypot(recordSize.width / 2, recordSize.height / 2))
          : GraphType.rankToRadius(rank),
        viewWidth: recordSize?.width,
        viewHeight: recordSize?.height,
        viewRankReason: reasons.join(", ")
      };
    });

    return { nodes: rankedNodes, edges: rankedEdges };
  }

  recordNodeSize(node: ProjectedGraphNode, typeFields: ProjectedGraphNodeField[]): { width: number; height: number } {
    const visibleFields = typeFields.slice(0, 6);
    const facetHeadings = new Set(visibleFields.map(field => field.facetPath)).size;
    const visibleFieldCount = visibleFields.length + facetHeadings + (typeFields.length > visibleFields.length ? 1 : 0);
    const labels = [
      node.displayName ?? node.localId ?? node.name ?? "",
      node.typeLabel ?? "",
      ...typeFields.map(field => [field.facetLabel, field.label, field.value, field.typeLabel].filter(Boolean).join(" "))
    ];
    const longest = Math.max(12, ...labels.map(label => String(label).length));
    return {
      width: Math.max(156, Math.min(286, longest * 7 + 34)),
      height: Math.max(92, 54 + visibleFieldCount * 22)
    };
  }

  relations(physical: ProjectedGraph): RelationInstance[] {
    const nodesByName = new Map(physical.nodes.map(node => [node.name, node]));
    const edgesByNode = new Map<string, import("./GraphModel.js").ProjectedGraphEdge[]>();
    physical.edges.forEach(edge => {
      if (!edgesByNode.has(edge.node1InternalId)) edgesByNode.set(edge.node1InternalId, []);
      if (!edgesByNode.has(edge.node2InternalId)) edgesByNode.set(edge.node2InternalId, []);
      edgesByNode.get(edge.node1InternalId)!.push(edge);
      edgesByNode.get(edge.node2InternalId)!.push(edge);
    });

    return physical.nodes
      .filter(node => {
        if (GraphNode.from(node).isRelationInstance(this.model.basis)) {
          return true;
        }

        const incident = edgesByNode.get(node.name ?? "") ?? [];
        return incident.some(edge => {
          const otherId = edge.node1InternalId === node.name ? edge.node2InternalId : edge.node1InternalId;
          return this.model.schema.edgeTypes.has(otherId);
        });
      })
      .map(relation => {
        const incident = edgesByNode.get(relation.name ?? "") ?? [];
        const ports = incident
          .map(edge => nodesByName.get(edge.node1InternalId === relation.name ? edge.node2InternalId : edge.node1InternalId))
          .filter(Boolean);
        const typePort = ports.find(port => port?.attributes?.[graphRoleAttribute] === "type")
          ?? ports.find(port => port && this.model.schema.edgeTypes.has(port.name ?? ""));
        const endpointPorts = ports.filter(port => port !== typePort);
        const sourcePort = endpointPorts.find(port => port?.attributes?.[graphRoleAttribute] === "source") ?? endpointPorts[0];
        const targetPort = endpointPorts.find(port => port?.attributes?.[graphRoleAttribute] === "target") ?? endpointPorts.find(port => port !== sourcePort);
        const node1InternalId = sourcePort ? this.portEndpoint(sourcePort.name ?? "", edgesByNode, relation.name ?? "") : null;
        const node2InternalId = targetPort ? this.portEndpoint(targetPort.name ?? "", edgesByNode, relation.name ?? "") : null;
        const typeGlobalId = typePort?.attributes?.[graphRoleAttribute] === "type"
          ? this.portEndpoint(typePort.name ?? "", edgesByNode, relation.name ?? "")
          : typePort?.name;
        const type = typeGlobalId ? this.model.schema.edgeTypes.get(typeGlobalId) : null;
        const physicalEdgeKeys = new Set(incident.map(edge => edge.key ?? GraphEdge.keyFor(edge.node1InternalId, edge.node2InternalId)));
        for (const port of ports) {
          for (const edge of edgesByNode.get(port?.name ?? "") ?? []) {
            physicalEdgeKeys.add(edge.key ?? GraphEdge.keyFor(edge.node1InternalId, edge.node2InternalId));
          }
        }
        return {
          relationGlobalId: relation.name ?? "",
          displayName: relation.displayName ?? "",
          collapsed: Boolean(relation.collapsed),
          node1InternalId,
          node2InternalId,
          type,
          portGlobalIds: ports.map(port => port?.name ?? ""),
          physicalEdgeKeys
        };
      })
      .filter(relation => relation.node1InternalId && relation.node2InternalId);
  }

  portEndpoint(portGlobalId: string, edgesByNode: Map<string, import("./GraphModel.js").ProjectedGraphEdge[]>, relationGlobalId: string): string | null {
    const edges = edgesByNode.get(portGlobalId) ?? [];
    const edge = edges.find(item => item.node1InternalId !== relationGlobalId && item.node2InternalId !== relationGlobalId)
      ?? edges.find(item => item.node1InternalId !== portGlobalId || item.node2InternalId !== relationGlobalId);
    if (!edge) return null;
    return edge.node1InternalId === portGlobalId ? edge.node2InternalId : edge.node1InternalId;
  }

  nodeTypeAssignments(physical: ProjectedGraph): Map<string, GraphType[]> {
    const result = new Map<string, GraphType[]>();
    const addFacet = (nodeId: string, type: GraphType) => {
      const facets = result.get(nodeId) ?? [];
      if (!facets.some(existing => existing.path === type.path)) {
        facets.push(type);
        facets.sort((left, right) => left.path.localeCompare(right.path, "ru"));
      }
      result.set(nodeId, facets);
    };
    for (const edge of physical.edges) {
      const sourceType = this.model.schema.nodeTypes.get(edge.node1InternalId);
      const targetType = this.model.schema.nodeTypes.get(edge.node2InternalId);
      if (sourceType && !targetType) addFacet(edge.node2InternalId, sourceType);
      else if (targetType && !sourceType) addFacet(edge.node1InternalId, targetType);
    }
    return result;
  }

  hiddenTypeDefinitionNodes(typePath: string, nodes: ProjectedGraphNode[]): string[] {
    const definitionPath = `${typePath}/Definition`;
    return nodes
      .map(node => node.name ?? node.path ?? "")
      .filter(name => name === definitionPath || GraphId.isChildOf(name, definitionPath));
  }

  projectedNodeTypeFields(
    node: ProjectedGraphNode,
    nodeFacets: GraphType[],
    source: ProjectedGraph,
    nodeTypeAssignments: Map<string, GraphType[]>,
    consumed: FieldValueConsumption
  ): ProjectedGraphNodeField[] {
    const result: ProjectedGraphNodeField[] = [];
    for (const nodeType of nodeFacets) {
      const fields = new Map<string, ProjectedGraphNodeField>();
      for (const field of this.definitionFields(nodeType.path, "Fields")) {
        fields.set(field.name.toLocaleLowerCase("ru"), field);
      }
      for (const field of this.definitionFields(nodeType.path, "Slots")) {
        const key = field.name.toLocaleLowerCase("ru");
        if (!fields.has(key)) {
          fields.set(key, field);
        }
      }
      result.push(...[...fields.values()].map(field => ({
        ...field,
        facetPath: nodeType.path,
        facetLabel: nodeType.label,
        value: this.projectedFieldValue(node, field, source, nodeTypeAssignments, consumed)
      })));
    }
    return result;
  }

  definitionFields(typePath: string, containerName: "Fields" | "Slots"): ProjectedGraphNodeField[] {
    const containerPath = `${typePath}/Definition/${containerName}`;
    const fields = [...this.model.loaded.values()]
      .filter(node => this.isDirectChild(node.name ?? node.path, containerPath))
      .map(node => {
        const allowedTypes = this.fieldAllowedTypes(node);
        return {
          facetPath: "",
          facetLabel: "",
          name: node.localId ?? GraphId.localId(node.name ?? node.path),
          label: node.displayName ?? node.localId ?? GraphId.localId(node.name ?? node.path),
          valueKind: node.attributes?.valueKind ?? (containerName === "Slots" ? "Node" : undefined),
          typeGlobalId: allowedTypes.map(type => type.path).join("|") || undefined,
          typeLabel: allowedTypes.map(type => type.label).join(" | ") || undefined,
          cardinality: this.fieldCardinality(node.attributes ?? {}),
          isCollection: String(node.attributes?.isCollection ?? "").toLowerCase() === "true"
        };
      });
    return fields.sort((left, right) => left.label.localeCompare(right.label, "ru"));
  }

  isDirectChild(path: string | null | undefined, parentPath: string): boolean {
    if (!path || !GraphId.isChildOf(path, parentPath)) {
      return false;
    }

    const parentSegments = parentPath.split("/").filter(Boolean).length;
    const segments = String(path).split("/").filter(Boolean).length;
    return segments === parentSegments + 1;
  }

  fieldAllowedTypes(fieldNode: GraphNode): GraphType[] {
    return (fieldNode.edges ?? [])
      .map(edge => edge.otherEndpoint(fieldNode.name))
      .map(path => this.model.schema.nodeTypes.get(path))
      .filter((type): type is GraphType => Boolean(type));
  }

  projectedFieldValue(
    node: ProjectedGraphNode,
    field: ProjectedGraphNodeField,
    source: ProjectedGraph,
    nodeTypeAssignments: Map<string, GraphType[]>,
    consumed: FieldValueConsumption
  ): string | undefined {
    const value = this.attributeValue(node.attributes ?? {}, field.name);
    if (value) {
      return value;
    }

    if (field.valueKind === "Primitive") {
      return "";
    }

    return this.linkedNodeFieldValue(node, field, source, nodeTypeAssignments, consumed);
  }

  linkedNodeFieldValue(
    node: ProjectedGraphNode,
    field: ProjectedGraphNodeField,
    source: ProjectedGraph,
    nodeTypeAssignments: Map<string, GraphType[]>,
    consumed: FieldValueConsumption
  ): string | undefined {
    const nodeName = node.name ?? "";
    const allowedTypeIds = new Set((field.typeGlobalId ?? "").split("|").filter(Boolean));
    const nodeByName = new Map(source.nodes.map(item => [item.name ?? "", item]));
    const values = source.edges
      .filter(edge => edge.node1InternalId === nodeName || edge.node2InternalId === nodeName)
      .map(edge => ({
        edge,
        neighborId: edge.node1InternalId === nodeName ? edge.node2InternalId : edge.node1InternalId
      }))
      .filter(match => match.neighborId && !this.model.schema.nodeTypes.has(match.neighborId) && !this.model.schema.edgeTypes.has(match.neighborId))
      .filter(match => this.linkedNodeMatchesField(match.neighborId, allowedTypeIds, nodeTypeAssignments))
      .map(match => {
        consumed.nodeIds.add(match.neighborId);
        consumed.edgeKeys.add(match.edge.key ?? GraphEdge.keyFor(match.edge.node1InternalId, match.edge.node2InternalId));
        return nodeByName.get(match.neighborId)?.displayName ?? this.model.displayName(match.neighborId);
      })
      .filter(Boolean)
      .sort((left, right) => left.localeCompare(right, "ru"));

    if (values.length === 0) {
      return undefined;
    }

    return field.isCollection
      ? [...new Set(values)].join(", ")
      : values[0];
  }

  linkedNodeMatchesField(neighborId: string, allowedTypeIds: Set<string>, nodeTypeAssignments: Map<string, GraphType[]>): boolean {
    if (allowedTypeIds.size === 0) {
      return true;
    }

    const assignedTypes = nodeTypeAssignments.get(neighborId) ?? this.assignedNodeTypesFromCache(neighborId);
    return assignedTypes.some(type => allowedTypeIds.has(type.path));
  }

  assignedNodeTypesFromCache(nodeId: string): GraphType[] {
    const node = this.model.loaded.get(nodeId);
    if (!node) {
      return [];
    }

    return (node.edges ?? [])
      .map(edge => this.model.schema.nodeTypes.get(edge.otherEndpoint(node.name)))
      .filter((type): type is GraphType => Boolean(type));
  }

  attributeValue(attributes: Record<string, string>, key: string): string | undefined {
    if (attributes[key]) {
      return attributes[key];
    }

    const match = Object.keys(attributes).find(name => name.localeCompare(key, "ru", { sensitivity: "accent" }) === 0);
    return match ? attributes[match] : undefined;
  }

  fieldCardinality(attributes: Record<string, string>): string | undefined {
    const min = attributes.min;
    const max = attributes.max;
    if (!min && !max) {
      return undefined;
    }

    if (!max) {
      return `${min || "0"}..*`;
    }

    return min === max ? min : `${min || "0"}..${max}`;
  }
}
