import { GraphEdge } from "./GraphEdge.js";
import { GraphId } from "./GraphId.js";
import { GraphType } from "./GraphType.js";

import type { GraphModel, ProjectedGraph } from "./GraphModel.js";
import type { GraphNode } from "./GraphNode.js";
import type { ProjectedGraphNode, ProjectedGraphNodeField } from "./GraphModel.js";

export type RelationInstance = {
  relationGlobalId: string;
  displayName: string;
  collapsed: boolean;
  node1InternalId: string | null;
  node2InternalId: string | null;
  type: GraphType | null | undefined;
  participantIds: string[];
  portGlobalIds: string[];
  physicalEdgeKeys: Set<string>;
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
    if (!this.hasDisplayRules()) {
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
        this.projectedNodeTypeFields(node, collapsedFacets));
    }

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

  hasDisplayRules(): boolean {
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
    return relation.participantIds.length === 2
      && (relation.collapsed === true || relation.type?.collapsed === true);
  }

  shouldHideRelation(relation: RelationInstance): boolean {
    return relation.type?.visible === false;
  }

  rank(graph: ProjectedGraph): ProjectedGraph {
    const nodeStats = new Map<string, { weightedDegree: number, focusBoost: number, reasons: string[] }>(graph.nodes.map((node: Record<string, unknown>) => [node.name as string, { weightedDegree: 0, focusBoost: 0, reasons: [] }]));
    const rankedEdges = graph.edges.map(edge => {
      const edgeType = edge.typeGlobalId ? this.model.schema.edgeTypes.get(edge.typeGlobalId) : null;
      const displayWeight = GraphType.readRank(edge.typeRank ?? edgeType?.rank, edge.projected ? 35 : 8);
      const rank = GraphType.roundRank(displayWeight);
      const sourceStats = nodeStats.get(edge.node1InternalId);
      const targetStats = nodeStats.get(edge.node2InternalId);
      if (sourceStats) sourceStats.weightedDegree += displayWeight;
      if (targetStats) targetStats.weightedDegree += displayWeight;
      if (this.model.selectedName === edge.node1InternalId && targetStats) targetStats.focusBoost += Math.min(25, displayWeight * 0.35);
      if (this.model.selectedName === edge.node2InternalId && sourceStats) sourceStats.focusBoost += Math.min(25, displayWeight * 0.35);
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
    const edgesByNode = new Map<string, import("./GraphModel.js").ProjectedGraphEdge[]>();
    physical.edges.forEach(edge => {
      if (!edgesByNode.has(edge.node1InternalId)) edgesByNode.set(edge.node1InternalId, []);
      if (!edgesByNode.has(edge.node2InternalId)) edgesByNode.set(edge.node2InternalId, []);
      edgesByNode.get(edge.node1InternalId)!.push(edge);
      edgesByNode.get(edge.node2InternalId)!.push(edge);
    });

    return physical.nodes
      .filter(node => this.isSemanticRelation(node))
      .map(relation => this.semanticRelation(relation, edgesByNode))
      .filter((relation): relation is RelationInstance => relation !== null);
  }

  isSemanticRelation(node: ProjectedGraphNode): boolean {
    return String(node.semanticKind ?? "").toLocaleLowerCase("en") === "relation";
  }

  semanticRelation(
    relation: ProjectedGraphNode,
    edgesByNode: Map<string, import("./GraphModel.js").ProjectedGraphEdge[]>
  ): RelationInstance | null {
    const relationGlobalId = relation.name ?? relation.path ?? relation.internalId ?? "";
    const participantIds = [...new Set((relation.relationMembers ?? [])
      .flatMap(member => member.participantIds ?? [])
      .filter(Boolean))];
    if (!relationGlobalId || participantIds.length === 0) {
      return null;
    }

    const typeGlobalId = (relation.typeIds ?? [])
      .find(typeId => this.model.schema.edgeTypes.has(typeId))
      ?? relation.typeIds?.[0];
    const incident = edgesByNode.get(relationGlobalId) ?? [];
    const typeIds = new Set(relation.typeIds ?? []);
    const participantSet = new Set(participantIds);
    const portGlobalIds = incident
      .map(edge => edge.node1InternalId === relationGlobalId ? edge.node2InternalId : edge.node1InternalId)
      .filter(id => id && !typeIds.has(id) && !participantSet.has(id));
    const physicalEdgeKeys = new Set(
      incident.map(edge => edge.key ?? GraphEdge.keyFor(edge.node1InternalId, edge.node2InternalId)));
    for (const portId of portGlobalIds) {
      for (const edge of edgesByNode.get(portId) ?? []) {
        physicalEdgeKeys.add(edge.key ?? GraphEdge.keyFor(edge.node1InternalId, edge.node2InternalId));
      }
    }
    return {
      relationGlobalId,
      displayName: relation.displayName ?? relation.localId ?? GraphId.localId(relationGlobalId),
      collapsed: Boolean(relation.collapsed),
      node1InternalId: participantIds.length === 2 ? participantIds[0] : null,
      node2InternalId: participantIds.length === 2 ? participantIds[1] : null,
      type: typeGlobalId ? this.model.schema.edgeTypes.get(typeGlobalId) : null,
      participantIds,
      portGlobalIds,
      physicalEdgeKeys
    };
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

    for (const node of physical.nodes) {
      const nodeId = node.name ?? "";
      if (!nodeId) {
        continue;
      }

      for (const typeId of node.typeIds ?? []) {
        const type = this.model.schema.nodeTypes.get(typeId);
        if (type) {
          addFacet(nodeId, type);
        }
      }
    }

    return result;
  }

  projectedNodeTypeFields(
    node: ProjectedGraphNode,
    nodeFacets: GraphType[]
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
        value: this.projectedFieldValue(node, field)
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
    const paths = new Set<string>();
    const singlePath = fieldNode.attributes?.nodeTypePath;
    if (singlePath) {
      paths.add(singlePath);
    }
    const serializedPaths = fieldNode.attributes?.allowedTypePaths;
    if (serializedPaths) {
      try {
        const parsed = JSON.parse(serializedPaths);
        if (Array.isArray(parsed)) {
          parsed.filter(path => typeof path === "string" && path).forEach(path => paths.add(path));
        }
      } catch {
        // Invalid metadata is ignored by the viewer; the domain API remains the validation boundary.
      }
    }

    return [...paths]
      .map(path => this.model.schema.nodeTypes.get(path))
      .filter((type): type is GraphType => Boolean(type));
  }

  projectedFieldValue(
    node: ProjectedGraphNode,
    field: ProjectedGraphNodeField
  ): string | undefined {
    // Node-valued fields currently have no canonical assignment record. An
    // arbitrary incident relation is not evidence that it fills this named
    // field, so the projection deliberately leaves the value empty.
    if (field.valueKind !== "Primitive") {
      return undefined;
    }

    return this.attributeValue(node.attributes ?? {}, field.name) ?? "";
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
