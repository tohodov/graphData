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

  projectFromCache(): any {
    return this.projectFromIterables(this.model.primitiveNodeViews(), this.model.primitiveEdgeViews());
  }

  project(physical: any): any {
    return this.projectFromIterables(physical?.nodes ?? [], physical?.edges ?? []);
  }

  projectFromIterables(nodes: Iterable<any>, edges: Iterable<any>): any {
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
        hidden.add(type.globalId);
      }
    }

    const nodeTypeAssignments = this.nodeTypeAssignments(source);
    const visibleNodes = source.nodes
      .filter(node => !hidden.has(node.name))
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
    for (const relation of hiddenRelations) {
      relation.physicalEdgeKeys.forEach(key => hiddenPhysicalEdges.add(key));
    }
    for (const [nodeId, nodeType] of nodeTypeAssignments) {
      if (nodeType.collapsed === true || nodeType.visible === false) {
        hiddenPhysicalEdges.add(GraphEdge.keyFor(nodeId, nodeType.globalId));
      }
    }

    const physicalEdges = source.edges.filter(edge => {
      const sourceVisible = visibleNodeIds.has(edge.sourceGlobalId);
      const targetVisible = visibleNodeIds.has(edge.targetGlobalId);
      return (sourceVisible || targetVisible)
        && !hidden.has(edge.sourceGlobalId)
        && !hidden.has(edge.targetGlobalId)
        && !hiddenPhysicalEdges.has(edge.key ?? GraphEdge.keyFor(edge.sourceGlobalId, edge.targetGlobalId));
    });

    const projectedEdges = collapsedRelations
      .filter(relation => visibleNodeIds.has(relation.sourceGlobalId) && visibleNodeIds.has(relation.targetGlobalId))
      .filter(relation => relation.type?.visible !== false)
      .map(relation => {
        const key = "projected:" + relation.relationGlobalId;
        return {
          key,
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

  visiblePrimitiveGraph(nodes: Iterable<any>, edges: Iterable<any>): any {
    const primitiveNodes = [...nodes];
    const visibleNodeIds = new Set(
      primitiveNodes
        .filter(node => node.showed === true)
        .map(node => node.name)
    );
    return {
      nodes: primitiveNodes.filter(node => visibleNodeIds.has(node.name)),
      edges: [...edges].filter(edge =>
        visibleNodeIds.has(edge.sourceGlobalId) || visibleNodeIds.has(edge.targetGlobalId))
    };
  }

  relationsFromCache(): any[] {
    return this.relations(this.visiblePrimitiveGraph(this.model.primitiveNodeViews(), this.model.primitiveEdgeViews()));
  }

  nodeTypeAssignmentsFromCache(): Map<string, GraphType> {
    return this.nodeTypeAssignments(this.visiblePrimitiveGraph(this.model.primitiveNodeViews(), this.model.primitiveEdgeViews()));
  }

  shouldCollapseRelation(relation: any): boolean {
    return relation.collapsed === true || relation.type?.collapsed === true;
  }

  shouldHideRelation(relation: any): boolean {
    return relation.type?.visible === false;
  }

  rank(graph: any): any {
    const nodeStats = new Map<string, any>(graph.nodes.map((node: any) => [node.name, { weightedDegree: 0, focusBoost: 0, reasons: [] }]));
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
      .filter(node => {
        if (GraphNode.from(node).isRelationInstance(this.model.basis)) {
          return true;
        }

        const incident = edgesByNode.get(node.name) ?? [];
        return incident.some(edge => {
          const otherId = edge.sourceGlobalId === node.name ? edge.targetGlobalId : edge.sourceGlobalId;
          return this.model.schema.edgeTypes.has(otherId);
        });
      })
      .map(relation => {
        const incident = edgesByNode.get(relation.name) ?? [];
        const ports = incident
          .map(edge => nodesByName.get(edge.sourceGlobalId === relation.name ? edge.targetGlobalId : edge.sourceGlobalId))
          .filter(Boolean);
        const typePort = ports.find(port => port.attributes?.[graphRoleAttribute] === "type")
          ?? ports.find(port => this.model.schema.edgeTypes.has(port.name));
        const endpointPorts = ports.filter(port => port !== typePort);
        const sourcePort = endpointPorts.find(port => port.attributes?.[graphRoleAttribute] === "source") ?? endpointPorts[0];
        const targetPort = endpointPorts.find(port => port.attributes?.[graphRoleAttribute] === "target") ?? endpointPorts.find(port => port !== sourcePort);
        const sourceGlobalId = sourcePort ? this.portEndpoint(sourcePort.name, edgesByNode, relation.name) : null;
        const targetGlobalId = targetPort ? this.portEndpoint(targetPort.name, edgesByNode, relation.name) : null;
        const typeGlobalId = typePort?.attributes?.[graphRoleAttribute] === "type"
          ? this.portEndpoint(typePort.name, edgesByNode, relation.name)
          : typePort?.name;
        const type = typeGlobalId ? this.model.schema.edgeTypes.get(typeGlobalId) : null;
        const physicalEdgeKeys = new Set(incident.map(edge => edge.key ?? GraphEdge.keyFor(edge.sourceGlobalId, edge.targetGlobalId)));
        for (const port of ports) {
          for (const edge of edgesByNode.get(port.name) ?? []) {
            physicalEdgeKeys.add(edge.key ?? GraphEdge.keyFor(edge.sourceGlobalId, edge.targetGlobalId));
          }
        }
        return {
          relationGlobalId: relation.name,
          displayName: relation.displayName,
          collapsed: Boolean(relation.collapsed),
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
