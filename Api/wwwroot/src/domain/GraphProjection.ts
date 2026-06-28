import { graphRoleAttribute } from "./graphAttributes.js";
import { GraphEdge } from "./GraphEdge.js";
import { GraphId } from "./GraphId.js";
import { GraphNode } from "./GraphNode.js";
import { GraphType } from "./GraphType.js";

import type { GraphModel, ProjectedGraph } from "./GraphModel.js";

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
    const hidden = new Set();
    for (const relation of hiddenRelations) {
      hidden.add(relation.relationGlobalId);
      relation.portGlobalIds.forEach(name => hidden.add(name));
    }
    for (const type of [...this.model.schema.nodeTypes.values(), ...this.model.schema.edgeTypes.values()]) {
      if (type.collapsed === true || type.visible === false) {
        hidden.add(type.path);
      }
    }

    const nodeTypeAssignments = this.nodeTypeAssignments(source);
    const visibleNodes = source.nodes
      .filter(node => !hidden.has(node.name ?? ""))
      .filter(node => nodeTypeAssignments.get(node.name ?? "")?.visible !== false);
    const visibleNodeIds = new Set(visibleNodes.map(node => node.name ?? ""));
    const typedNodes = visibleNodes.map(node => {
      const nodeType = nodeTypeAssignments.get(node.name ?? "");
      return {
        ...node,
        typeGlobalId: nodeType?.path,
        typeLabel: nodeType?.label,
        typeRank: nodeType?.rank,
        color: nodeType?.color,
        displayName: this.model.formatProjectedNodeName(node as any, nodeType ?? {})
      };
    });

    const hiddenPhysicalEdges = new Set();
    for (const relation of hiddenRelations) {
      relation.physicalEdgeKeys.forEach(key => hiddenPhysicalEdges.add(key));
    }
    for (const [nodeId, nodeType] of nodeTypeAssignments) {
      if (nodeType.collapsed === true || nodeType.visible === false) {
        hiddenPhysicalEdges.add(GraphEdge.keyFor(nodeId, nodeType.path));
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

  nodeTypeAssignmentsFromCache(): Map<string, GraphType> {
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
        viewRadius: GraphType.rankToRadius(rank),
        viewRankReason: reasons.join(", ")
      };
    });

    return { nodes: rankedNodes, edges: rankedEdges };
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

  nodeTypeAssignments(physical: ProjectedGraph): Map<string, GraphType> {
    const result = new Map();
    for (const edge of physical.edges) {
      const sourceType = this.model.schema.nodeTypes.get(edge.node1InternalId);
      const targetType = this.model.schema.nodeTypes.get(edge.node2InternalId);
      if (sourceType && !targetType) result.set(edge.node2InternalId, sourceType);
      else if (targetType && !sourceType) result.set(edge.node1InternalId, targetType);
    }
    return result;
  }
}
