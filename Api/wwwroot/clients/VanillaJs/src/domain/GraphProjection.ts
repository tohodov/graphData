import { graphRoleAttribute } from "./graphAttributes.js";
import { GraphEdge } from "./GraphEdge.js";
import { GraphId } from "./GraphId.js";
import { GraphNode } from "./GraphNode.js";
import { GraphType } from "./GraphType.js";

export class GraphProjection {
  model: any;
  constructor(model: any) {
    this.model = model;
  }

  project(physical: any): any {
    const relationInstances = this.relations(physical);
    const hidden = new Set();
    for (const relation of relationInstances) {
      hidden.add(relation.relationGlobalId);
      relation.portGlobalIds.forEach(name => hidden.add(name));
    }
    for (const type of [...this.model.schema.nodeTypes.values(), ...this.model.schema.edgeTypes.values()]) {
      hidden.add(type.globalId);
    }

    const nodeTypeAssignments = this.nodeTypeAssignments(physical);
    const visibleNodes = physical.nodes
      .filter(node => !hidden.has(node.name))
      .filter(node => !this.model.isSchemaRoot(node.name))
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
        displayName: this.model.formatProjectedNodeName(node, nodeType)
      };
    });

    const hiddenPhysicalEdges = new Set();
    for (const relation of relationInstances) {
      relation.physicalEdgeKeys.forEach(key => hiddenPhysicalEdges.add(key));
    }
    for (const [nodeId, nodeType] of nodeTypeAssignments) {
      hiddenPhysicalEdges.add(GraphEdge.keyFor(nodeId, nodeType.globalId));
    }

    const physicalEdges = physical.edges.filter(edge => {
      return visibleNodeIds.has(edge.sourceGlobalId)
        && visibleNodeIds.has(edge.targetGlobalId)
        && !hiddenPhysicalEdges.has(edge.key ?? GraphEdge.keyFor(edge.sourceGlobalId, edge.targetGlobalId));
    });

    const projectedEdges = relationInstances
      .filter(relation => visibleNodeIds.has(relation.sourceGlobalId) && visibleNodeIds.has(relation.targetGlobalId))
      .filter(relation => relation.type?.visible !== false)
      .map(relation => ({
        key: "projected:" + relation.relationGlobalId,
        sourceGlobalId: relation.sourceGlobalId,
        targetGlobalId: relation.targetGlobalId,
        sourceLocalId: GraphId.localId(relation.sourceGlobalId),
        targetLocalId: GraphId.localId(relation.targetGlobalId),
        relationGlobalId: relation.relationGlobalId,
        typeGlobalId: relation.type?.globalId,
        label: relation.type?.labelVisible === false ? "" : relation.type?.label ?? relation.displayName,
        color: relation.type?.color,
        directed: relation.type?.directed ?? false,
        typeRank: relation.type?.rank,
        projected: true
      }));

    return this.rank({ nodes: typedNodes, edges: [...physicalEdges, ...projectedEdges] });
  }

  rank(graph: any): any {
    const nodeStats = new Map(graph.nodes.map(node => [node.name, { weightedDegree: 0, focusBoost: 0, reasons: [] }]));
    const rankedEdges = graph.edges.map(edge => {
      const edgeType = edge.typeGlobalId ? this.model.schema.edgeTypes.get(edge.typeGlobalId) : null;
      const basisWeight = GraphType.readRank(edge.typeRank ?? edgeType?.rank, edge.projected ? 35 : 8);
      const rank = GraphType.roundRank(basisWeight);
      const sourceStats = nodeStats.get(edge.sourceGlobalId);
      const targetStats = nodeStats.get(edge.targetGlobalId);
      if (sourceStats) sourceStats.weightedDegree += basisWeight;
      if (targetStats) targetStats.weightedDegree += basisWeight;
      if (this.model.selectedName === edge.sourceGlobalId && targetStats) targetStats.focusBoost += Math.min(25, basisWeight * 0.35);
      if (this.model.selectedName === edge.targetGlobalId && sourceStats) sourceStats.focusBoost += Math.min(25, basisWeight * 0.35);
      return {
        ...edge,
        viewRank: rank,
        viewRankReason: edgeType?.label
          ? "edge type " + edgeType.label + ": " + GraphType.formatRank(rank)
          : "physical edge: " + GraphType.formatRank(rank)
      };
    });

    const rankedNodes = graph.nodes.map(node => {
      const stats = nodeStats.get(node.name);
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

  relations(physical: any): any[] {
    const nodesByName = new Map(physical.nodes.map(node => [node.name, node]));
    const edgesByNode = new Map();
    physical.edges.forEach(edge => {
      if (!edgesByNode.has(edge.sourceGlobalId)) edgesByNode.set(edge.sourceGlobalId, []);
      if (!edgesByNode.has(edge.targetGlobalId)) edgesByNode.set(edge.targetGlobalId, []);
      edgesByNode.get(edge.sourceGlobalId).push(edge);
      edgesByNode.get(edge.targetGlobalId).push(edge);
    });

    return physical.nodes
      .filter(node => GraphNode.from(node).isRelationInstance(this.model.basis))
      .map(relation => {
        const incident = edgesByNode.get(relation.name) ?? [];
        const ports = incident
          .map(edge => nodesByName.get(edge.sourceGlobalId === relation.name ? edge.targetGlobalId : edge.sourceGlobalId))
          .filter(Boolean);
        const sourcePort = ports.find(port => port.attributes?.[graphRoleAttribute] === "source") ?? ports[0];
        const targetPort = ports.find(port => port.attributes?.[graphRoleAttribute] === "target") ?? ports.find(port => port !== sourcePort);
        const typePort = ports.find(port => port.attributes?.[graphRoleAttribute] === "type")
          ?? ports.find(port => this.model.schema.edgeTypes.has(port.name));
        const sourceGlobalId = sourcePort ? this.portEndpoint(sourcePort.name, edgesByNode, relation.name) : null;
        const targetGlobalId = targetPort ? this.portEndpoint(targetPort.name, edgesByNode, relation.name) : null;
        const type = typePort ? this.model.schema.edgeTypes.get(typePort.name) : null;
        const physicalEdgeKeys = new Set(incident.map(edge => edge.key ?? GraphEdge.keyFor(edge.sourceGlobalId, edge.targetGlobalId)));
        for (const port of ports) {
          for (const edge of edgesByNode.get(port.name) ?? []) {
            physicalEdgeKeys.add(edge.key ?? GraphEdge.keyFor(edge.sourceGlobalId, edge.targetGlobalId));
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
      .filter(relation => relation.sourceGlobalId && relation.targetGlobalId);
  }

  portEndpoint(portGlobalId: string, edgesByNode: Map<string, any[]>, relationGlobalId: string): string | null {
    const edges = edgesByNode.get(portGlobalId) ?? [];
    const edge = edges.find(item => item.sourceGlobalId !== relationGlobalId && item.targetGlobalId !== relationGlobalId)
      ?? edges.find(item => item.sourceGlobalId !== portGlobalId || item.targetGlobalId !== relationGlobalId);
    if (!edge) return null;
    return edge.sourceGlobalId === portGlobalId ? edge.targetGlobalId : edge.sourceGlobalId;
  }

  nodeTypeAssignments(physical: any): Map<string, GraphType> {
    const result = new Map();
    for (const edge of physical.edges) {
      const sourceType = this.model.schema.nodeTypes.get(edge.sourceGlobalId);
      const targetType = this.model.schema.nodeTypes.get(edge.targetGlobalId);
      if (sourceType && !targetType) result.set(edge.targetGlobalId, sourceType);
      else if (targetType && !sourceType) result.set(edge.sourceGlobalId, targetType);
    }
    return result;
  }
}
