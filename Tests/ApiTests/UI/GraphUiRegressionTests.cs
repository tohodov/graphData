using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Jint;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Ui;

[RelevantTestClass]
public sealed class GraphUiRegressionTests {
    [TestMethod]
    public void GraphModel_CollapsedEdgesStayInGraphForEndpointControls() {
        var engine = CreateUiEngine(
            ("Api/wwwroot/src/domain/GraphEdge.js", "GraphEdge"),
            ("Api/wwwroot/src/domain/GraphModel.js", "GraphModel"));

        engine.Execute(
            """
            const edge = new GraphEdge({
              node1InternalId: "a",
              node2InternalId: "b",
              node1LocalId: "a",
              node2LocalId: "b"
            });
            const model = new GraphModel();
            model.loaded.set("a", {
              name: "a",
              toViewNode() { return { name: "a", displayName: "A" }; },
              edges: [edge]
            });
            model.loaded.set("b", {
              name: "b",
              toViewNode() { return { name: "b", displayName: "B" }; },
              edges: []
            });

            const before = model.visibleGraph().edges[0].collapsed === false
              && model.collapsedEdges === undefined;
            model.collapseEdge(edge);
            const collapsedGraph = model.visibleGraph();
            const collapsed = collapsedGraph.nodes.length === 2
              && collapsedGraph.edges.length === 1
              && collapsedGraph.edges[0].collapsed === true
              && edge.collapsed === true
              && model.isEdgeCollapsed(edge);
            model.expandEdge(edge);
            const expanded = model.visibleGraph().edges[0].collapsed === false
              && edge.collapsed === false
              && !model.isEdgeCollapsed(edge);
            model.collapseEdge(edge);
            const replacement = GraphEdge.mergeMany([edge], [new GraphEdge({
              node1InternalId: "a",
              node2InternalId: "b",
              node1LocalId: "a",
              node2LocalId: "b"
            })])[0];
            const mergePreservesObjectState = replacement.collapsed === true;

            globalThis.__result = before && collapsed && expanded && mergePreservesObjectState;
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void GraphModel_VisibleEdgesOwnEndpointControls() {
        var engine = CreateUiEngine(
            ("Api/wwwroot/src/domain/GraphEdge.js", "GraphEdge"),
            ("Api/wwwroot/src/domain/GraphModel.js", "GraphModel"));

        engine.Execute(
            """
            const edge = new GraphEdge({
              node1InternalId: "a",
              node2InternalId: "b",
              node1LocalId: "a",
              node2LocalId: "b-local"
            });

            function loadedNode(name, edges) {
              return {
                name,
                displayName: name,
                showed: true,
                edges,
                toViewNode() { return { name, globalId: name, displayName: name, showed: true }; }
              };
            }

            const loadedModel = new GraphModel();
            loadedModel.loaded.set("a", loadedNode("a", [edge]));
            loadedModel.loaded.set("b", loadedNode("b", [edge]));
            const loadedControls = loadedModel.visibleGraph().edges[0].controls;
            loadedModel.collapseEdge(edge);
            const collapsedControls = loadedModel.visibleGraph().edges[0].controls;

            edge.collapsed = false;
            const oneEndpointModel = new GraphModel();
            oneEndpointModel.loaded.set("a", loadedNode("a", [edge]));
            const oneEndpointControls = oneEndpointModel.visibleGraph().edges[0].controls;

            globalThis.__result = loadedControls.length === 2
              && loadedControls.every(control => control.action === "collapse-edge")
              && loadedControls.map(control => control.anchorName).sort().join(",") === "a,b"
              && collapsedControls.length === 2
              && collapsedControls.every(control => control.action === "expand-edge")
              && oneEndpointControls.length === 1
              && oneEndpointControls[0].action === "load-neighbor"
              && oneEndpointControls[0].anchorName === "a"
              && oneEndpointControls[0].neighborLocalId === "b-local";
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void GraphModel_TracksNodeAndEdgeSelectionTogether() {
        var engine = CreateUiEngine(
            ("Api/wwwroot/src/domain/GraphEdge.js", "GraphEdge"),
            ("Api/wwwroot/src/domain/GraphModel.js", "GraphModel"));

        engine.Execute(
            """
            const edge = new GraphEdge({
              node1InternalId: "a",
              node2InternalId: "b",
              node1LocalId: "a",
              node2LocalId: "b"
            });
            const otherEdge = new GraphEdge({
              node1InternalId: "b",
              node2InternalId: "c",
              node1LocalId: "b",
              node2LocalId: "c"
            });
            const model = new GraphModel();
            model.loaded.set("a", { name: "a", edges: [edge] });
            model.loaded.set("b", { name: "b", edges: [edge, otherEdge] });
            model.loaded.set("c", { name: "c", edges: [otherEdge] });

            model.selectedName = "a";
            const singleNodeClearsEdges = model.isSelectedName("a")
              && model.selectedEdgeKeys.size === 0
              && model.edgeKey({}) === "";

            model.addSelectedName("b");
            model.addSelectedEdge(edge);
            const mixedSelection = model.selectedName === "b"
              && model.isSelectedName("a")
              && model.isSelectedName("b")
              && model.isSelectedEdge(edge);

            model.selectOnlyEdge(otherEdge);
            const onlyEdge = model.selectedName === null
              && model.selectedNames.size === 0
              && model.isSelectedEdge(otherEdge)
              && !model.isSelectedEdge(edge);

            model.selectElements(["a"], [edge.key]);
            const boxedSelection = model.selectedName === "a"
              && model.isSelectedName("a")
              && model.isSelectedEdge(edge)
              && !model.isSelectedEdge(otherEdge);

            model.selectElements(["b"], [otherEdge.key], { append: true });
            const appendedSelection = model.selectedName === "b"
              && model.isSelectedName("a")
              && model.isSelectedName("b")
              && model.isSelectedEdge(edge)
              && model.isSelectedEdge(otherEdge);

            model.removeSelectedEdgesConnectedTo("b");
            const prunedEdges = model.selectedEdgeKeys.size === 0;

            globalThis.__result = singleNodeClearsEdges
              && mixedSelection
              && onlyEdge
              && boxedSelection
              && appendedSelection
              && prunedEdges;
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void GraphModel_PositionMapStoresCoordinatesOnNodes() {
        var engine = CreateUiEngine(
            ("Api/wwwroot/src/domain/GraphNode.js", "GraphNode"),
            ("Api/wwwroot/src/domain/GraphModel.js", "GraphModel"));

        engine.Execute(
            """
            const model = new GraphModel();
            model.loaded.set("a", new GraphNode({ globalId: "a" }));
            model.positions.set("a", { x: 10, y: 20 });

            model.positions.set("b", { x: -5, y: 7 });
            model.loaded.set("b", new GraphNode({ globalId: "b" }));
            const unknownPositionIgnored = model.positions.get("b") === undefined
              && model.loaded.get("b").position === null;
            model.positions.set("b", { x: -5, y: 7 });
            const attachedPosition = model.positions.get("b");
            const positionAttachedToNode = attachedPosition === model.loaded.get("b").position;

            const deleted = model.positions.delete("a");
            model.positions.set("a", { x: 1, y: 2 });
            model.positions.clear();

            globalThis.__result = model.loaded.get("a").position === null
              && deleted === true
              && unknownPositionIgnored
              && positionAttachedToNode
              && attachedPosition.x === -5
              && attachedPosition.y === 7
              && !model.positions.has("a")
              && !model.positions.has("b");
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void GraphViewer_LoadedButNotShowedNeighborsRemainExpandableUntilShown() {
        var engine = CreateUiEngine(
            ("Api/wwwroot/src/domain/GraphEdge.js", "GraphEdge"),
            ("Api/wwwroot/src/domain/GraphNode.js", "GraphNode"),
            ("Api/wwwroot/src/domain/GraphProjection.js", "GraphProjection"),
            ("Api/wwwroot/src/domain/GraphModel.js", "GraphModel"),
            ("Api/wwwroot/src/GraphViewer.js", "GraphViewer"));

        engine.Execute(
            """
            const edge = new GraphEdge({
              node1InternalId: "a",
              node2InternalId: "b",
              node1LocalId: "a",
              node2LocalId: "b-local"
            });
            const model = new GraphModel();
            model.rootName = "a";
            model.loaded.set("a", new GraphNode({
              globalId: "a",
              displayName: "A",
              showed: true,
              edges: [edge]
            }));
            model.loaded.set("b", new GraphNode({
              globalId: "b",
              displayName: "B",
              showed: false,
              edges: []
            }));
            model.positions.set("a", { x: 10, y: 20 });
            model.velocities.set("a", { x: 0, y: 0 });

            const viewer = Object.create(GraphViewer.prototype);
            const calls = [];
            viewer.graph = model;
            viewer.loadNeighbor = () => calls.push("load");
            viewer.stopSimulation = () => calls.push("stop");
            viewer.render = () => calls.push("render");
            viewer.renderTypeControls = () => calls.push("types");
            viewer.setStatus = message => calls.push("status:" + message);
            viewer.displayName = id => model.displayName(id);

            viewer.refreshEdgeAngles();
            const before = model.visibleGraph();
            const control = before.edges[0].controls[0];
            const beforeSourceNodeName = before.edges[0].node1?.name;
            const beforeTargetNodeName = before.edges[0].node2?.name;
            const beforeSourceVisible = before.edges[0].node1?.showed === true;
            const beforeTargetVisible = before.edges[0].node2?.showed === true;
            viewer.handleEdgeControl(before.edges[0], control);
            const after = model.visibleGraph();
            const position = model.positions.get("b");
            const nodePosition = model.loaded.get("b").position;

            globalThis.__debug = {
              hasLoadedHiddenNode: model.hasNode("b"),
              hiddenBeforeRender: !before.nodes.some(node => node.name === "b"),
              singleVisibleEdge: before.edges.length === 1,
              sourceNodeLinked: beforeSourceNodeName === "a",
              targetNodeLinked: beforeTargetNodeName === "b",
              sourceVisibleFromNode: beforeSourceVisible,
              targetHiddenFromNode: !beforeTargetVisible,
              loadControl: control.action === "load-neighbor",
              controlTargetsHiddenNode: control.otherName === "b",
              didNotLoadAgain: !calls.includes("load"),
              visibleAfterReveal: model.isNodeVisible("b"),
              renderedAfterReveal: after.nodes.some(node => node.name === "b"),
              collapseControlsAfterReveal: after.edges[0].controls.every(item => item.action === "collapse-edge"),
              parentAssigned: model.parentByNode.get("b") === "a",
              selectedRevealedNode: model.selectedName === "b",
              stoppedSimulation: calls.includes("stop"),
              rendered: calls.includes("render"),
              renderedTypes: calls.includes("types"),
              statusUpdated: calls.includes("status:Развернуто узлов: 2"),
              seededX: Math.abs(position.x - 10) < 0.000001,
              seededY: Math.abs(position.y + 184) < 0.000001,
              positionOwnedByNode: nodePosition === position
            };
            globalThis.__result = Object.values(globalThis.__debug).every(Boolean);
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean(), engine.Evaluate("JSON.stringify(__debug)").AsString());
    }

    [TestMethod]
    public void GraphViewer_BackgroundLoadKeepsOnlyPositionedNodesShowed() {
        var engine = CreateUiEngine(
            ("Api/wwwroot/src/domain/GraphEdge.js", "GraphEdge"),
            ("Api/wwwroot/src/domain/GraphNode.js", "GraphNode"),
            ("Api/wwwroot/src/domain/GraphProjection.js", "GraphProjection"),
            ("Api/wwwroot/src/domain/GraphModel.js", "GraphModel"),
            ("Api/wwwroot/src/GraphViewer.js", "GraphViewer"));

        engine.Execute(
            """
            const edge = new GraphEdge({
              node1InternalId: "root",
              node2InternalId: "root/types",
              node1LocalId: "root",
              node2LocalId: "types",
              neighborLocalId: "types"
            });
            const model = new GraphModel();
            model.rootName = "root";
            model.loaded.set("root", new GraphNode({
              globalId: "root",
              displayName: "Root",
              showed: true,
              edges: [edge]
            }));
            model.loaded.set("root/types", new GraphNode({
              globalId: "root/types",
              displayName: "Types",
              showed: true,
              edges: [edge]
            }));
            model.positions.set("root", { x: 10, y: 20 });
            model.velocities.set("root", { x: 0, y: 0 });

            const viewer = Object.create(GraphViewer.prototype);
            viewer.graph = model;
            viewer.normalizeNodeResponse = node => GraphNode.fromApi(node);
            viewer.normalizeEdgeResponse = edge => GraphEdge.fromApi(edge);
            viewer.mergeEdges = (left, right) => GraphEdge.mergeMany(left, right);
            viewer.renderTypeControls = () => {};

            viewer.mergeSubgraphIntoViewer({
              nodes: [
                { globalId: "root", displayName: "Root", edges: [edge] },
                { globalId: "root/types", displayName: "Types", edges: [edge] }
              ],
              edges: [edge]
            }, { select: false, showed: false });

            const root = model.loaded.get("root");
            const types = model.loaded.get("root/types");
            const graph = model.visibleGraph();
            const control = graph.edges[0].controls[0];

            globalThis.__debug = {
              rootShowed: root.showed === true,
              typesHidden: types.showed === false,
              rootPositioned: model.positions.has("root"),
              rootOwnsPosition: root.position === model.positions.get("root"),
              typesUnpositioned: !model.positions.has("root/types"),
              typesOwnsNoPosition: types.position === null,
              oneNode: graph.nodes.length === 1,
              rootRendered: graph.nodes[0].name === "root",
              oneEdge: graph.edges.length === 1,
              loadControl: control.action === "load-neighbor",
              controlAnchor: control.anchorName === "root",
              controlTarget: control.otherName === "root/types",
              neighborLocalId: control.neighborLocalId === "types",
              finiteAngle: Number.isFinite(control.angle)
            };
            globalThis.__result = Object.values(globalThis.__debug).every(Boolean);
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean(), engine.Evaluate("JSON.stringify(__debug)").AsString());
    }

    [TestMethod]
    public void GraphViewer_BasisCacheNodesRequireExplicitShowedTrue() {
        var engine = CreateUiEngine(
            ("Api/wwwroot/src/domain/GraphEdge.js", "GraphEdge"),
            ("Api/wwwroot/src/domain/GraphNode.js", "GraphNode"),
            ("Api/wwwroot/src/domain/GraphProjection.js", "GraphProjection"),
            ("Api/wwwroot/src/domain/GraphModel.js", "GraphModel"),
            ("Api/wwwroot/src/GraphViewer.js", "GraphViewer"));

        engine.Execute(
            """
            const model = new GraphModel();
            model.rootName = "workspace";
            model.loaded.set("workspace", new GraphNode({
              globalId: "workspace",
              displayName: "Workspace",
              showed: true
            }));
            model.positions.set("workspace", { x: 10, y: 20 });

            model.loaded.set("graphdata/types/nodes/Opened", new GraphNode({
              globalId: "graphdata/types/nodes/Opened",
              displayName: "Opened",
              showed: true
            }));
            model.positions.set("graphdata/types/nodes/Opened", { x: 30, y: 40 });

            const viewer = Object.create(GraphViewer.prototype);
            viewer.graph = model;

            viewer.storeBasisGraphNodes({
              nodes: new Map([
                ["graphdata/types/nodes", new GraphNode({
                  globalId: "graphdata/types/nodes",
                  displayName: "Node types"
                })],
                ["graphdata/types/nodes/Hidden", new GraphNode({
                  globalId: "graphdata/types/nodes/Hidden",
                  displayName: "Hidden"
                })],
                ["graphdata/types/nodes/Opened", new GraphNode({
                  globalId: "graphdata/types/nodes/Opened",
                  displayName: "Opened refreshed"
                })]
              ])
            });

            const root = model.loaded.get("workspace");
            const rootType = model.loaded.get("graphdata/types/nodes");
            const hiddenType = model.loaded.get("graphdata/types/nodes/Hidden");
            const openedType = model.loaded.get("graphdata/types/nodes/Opened");
            const graph = model.visibleGraph();
            const visibleNames = graph.nodes.map(node => node.name).sort().join(",");

            globalThis.__debug = {
              rootVisible: root.showed === true,
              rootTypeCached: Boolean(rootType),
              rootTypeHidden: rootType.showed !== true,
              rootTypeUnpositioned: !model.positions.has("graphdata/types/nodes"),
              hiddenTypeCached: Boolean(hiddenType),
              hiddenTypeHidden: hiddenType.showed !== true,
              hiddenTypeUnpositioned: !model.positions.has("graphdata/types/nodes/Hidden"),
              openedStillVisible: openedType.showed === true,
              openedStillPositioned: model.positions.has("graphdata/types/nodes/Opened"),
              onlyExplicitlyShownRendered: visibleNames === "graphdata/types/nodes/Opened,workspace"
            };
            globalThis.__result = Object.values(globalThis.__debug).every(Boolean);
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean(), engine.Evaluate("JSON.stringify(__debug)").AsString());
    }

    [TestMethod]
    public void GraphProjection_KeepsFrontierEdgesForLazyLoading() {
        var engine = CreateUiEngine(
            ("Api/wwwroot/src/domain/GraphEdge.js", "GraphEdge"),
            ("Api/wwwroot/src/domain/GraphNode.js", "GraphNode"),
            ("Api/wwwroot/src/domain/GraphProjection.js", "GraphProjection"),
            ("Api/wwwroot/src/domain/GraphModel.js", "GraphModel"));

        engine.Execute(
            """
            const model = new GraphModel();
            const edge = new GraphEdge({
              node1InternalId: "root",
              node2InternalId: "root/child",
              node1LocalId: "root",
              node2LocalId: "child",
              neighborLocalId: "child"
            });
            model.rootName = "root";
            model.putNode(new GraphNode({
              globalId: "root",
              displayName: "Root",
              showed: true,
              edges: [edge]
            }));
            model.positions.set("root", { x: 10, y: 20 });

            const graph = model.visibleGraph();
            const control = graph.edges[0]?.controls?.[0];

            globalThis.__result = graph.nodes.length === 1
              && graph.nodes[0].name === "root"
              && graph.edges.length === 1
              && graph.edges[0].node1InternalId === "root"
              && graph.edges[0].node2InternalId === "root/child"
              && control?.action === "load-neighbor"
              && control?.anchorName === "root"
              && control?.neighborLocalId === "child";
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void GraphModel_ProjectedEdgeStateIsStoredOnTypedEdgeNode() {
        var engine = CreateUiEngine(
            ("Api/wwwroot/src/domain/GraphEdge.js", "GraphEdge"),
            ("Api/wwwroot/src/domain/GraphNode.js", "GraphNode"),
            ("Api/wwwroot/src/domain/GraphProjection.js", "GraphProjection"),
            ("Api/wwwroot/src/domain/GraphModel.js", "GraphModel"));

        engine.Execute(
            """
            const model = new GraphModel();
            model.applyUiSettings({
              basis: {
                nodeTypeRoot: "graphdata/types/nodes"
              }
            });

            const typeId = "graphdata/types/nodes/Connection";
            const relationId = "r1";
            const sourcePortId = relationId + "/source";
            const targetPortId = relationId + "/target";
            const typePortId = relationId + "/type";
            model.schema.edgeTypes.set(typeId, {
              path: typeId,
              label: "Connection",
              visible: true,
              collapsed: true
            });

            model.putNode(new GraphNode({
              globalId: "a",
              displayName: "A",
              edges: [{ node1InternalId: "a", node2InternalId: sourcePortId }]
            }));
            model.putNode(new GraphNode({
              globalId: sourcePortId,
              displayName: "source",
              attributes: { [graphRoleAttribute]: "source" },
              edges: [{ node1InternalId: sourcePortId, node2InternalId: relationId }]
            }));
            model.putNode(new GraphNode({
              globalId: relationId,
              displayName: "R",
              attributes: { [graphKindAttribute]: "edge-instance", [graphElementAttribute]: "edge" },
              edges: [
                { node1InternalId: relationId, node2InternalId: targetPortId },
                { node1InternalId: relationId, node2InternalId: typePortId }
              ]
            }));
            model.putNode(new GraphNode({
              globalId: targetPortId,
              displayName: "target",
              attributes: { [graphRoleAttribute]: "target" },
              edges: [{ node1InternalId: targetPortId, node2InternalId: "b" }]
            }));
            model.putNode(new GraphNode({
              globalId: typePortId,
              displayName: "type",
              attributes: { [graphRoleAttribute]: "type" },
              edges: [{ node1InternalId: typePortId, node2InternalId: typeId }]
            }));
            model.putNode(new GraphNode({
              globalId: "b",
              displayName: "B",
              edges: []
            }));
            model.putNode(new GraphNode({
              globalId: typeId,
              displayName: "Connection",
              edges: []
            }));

            for (const node of model.loaded.values()) {
              node.showed = true;
            }

            const initialEdge = model.visibleGraph().edges.find(edge => edge.projected);
            const initialState = initialEdge?.collapsed === false;
            model.collapseEdge(initialEdge);
            const relation = model.loaded.get(relationId);
            const collapsedEdge = model.visibleGraph().edges.find(edge => edge.key === initialEdge.key);
            const relationCollapsed = relation.collapsed === true;
            const collapsedState = collapsedEdge?.collapsed === true
              && model.isEdgeCollapsed(collapsedEdge);

            model.expandEdge(collapsedEdge);
            const expandedEdge = model.visibleGraph().edges.find(edge => edge.key === initialEdge.key);
            const relationExpanded = relation.collapsed === false;
            model.collapseEdge(initialEdge.key);
            const collapsedByKey = relation.collapsed === true
              && model.isEdgeCollapsed(initialEdge.key);
            model.expandEdge(initialEdge.key);
            const expandedByKey = relation.collapsed === false
              && !model.isEdgeCollapsed(initialEdge.key);

            globalThis.__result = model.collapsedEdges === undefined
              && initialState
              && relationCollapsed
              && collapsedState
              && relationExpanded
              && expandedEdge?.collapsed === false
              && !model.isEdgeCollapsed(expandedEdge)
              && collapsedByKey
              && expandedByKey;
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void GraphProjection_ResolvesEdgeTypeThroughTypePort() {
        var engine = CreateUiEngine(
            ("Api/wwwroot/src/domain/GraphEdge.js", "GraphEdge"),
            ("Api/wwwroot/src/domain/GraphNode.js", "GraphNode"),
            ("Api/wwwroot/src/domain/GraphProjection.js", "GraphProjection"),
            ("Api/wwwroot/src/domain/GraphModel.js", "GraphModel"));

        engine.Execute(
            """
            const model = new GraphModel();
            model.applyUiSettings({
              basis: {
                nodeTypeRoot: "graphdata/types/nodes"
              }
            });

            const typeId = "graphdata/types/nodes/DependsOn";
            const relationId = "r1";
            const sourcePortId = relationId + "/source";
            const targetPortId = relationId + "/target";
            const typePortId = relationId + "/type";
            model.schema.edgeTypes.set(typeId, {
              path: typeId,
              label: "DependsOn",
              directed: true,
              collapsed: true,
              visible: true,
              rank: 77
            });

            model.putNode(new GraphNode({
              globalId: "a",
              displayName: "A",
              edges: [{ node1InternalId: "a", node2InternalId: sourcePortId }]
            }));
            model.putNode(new GraphNode({
              globalId: sourcePortId,
              attributes: { [graphKindAttribute]: "edge-port", [graphRoleAttribute]: "source" },
              edges: [{ node1InternalId: sourcePortId, node2InternalId: relationId }]
            }));
            model.putNode(new GraphNode({
              globalId: relationId,
              attributes: { [graphKindAttribute]: "edge-instance", [graphElementAttribute]: "edge" },
              edges: [
                { node1InternalId: relationId, node2InternalId: targetPortId },
                { node1InternalId: relationId, node2InternalId: typePortId }
              ]
            }));
            model.putNode(new GraphNode({
              globalId: targetPortId,
              attributes: { [graphKindAttribute]: "edge-port", [graphRoleAttribute]: "target" },
              edges: [{ node1InternalId: targetPortId, node2InternalId: "b" }]
            }));
            model.putNode(new GraphNode({
              globalId: typePortId,
              attributes: { [graphKindAttribute]: "edge-port", [graphRoleAttribute]: "type" },
              edges: [{ node1InternalId: typePortId, node2InternalId: typeId }]
            }));
            model.putNode(new GraphNode({
              globalId: "b",
              displayName: "B",
              edges: []
            }));
            model.putNode(new GraphNode({
              path: typeId,
              displayName: "DependsOn",
              edges: []
            }));

            for (const node of model.loaded.values()) {
              node.showed = true;
            }

            const edge = model.visibleGraph().edges.find(item => item.projected);
            globalThis.__result = edge?.typeGlobalId === typeId
              && edge?.label === "DependsOn"
              && edge?.directed === true;
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void GraphProjection_LeavesTypedEdgePrimitiveWhenEdgeTypeIsExpanded() {
        var engine = CreateUiEngine(
            ("Api/wwwroot/src/domain/GraphEdge.js", "GraphEdge"),
            ("Api/wwwroot/src/domain/GraphNode.js", "GraphNode"),
            ("Api/wwwroot/src/domain/GraphProjection.js", "GraphProjection"),
            ("Api/wwwroot/src/domain/GraphModel.js", "GraphModel"));

        engine.Execute(
            """
            const model = new GraphModel();
            model.applyUiSettings({
              basis: {
                nodeTypeRoot: "graphdata/types/nodes"
              }
            });

            const typeId = "graphdata/types/nodes/DependsOn";
            const relationId = "r1";
            const sourcePortId = relationId + "/source";
            const targetPortId = relationId + "/target";
            const typePortId = relationId + "/type";
            model.schema.edgeTypes.set(typeId, {
              path: typeId,
              label: "DependsOn",
              collapsed: false,
              visible: true
            });

            model.putNode(new GraphNode({
              globalId: "a",
              displayName: "A",
              edges: [{ node1InternalId: "a", node2InternalId: sourcePortId }]
            }));
            model.putNode(new GraphNode({
              globalId: sourcePortId,
              attributes: { [graphKindAttribute]: "edge-port", [graphRoleAttribute]: "source" },
              edges: [{ node1InternalId: sourcePortId, node2InternalId: relationId }]
            }));
            model.putNode(new GraphNode({
              globalId: relationId,
              attributes: { [graphKindAttribute]: "edge-instance", [graphElementAttribute]: "edge" },
              edges: [
                { node1InternalId: relationId, node2InternalId: targetPortId },
                { node1InternalId: relationId, node2InternalId: typePortId }
              ]
            }));
            model.putNode(new GraphNode({
              globalId: targetPortId,
              attributes: { [graphKindAttribute]: "edge-port", [graphRoleAttribute]: "target" },
              edges: [{ node1InternalId: targetPortId, node2InternalId: "b" }]
            }));
            model.putNode(new GraphNode({
              globalId: typePortId,
              attributes: { [graphKindAttribute]: "edge-port", [graphRoleAttribute]: "type" },
              edges: [{ node1InternalId: typePortId, node2InternalId: typeId }]
            }));
            model.putNode(new GraphNode({ globalId: "b", displayName: "B", edges: [] }));
            model.putNode(new GraphNode({ globalId: typeId, displayName: "DependsOn", edges: [] }));

            for (const node of model.loaded.values()) {
              node.showed = true;
            }

            const graph = model.visibleGraph();
            const names = new Set(graph.nodes.map(node => node.name));
            globalThis.__result = !graph.edges.some(edge => edge.projected)
              && names.has(relationId)
              && names.has(sourcePortId)
              && names.has(targetPortId)
              && names.has(typePortId)
              && names.has(typeId);
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void GraphProjection_CollapsedNodeTypeBecomesRecordNodeWithFields() {
        var engine = CreateUiEngine(
            ("Api/wwwroot/src/domain/GraphEdge.js", "GraphEdge"),
            ("Api/wwwroot/src/domain/GraphNode.js", "GraphNode"),
            ("Api/wwwroot/src/domain/GraphProjection.js", "GraphProjection"),
            ("Api/wwwroot/src/domain/GraphModel.js", "GraphModel"));

        engine.Execute(
            """
            const model = new GraphModel();
            const typeId = "NodeTypes/Weapon";
            const countryTypeId = "NodeTypes/Country";
            const definitionId = typeId + "/Definition";
            const fieldsId = definitionId + "/Fields";
            const slotsId = definitionId + "/Slots";
            const countryFieldId = fieldsId + "/Country";
            const yearFieldId = fieldsId + "/FoundedYear";
            const countrySlotId = slotsId + "/Country";

            model.schema.nodeTypes.set(typeId, {
              path: typeId,
              label: "Weapon",
              visible: true,
              collapsed: true,
              rank: 50,
              color: "#0f766e"
            });
            model.schema.nodeTypes.set(countryTypeId, {
              path: countryTypeId,
              label: "Country",
              visible: true,
              collapsed: false,
              rank: 50
            });

            const assignment = new GraphEdge({ node1InternalId: "FN_FAL", node2InternalId: typeId });
            const typeDefinition = new GraphEdge({ node1InternalId: typeId, node2InternalId: definitionId });
            const definitionFields = new GraphEdge({ node1InternalId: definitionId, node2InternalId: fieldsId });
            const definitionSlots = new GraphEdge({ node1InternalId: definitionId, node2InternalId: slotsId });
            const countryField = new GraphEdge({ node1InternalId: fieldsId, node2InternalId: countryFieldId });
            const yearField = new GraphEdge({ node1InternalId: fieldsId, node2InternalId: yearFieldId });
            const countrySlot = new GraphEdge({ node1InternalId: slotsId, node2InternalId: countrySlotId });
            const countryFieldType = new GraphEdge({ node1InternalId: countryFieldId, node2InternalId: countryTypeId });
            const countrySlotType = new GraphEdge({ node1InternalId: countrySlotId, node2InternalId: countryTypeId });
            const countryValue = new GraphEdge({ node1InternalId: "FN_FAL", node2InternalId: "USSR" });
            const countryTypeAssignment = new GraphEdge({ node1InternalId: "USSR", node2InternalId: countryTypeId });

            model.putNode(new GraphNode({
              globalId: "FN_FAL",
              displayName: "FN_FAL",
              attributes: { FoundedYear: "1953" },
              edges: [assignment, countryValue]
            }));
            model.putNode(new GraphNode({
              globalId: typeId,
              displayName: "Weapon",
              edges: [assignment, typeDefinition]
            }));
            model.putNode(new GraphNode({
              globalId: countryTypeId,
              displayName: "Country",
              edges: [countryFieldType, countrySlotType, countryTypeAssignment]
            }));
            model.putNode(new GraphNode({
              globalId: "USSR",
              displayName: "USSR",
              edges: [countryValue, countryTypeAssignment]
            }));
            model.putNode(new GraphNode({
              globalId: definitionId,
              displayName: "Definition",
              edges: [typeDefinition, definitionFields, definitionSlots]
            }));
            model.putNode(new GraphNode({
              globalId: fieldsId,
              displayName: "Fields",
              edges: [definitionFields, countryField, yearField]
            }));
            model.putNode(new GraphNode({
              globalId: slotsId,
              displayName: "Slots",
              edges: [definitionSlots, countrySlot]
            }));
            model.putNode(new GraphNode({
              globalId: countryFieldId,
              displayName: "Country",
              attributes: { valueKind: "Node", min: "1", max: "1" },
              edges: [countryField, countryFieldType]
            }));
            model.putNode(new GraphNode({
              globalId: yearFieldId,
              displayName: "FoundedYear",
              attributes: { valueKind: "Primitive", clrType: "int", min: "1", max: "1" },
              edges: [yearField]
            }));
            model.putNode(new GraphNode({
              globalId: countrySlotId,
              displayName: "Country",
              attributes: { min: "1", max: "1" },
              edges: [countrySlot, countrySlotType]
            }));

            for (const node of model.loaded.values()) {
              node.showed = true;
            }

            const graph = model.visibleGraph();
            const names = new Set(graph.nodes.map(node => node.name));
            const recordNode = graph.nodes.find(node => node.name === "FN_FAL");
            const fieldNames = (recordNode?.typeFields ?? []).map(field => field.name).sort().join(",");
            const yearFieldView = recordNode?.typeFields?.find(field => field.name === "FoundedYear");
            const countryFieldView = recordNode?.typeFields?.find(field => field.name === "Country");

            globalThis.__result = names.has("FN_FAL")
              && !names.has(typeId)
              && !names.has(definitionId)
              && !names.has(fieldsId)
              && !names.has(countryFieldId)
              && !names.has("USSR")
              && recordNode?.typeGlobalId === typeId
              && recordNode?.typeLabel === "Weapon"
              && recordNode?.viewShape === "record"
              && recordNode?.viewWidth > 0
              && recordNode?.viewHeight > 0
              && fieldNames === "Country,FoundedYear"
              && yearFieldView?.value === "1953"
              && countryFieldView?.value === "USSR"
              && countryFieldView?.typeLabel === "Country"
              && !graph.edges.some(edge => edge.node1InternalId === "FN_FAL" && edge.node2InternalId === typeId)
              && !graph.edges.some(edge => edge.node1InternalId === "FN_FAL" && edge.node2InternalId === "USSR")
              && !graph.edges.some(edge => edge.node1InternalId === fieldsId || edge.node2InternalId === fieldsId);
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void GraphModel_RebuildProjectionPublishesIntermediateGraph() {
        var engine = CreateUiEngine(
            ("Api/wwwroot/src/domain/GraphEdge.js", "GraphEdge"),
            ("Api/wwwroot/src/domain/GraphProjection.js", "GraphProjection"),
            ("Api/wwwroot/src/domain/GraphModel.js", "GraphModel"));

        engine.Execute(
            """
            const edge = new GraphEdge({
              node1InternalId: "a",
              node2InternalId: "b",
              node1LocalId: "a",
              node2LocalId: "b"
            });
            const model = new GraphModel();
            model.loaded.set("a", {
              name: "a",
              toViewNode() { return { name: "a", displayName: "A" }; },
              edges: [edge]
            });
            model.loaded.set("b", {
              name: "b",
              toViewNode() { return { name: "b", displayName: "B" }; },
              edges: []
            });

            const events = [];
            model.onProjectionRebuilt(event => events.push(event));
            const graph = model.rebuildProjection({ emit: true, reason: "basis-rule-change" });

            globalThis.__result = events.length === 1
              && events[0].reason === "basis-rule-change"
              && events[0].projectionGraph === graph
              && events[0].intermediateGraph === graph
              && model.primitiveNodeCount() === 2
              && model.primitiveEdgeCount() === 1
              && model.intermediateGraph === graph;
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void GraphModel_PrimitiveCacheKeepsHiddenLoadedNodes() {
        var engine = CreateUiEngine(
            ("Api/wwwroot/src/domain/GraphEdge.js", "GraphEdge"),
            ("Api/wwwroot/src/domain/GraphNode.js", "GraphNode"),
            ("Api/wwwroot/src/domain/GraphProjection.js", "GraphProjection"),
            ("Api/wwwroot/src/domain/GraphModel.js", "GraphModel"));

        engine.Execute(
            """
            const model = new GraphModel();
            model.putNode(new GraphNode({
              globalId: "root",
              displayName: "Root",
              showed: true,
              edges: []
            }));
            model.putNode(new GraphNode({
              globalId: "graphdata/types/nodes/HiddenType",
              displayName: "HiddenType",
              showed: false,
              edges: []
            }));

            const graph = model.rebuildProjection({ emit: true, reason: "cache" });
            const cachedNames = [...model.primitiveNodeViews()].map(node => node.name);

            globalThis.__result = model.primitiveNodeCount() === 2
              && cachedNames.some(name => name === "graphdata/types/nodes/HiddenType")
              && graph.nodes.length === 1
              && graph.nodes[0].name === "root";
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void GraphModel_LoadedMapPublishesPrimitiveGraphEvents() {
        var engine = CreateUiEngine(
            ("Api/wwwroot/src/domain/GraphEdge.js", "GraphEdge"),
            ("Api/wwwroot/src/domain/GraphNode.js", "GraphNode"),
            ("Api/wwwroot/src/domain/GraphProjection.js", "GraphProjection"),
            ("Api/wwwroot/src/domain/GraphModel.js", "GraphModel"));

        engine.Execute(
            """
            const model = new GraphModel();
            const events = [];
            model.onPrimitiveGraphChanged(event => events.push(event));
            model.batchPrimitiveChanges("load-batch", () => {
              model.loaded.set("a", new GraphNode({ globalId: "a" }));
              model.loaded.set("b", new GraphNode({ globalId: "b" }));
            });
            model.loaded.delete("b");
            model.loaded.clear();

            globalThis.__result = events.length === 3
              && events[0].kind === "batch"
              && events[0].reason === "load-batch"
              && events[0].changes.length === 2
              && events[1].kind === "node-delete"
              && events[2].kind === "cache-clear"
              && model.primitiveRevision === 3;
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void GraphProjection_DoesNotHideSchemaRootWithoutExplicitRule() {
        var engine = CreateUiEngine(
            ("Api/wwwroot/src/domain/GraphEdge.js", "GraphEdge"),
            ("Api/wwwroot/src/domain/GraphNode.js", "GraphNode"),
            ("Api/wwwroot/src/domain/GraphProjection.js", "GraphProjection"),
            ("Api/wwwroot/src/domain/GraphModel.js", "GraphModel"));

        engine.Execute(
            """
            const model = new GraphModel();
            model.applyUiSettings({
              basis: {
                nodeTypeRoot: "graphdata/types/nodes"
              }
            });
            const edge = new GraphEdge({
              node1InternalId: "root",
              node2InternalId: "graphdata/types/nodes",
              node1LocalId: "root",
              node2LocalId: "Types"
            });
            model.putNode(new GraphNode({
              globalId: "root",
              displayName: "Root",
              showed: true,
              edges: [edge]
            }));
            model.putNode(new GraphNode({
              globalId: "graphdata/types/nodes",
              displayName: "Types",
              showed: true,
              edges: [edge]
            }));

            const graph = model.visibleGraph();
            const names = new Set(graph.nodes.map(node => node.name));
            globalThis.__result = names.has("root")
              && names.has("graphdata/types/nodes")
              && graph.edges.length === 1;
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void GraphModel_AppliesUiSettingsBasis()
    {
        var engine = CreateUiEngine(("Api/wwwroot/src/domain/GraphModel.js", "GraphModel"));

        engine.Execute(
            """
            const model = new GraphModel();
            model.applyUiSettings({
              systemNodeIds: {
                graphDataRoot: "backend/root",
                nodeTypeRoot: "backend/types/nodes"
              },
            });

            globalThis.__result = model.basis.nodeTypeRoot === "backend/types/nodes"
              && model.basis.edgeTypeRoot === ""
              && model.basis.relationRoot === ""
              && model.defaultBasis().relationRoot === ""
              && model.schema.systemNodeIds.graphDataRoot === "backend/root";
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void FrontendDefaults_DoNotHardcodeSystemNodeIds()
    {
        var attributes = ReadUiFile("Api/wwwroot/src/domain/graphAttributes.ts");
        var html = ReadUiFile("Api/wwwroot/index.html");

        Assert.IsFalse(attributes.Contains("graphdata/types", StringComparison.Ordinal));
        Assert.IsFalse(attributes.Contains("graphdata/relations", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("graphdata/types", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("graphdata/relations", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BasisTab_DoesNotExposeLegacyProjectionPresetSelector()
    {
        var html = ReadUiFile("Api/wwwroot/index.html");
        var viewer = ReadUiFile("Api/wwwroot/src/GraphViewer.ts");

        Assert.IsFalse(html.Contains("projection-basis", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("Пустой базис", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("Типовой базис", StringComparison.Ordinal));
        Assert.IsFalse(viewer.Contains("projectionBasis", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FrontendDefaults_UseDoubleNodeRadius()
    {
        var attributes = ReadUiFile("Api/wwwroot/src/domain/graphAttributes.ts");
        StringAssert.Contains(attributes, "export const nodeRadius = 68;");

        var engine = new Engine();
        engine.Execute(
            """
            const graphElementAttribute = "graphElement";
            const projectionCollapsedAttribute = "projectionCollapsed";
            const projectionColorAttribute = "projectionColor";
            const projectionDirectedAttribute = "projectionDirected";
            const projectionInfoAttribute = "projectionInfo";
            const projectionLabelVisibleAttribute = "projectionLabelVisible";
            const projectionRankAttribute = "projectionRank";
            const projectionVisibleAttribute = "projectionVisible";
            const nodeRadius = 68;
            """);
        engine.Execute(ReadUiJsModule("Api/wwwroot/src/domain/GraphType.js", "GraphType"));
        engine.Execute(
            """
            globalThis.__result = GraphType.rankToRadius(55) === 68
              && GraphType.rankToRadius(0) === 56
              && GraphType.rankToRadius(200) === 96;
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void WebGpuCanvas_DoesNotRenderCollapsedEdgesAsLines() {
        var engine = CreateUiEngine(("Api/wwwroot/src/ui/WebGpuGraphCanvas.js", "WebGpuGraphCanvas"));

        engine.Execute(
            """
            const canvas = Object.create(WebGpuGraphCanvas.prototype);
            canvas.positions = new Map([
              ["a", { x: 0, y: 0 }],
              ["b", { x: 100, y: 0 }]
            ]);
            canvas.callbacks = { isNodeSelected() { return false; } };

            const memory = canvas.buildRenderMemory({
              nodes: [{ name: "a" }, { name: "b" }],
              edges: [
                { key: "collapsed", node1InternalId: "a", node2InternalId: "b", collapsed: true },
                { key: "visible", node1InternalId: "b", node2InternalId: "a" }
              ]
            });

            function runSimulationWith(edges) {
              const simulated = Object.create(WebGpuGraphCanvas.prototype);
              simulated.positions = new Map([
                ["a", { x: -50, y: 0 }],
                ["b", { x: 50, y: 0 }]
              ]);
              simulated.velocities = new Map();
              simulated.currentGraph = {
                nodes: [{ name: "a" }, { name: "b" }],
                edges
              };
              simulated.simulateStep();
              return {
                ax: simulated.positions.get("a").x,
                bx: simulated.positions.get("b").x
              };
            }

            const withoutEdge = runSimulationWith([]);
            const collapsedEdge = runSimulationWith([
              { key: "collapsed", node1InternalId: "a", node2InternalId: "b", collapsed: true }
            ]);
            const visibleEdge = runSimulationWith([
              { key: "visible", node1InternalId: "a", node2InternalId: "b" }
            ]);

            globalThis.__result = memory.edgeCount === 1
              && memory.edges[0].key === "visible"
              && Math.abs(collapsedEdge.ax - withoutEdge.ax) < 0.000001
              && Math.abs(collapsedEdge.bx - withoutEdge.bx) < 0.000001
              && Math.abs(visibleEdge.ax - withoutEdge.ax) > 0.000001;
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void GraphViewer_EdgeEndpointClicksCollapseExpandAndLoadByState() {
        var engine = CreateUiEngine(
            ("Api/wwwroot/src/domain/GraphEdge.js", "GraphEdge"),
            ("Api/wwwroot/src/domain/GraphNode.js", "GraphNode"),
            ("Api/wwwroot/src/domain/GraphModel.js", "GraphModel"),
            ("Api/wwwroot/src/GraphViewer.js", "GraphViewer"));

        engine.Execute(
            """
            function makeEdge(collapsed = false) {
              return new GraphEdge({
                node1InternalId: "a",
                node2InternalId: "b",
                node1LocalId: "a",
                node2LocalId: "b-local",
                collapsed
              });
            }

            function makeViewer(loadedNames, edge) {
              const calls = [];
              const viewer = Object.create(GraphViewer.prototype);
              const model = new GraphModel();
              model.rootName = "a";
              loadedNames.forEach(name => model.loaded.set(name, {
                name,
                displayName: name,
                showed: true,
                edges: [],
                toViewNode() { return { name, displayName: name }; }
              }));
              loadedNames.forEach(name => model.rootComponentByNode.set(name, "root"));
              const collapseEdge = model.collapseEdge.bind(model);
              model.collapseEdge = edge => {
                collapseEdge(edge);
                calls.push("collapse:" + edge.key);
              };
              const expandEdge = model.expandEdge.bind(model);
              model.expandEdge = edge => {
                expandEdge(edge);
                calls.push("expand:" + edge.key);
              };
              viewer.graph = model;
              viewer.loadNeighbor = (anchorName, neighborLocalId) => calls.push("load:" + anchorName + ":" + neighborLocalId);
              viewer.render = () => calls.push("render");
              viewer.stopSimulation = () => calls.push("stop");
              viewer.setStatus = message => calls.push("status:" + message);
              viewer.displayName = id => id;
              return { viewer, calls };
            }

            const edge = makeEdge();
            const loaded = makeViewer(["a", "b"], edge);
            const loadedControl = loaded.viewer.graph.edgeEndpointControl(edge, "a");
            loaded.viewer.handleEdgeControl(edge, loadedControl);
            const loadedClickCollapsesEdge = loaded.calls.includes("collapse:" + edge.key)
              && edge.collapsed === true
              && loaded.viewer.graph.loaded.get("a").showed === true
              && loaded.viewer.graph.loaded.get("b").showed === false
              && !loaded.calls.some(call => call.startsWith("load:"));

            const oppositeEdge = makeEdge();
            const opposite = makeViewer(["a", "b"], oppositeEdge);
            const oppositeControl = opposite.viewer.graph.edgeEndpointControl(oppositeEdge, "b");
            opposite.viewer.handleEdgeControl(oppositeEdge, oppositeControl);
            const oppositeButtonCollapsesOtherSide = opposite.calls.includes("collapse:" + oppositeEdge.key)
              && oppositeEdge.collapsed === true
              && opposite.viewer.graph.loaded.get("a").showed === false
              && opposite.viewer.graph.loaded.get("b").showed === true;

            const collapsedEdge = makeEdge(true);
            const collapsed = makeViewer(["a", "b"], collapsedEdge);
            const collapsedControl = collapsed.viewer.graph.edgeEndpointControl(collapsedEdge, "a");
            collapsed.viewer.handleEdgeControl(collapsedEdge, collapsedControl);
            const collapsedClickExpandsOnly = collapsed.calls.includes("expand:" + edge.key)
              && collapsedEdge.collapsed === false
              && collapsed.calls.includes("render")
              && !collapsed.calls.some(call => call.startsWith("load:"));

            const loadEdge = makeEdge();
            const oneEndpoint = makeViewer(["a"], loadEdge);
            const oneEndpointControl = oneEndpoint.viewer.graph.edgeEndpointControl(loadEdge, "a");
            oneEndpoint.viewer.handleEdgeControl(loadEdge, oneEndpointControl);
            const oneEndpointClickLoadsNeighbor = oneEndpoint.calls.includes("load:a:b-local")
              && !oneEndpoint.calls.some(call => call.startsWith("collapse:"));

            globalThis.__result = loadedClickCollapsesEdge
              && loadedControl.kind === "collapse"
              && oppositeButtonCollapsesOtherSide
              && oppositeControl.kind === "collapse"
              && collapsedClickExpandsOnly
              && collapsedControl.kind === "expand"
              && oneEndpointClickLoadsNeighbor
              && oneEndpointControl.kind === "expand";
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void GraphViewer_CollapseEdgeHidesEndpointSideOnlyWhenItSplitsVisibleGraph() {
        var engine = CreateUiEngine(
            ("Api/wwwroot/src/domain/GraphEdge.js", "GraphEdge"),
            ("Api/wwwroot/src/domain/GraphNode.js", "GraphNode"),
            ("Api/wwwroot/src/domain/GraphProjection.js", "GraphProjection"),
            ("Api/wwwroot/src/domain/GraphModel.js", "GraphModel"),
            ("Api/wwwroot/src/GraphViewer.js", "GraphViewer"));

        engine.Execute(
            """
            function makeViewer(names, pairs, componentByNode) {
              const viewer = Object.create(GraphViewer.prototype);
              const model = new GraphModel();
              const edges = pairs.map(([a, b]) => new GraphEdge({
                node1InternalId: a,
                node2InternalId: b,
                node1LocalId: a,
                node2LocalId: b
              }));
              const edgesByNode = new Map(names.map(name => [name, []]));
              edges.forEach(edge => {
                edgesByNode.get(edge.node1InternalId).push(edge);
                edgesByNode.get(edge.node2InternalId).push(edge);
              });
              names.forEach(name => {
                model.loaded.set(name, new GraphNode({
                  globalId: name,
                  displayName: name,
                  showed: true,
                  edges: edgesByNode.get(name)
                }));
                model.positions.set(name, { x: names.indexOf(name) * 120, y: 0 });
                model.rootComponentByNode.set(name, componentByNode[name] ?? "root");
              });
              viewer.graph = model;
              viewer.refreshEdgeAngles = () => {};
              viewer.stopSimulation = () => {};
              viewer.render = () => {};
              viewer.setStatus = message => viewer.lastStatus = message;
              viewer.displayName = id => id;
              return { viewer, edges };
            }

            const bridge = makeViewer(["1", "2", "3", "4"], [["1", "2"], ["1", "3"], ["3", "4"]], {
              "1": "root-a",
              "2": "root-a",
              "3": "root-a",
              "4": "root-a"
            });
            const bridgeEdge = bridge.edges.find(edge => edge.connects("1") && edge.connects("2"));
            bridge.viewer.collapseEdge(bridgeEdge, "2");
            const bridgeVisible = [...bridge.viewer.graph.loaded.values()]
              .filter(node => node.showed === true)
              .map(node => node.name)
              .sort()
              .join(",");

            const cycle = makeViewer(["1", "2", "3"], [["1", "2"], ["2", "3"], ["3", "1"]], {
              "1": "root-a",
              "2": "root-a",
              "3": "root-a"
            });
            const cycleEdge = cycle.edges.find(edge => edge.connects("1") && edge.connects("2"));
            cycle.viewer.collapseEdge(cycleEdge, "1");
            const cycleVisible = [...cycle.viewer.graph.loaded.values()]
              .filter(node => node.showed === true)
              .map(node => node.name)
              .sort()
              .join(",");

            const twoRoots = makeViewer(["a", "b", "c"], [["a", "b"], ["b", "c"]], {
              a: "component-a",
              b: "component-b",
              c: "component-b"
            });
            const crossRootEdge = twoRoots.edges.find(edge => edge.connects("a") && edge.connects("b"));
            twoRoots.viewer.collapseEdge(crossRootEdge, "a");
            const protectedVisible = [...twoRoots.viewer.graph.loaded.values()]
              .filter(node => node.showed === true)
              .map(node => node.name)
              .sort()
              .join(",");

            const restoreCycle = makeViewer(["a", "b", "c", "d"], [["a", "b"], ["b", "c"], ["c", "d"], ["d", "a"]], {
              a: "component-a",
              b: "component-a",
              c: "component-a",
              d: "component-a"
            });
            const firstCollapsedEdge = restoreCycle.edges.find(edge => edge.connects("a") && edge.connects("b"));
            const hidingEdge = restoreCycle.edges.find(edge => edge.connects("a") && edge.connects("d"));
            restoreCycle.viewer.collapseEdge(firstCollapsedEdge, "a");
            restoreCycle.viewer.collapseEdge(hidingEdge, "a");
            const cycleHiddenVisible = [...restoreCycle.viewer.graph.loaded.values()]
              .filter(node => node.showed === true)
              .map(node => node.name)
              .sort()
              .join(",");
            restoreCycle.viewer.expandEdge(hidingEdge, "a", "d");
            const cycleRestoredVisible = [...restoreCycle.viewer.graph.loaded.values()]
              .filter(node => node.showed === true)
              .map(node => node.name)
              .sort()
              .join(",");

            globalThis.__debug = { bridgeVisible, cycleVisible, protectedVisible, cycleHiddenVisible, cycleRestoredVisible };
            globalThis.__result = bridgeVisible === "2"
              && bridgeEdge.collapsed === true
              && cycleVisible === "1,2,3"
              && cycleEdge.collapsed === true
              && protectedVisible === "a,b"
              && cycleHiddenVisible === "a"
              && cycleRestoredVisible === "a,b,c,d"
              && hidingEdge.collapsed === false
              && firstCollapsedEdge.collapsed === true;
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean(), engine.Evaluate("JSON.stringify(__debug)").AsString());
    }

    [TestMethod]
    public void GraphViewer_EdgeCollapseExpandDoesNotMoveExistingNodes() {
        var engine = CreateUiEngine(
            ("Api/wwwroot/src/domain/GraphEdge.js", "GraphEdge"),
            ("Api/wwwroot/src/domain/GraphNode.js", "GraphNode"),
            ("Api/wwwroot/src/domain/GraphModel.js", "GraphModel"),
            ("Api/wwwroot/src/GraphViewer.js", "GraphViewer"));

        engine.Execute(
            """
            const model = new GraphModel();
            const edge = new GraphEdge({
              node1InternalId: "a",
              node2InternalId: "b",
              node1LocalId: "a",
              node2LocalId: "b"
            });

            function loadedNode(name, displayName, edges) {
              return {
                name,
                displayName,
                showed: true,
                edges,
                toViewNode() { return { name, globalId: name, displayName }; }
              };
            }

            model.loaded.set("a", loadedNode("a", "A", [edge]));
            model.loaded.set("b", loadedNode("b", "B", []));
            model.loaded.set("c", loadedNode("c", "C", []));
            model.positions.set("a", { x: 10, y: 20 });
            model.positions.set("b", { x: 140, y: -30 });
            model.positions.set("c", { x: -75, y: 90 });
            model.velocities.set("a", { x: 5, y: 5 });
            model.velocities.set("b", { x: -5, y: 4 });
            model.velocities.set("c", { x: 3, y: -2 });

            const viewer = Object.create(GraphViewer.prototype);
            viewer.graph = model;
            viewer.displayName = id => model.displayName(id);
            viewer.setStatus = () => {};
            viewer.renderTypeControls = () => {};
            let renderCalls = 0;
            let stopCalls = 0;
            let simulationCalls = 0;
            viewer.render = () => { renderCalls += 1; };
            viewer.stopSimulation = () => { stopCalls += 1; };
            viewer.runSimulation = frames => {
              simulationCalls += 1;
              for (const position of model.positions.values()) {
                position.x += frames;
                position.y -= frames;
              }
            };

            function snapshotPositions() {
              return JSON.stringify([...model.positions.entries()]
                .map(([name, position]) => [name, position.x, position.y])
                .sort((left, right) => left[0].localeCompare(right[0], "ru")));
            }

            const before = snapshotPositions();
            viewer.handleEndpointClick(edge, "a");
            const afterCollapse = snapshotPositions();
            const collapsed = edge.collapsed === true && model.isEdgeCollapsed(edge);

            model.loaded.get("b").showed = true;
            viewer.handleEndpointClick(edge, "a");
            const afterExpand = snapshotPositions();
            const expanded = edge.collapsed === false && !model.isEdgeCollapsed(edge);

            globalThis.__result = collapsed
              && expanded
              && renderCalls === 2
              && stopCalls === 2
              && simulationCalls === 0
              && before === afterCollapse
              && before === afterExpand;
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void GraphViewer_CollapsingTreeEdgeDoesNotRunSimulationOrMoveRemainingNodes() {
        var engine = CreateUiEngine(
            ("Api/wwwroot/src/domain/GraphEdge.js", "GraphEdge"),
            ("Api/wwwroot/src/domain/GraphModel.js", "GraphModel"),
            ("Api/wwwroot/src/GraphViewer.js", "GraphViewer"));

        engine.Execute(
            """
            const model = new GraphModel();
            const edge = new GraphEdge({
              node1InternalId: "root",
              node2InternalId: "child",
              node1LocalId: "root",
              node2LocalId: "child"
            });

            function loadedNode(name, displayName, edges) {
              return {
                name,
                displayName,
                showed: true,
                edges,
                toViewNode() { return { name, globalId: name, displayName }; }
              };
            }

            model.rootName = "root";
            model.selectedName = "child";
            model.loaded.set("root", loadedNode("root", "Root", [edge]));
            model.loaded.set("child", loadedNode("child", "Child", [edge]));
            model.loaded.set("sibling", loadedNode("sibling", "Sibling", []));
            model.parentByNode.set("child", "root");
            model.positions.set("root", { x: 10, y: 20 });
            model.positions.set("child", { x: 140, y: -30 });
            model.positions.set("sibling", { x: -75, y: 90 });

            const viewer = Object.create(GraphViewer.prototype);
            viewer.graph = model;
            viewer.displayName = id => model.displayName(id);
            viewer.setStatus = () => {};
            let renderCalls = 0;
            let stopCalls = 0;
            let simulationCalls = 0;
            viewer.render = () => { renderCalls += 1; };
            viewer.stopSimulation = () => { stopCalls += 1; };
            viewer.runSimulation = frames => {
              simulationCalls += 1;
              for (const position of model.positions.values()) {
                position.x += frames;
                position.y -= frames;
              }
            };

            function snapshotRemainingPositions() {
              return JSON.stringify(["root", "sibling"].map(name => {
                const position = model.positions.get(name);
                return [name, position.x, position.y];
              }));
            }

            const before = snapshotRemainingPositions();
            viewer.handleEndpointClick(edge, "root");
            const after = snapshotRemainingPositions();

            globalThis.__result = before === after
              && model.loaded.has("child")
              && model.loaded.get("child").showed === false
              && model.positions.has("child")
              && model.loaded.get("child").position === model.positions.get("child")
              && model.selectedName === "root"
              && renderCalls === 1
              && stopCalls === 1
              && simulationCalls === 0;
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void GraphViewer_LoadSubgraphPreservesNodeEdgesForLazyEndpointControls() {
        var engine = CreateUiEngine(
            ("Api/wwwroot/src/domain/GraphEdge.js", "GraphEdge"),
            ("Api/wwwroot/src/domain/GraphNode.js", "GraphNode"),
            ("Api/wwwroot/src/domain/GraphModel.js", "GraphModel"),
            ("Api/wwwroot/src/GraphViewer.js", "GraphViewer"));

        engine.Execute(
            """
            const viewer = Object.create(GraphViewer.prototype);
            viewer.graph = new GraphModel();
            viewer.normalizeNodeResponse = node => GraphNode.fromApi(node);
            viewer.normalizeEdgeResponse = edge => GraphEdge.fromApi(edge);
            viewer.mergeEdges = (left, right) => GraphEdge.mergeMany(left, right);
            viewer.render = () => {};
            viewer.renderTypeControls = () => {};
            viewer.runSimulation = () => {};
            viewer.fitView = () => {};

            viewer.loadSubgraphIntoViewer({
              nodes: [{
                localId: "root",
                globalId: "root",
                edges: ["a", "b", "c", "d"].map(name => ({
                  node1InternalId: "root",
                  node2InternalId: "root/" + name,
                  node1LocalId: "root",
                  node2LocalId: name,
                  neighborLocalId: name
                }))
              }],
              edges: []
            }, [], { selectRoot: false });

            const root = viewer.graph.loaded.get("root");
            const edge = root.edges[0];
            const control = viewer.graph.edgeEndpointControl(edge, "root");
            const graphEdgeControl = viewer.graph.visibleGraph().edges.find(item => item.key === edge.key)?.controls?.[0];
            const rootPosition = viewer.graph.positions.get("root");
            const angles = root.edges.map(item => item.controlAngleFor("root")).sort((a, b) => a - b);
            const step = Math.PI / 2;
            const evenlySpaced = angles.every((angle, index) => {
              const next = angles[(index + 1) % angles.length] + (index === angles.length - 1 ? Math.PI * 2 : 0);
              return Math.abs((next - angle) - step) < 0.000001;
            });

            viewer.storeNodeExpansion(new GraphNode({
              localId: "a",
              globalId: "root/a",
              edges: [{
                node1InternalId: "root",
                node2InternalId: "root/a",
                node1LocalId: "root",
                node2LocalId: "a",
                neighborLocalId: "root"
              }]
            }), "root", { select: false, showed: true });
            const loadedPosition = viewer.graph.positions.get("root/a");
            const loadedOffset = {
              x: loadedPosition.x - rootPosition.x,
              y: loadedPosition.y - rootPosition.y
            };
            const rootEdge = root.edges.find(item => item.node2InternalId === "root/a");
            const angleAfterLoad = rootEdge.controlAngleFor("root");
            viewer.graph.positions.set("root/a", { x: rootPosition.x + 204, y: rootPosition.y });
            viewer.refreshEdgeAngles();

            globalThis.__result = root.edges.length === 4
              && viewer.graph.primitiveEdgeCount() === 4
              && !viewer.graph.positions.has("root/b")
              && control?.kind === "expand"
              && control?.text === "+"
              && graphEdgeControl?.key === control?.key
              && Number.isFinite(control?.angle)
              && evenlySpaced
              && Math.abs(loadedOffset.x) < 0.000001
              && Math.abs(loadedOffset.y + 204) < 0.000001
              && Math.abs(angleAfterLoad + Math.PI / 2) < 0.000001
              && Math.abs(rootEdge.controlAngleFor("root")) < 0.000001;
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void GraphModel_SharesPrimitiveEdgeObjectAcrossCachedEndpoints() {
        var engine = CreateUiEngine(
            ("Api/wwwroot/src/domain/GraphEdge.js", "GraphEdge"),
            ("Api/wwwroot/src/domain/GraphNode.js", "GraphNode"),
            ("Api/wwwroot/src/domain/GraphModel.js", "GraphModel"),
            ("Api/wwwroot/src/GraphViewer.js", "GraphViewer"));

        engine.Execute(
            """
            const viewer = Object.create(GraphViewer.prototype);
            viewer.graph = new GraphModel();

            const edgeFromHiddenChild = {
              node1InternalId: "root",
              node2InternalId: "root/a",
              node1LocalId: "root",
              node2LocalId: "a",
              neighborLocalId: "root"
            };
            const edgeFromVisibleRoot = {
              node1InternalId: "root",
              node2InternalId: "root/a",
              node1LocalId: "root",
              node2LocalId: "a",
              neighborLocalId: "a"
            };

            viewer.graph.loaded.set("root/a", new GraphNode({
              globalId: "root/a",
              localId: "a",
              edges: [edgeFromHiddenChild]
            }));
            viewer.graph.loaded.set("root", new GraphNode({
              globalId: "root",
              localId: "root",
              showed: true,
              edges: [edgeFromVisibleRoot]
            }));
            viewer.graph.positions.set("root", { x: 0, y: 0 });
            viewer.refreshEdgeAngles();

            const rootStoredEdge = viewer.graph.loaded.get("root").edges[0];
            const childStoredEdge = viewer.graph.loaded.get("root/a").edges[0];
            const graphEdge = viewer.graph.visibleGraph().edges.find(edge =>
              edge.node1InternalId === "root" && edge.node2InternalId === "root/a");
            const control = graphEdge?.controls?.[0];

            globalThis.__result = rootStoredEdge === childStoredEdge
              && rootStoredEdge.controlAngleFor("root") !== null
              && rootStoredEdge.neighborLocalIdFor("root") === "a"
              && rootStoredEdge.neighborLocalIdFor("root/a") === "root"
              && control?.action === "load-neighbor"
              && control?.anchorName === "root"
              && control?.otherName === "root/a"
              && control?.neighborLocalId === "a"
              && Number.isFinite(control?.angle);
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void WebGpuCanvas_RendersUnshownEndpointControlsAtStoredAngle() {
        var engine = CreateUiEngine(("Api/wwwroot/src/ui/WebGpuGraphCanvas.js", "WebGpuGraphCanvas"));

        engine.Execute(
            """
            const buttons = [];
            const calls = [];
            const canvas = Object.create(WebGpuGraphCanvas.prototype);
            canvas.view = { x: 0, y: 0, scale: 1 };
            canvas.positions = new Map([
              ["a", { x: 10, y: 20 }],
              ["b", { x: 110, y: 20 }]
            ]);
            canvas.callbacks = {
              activateEdgeControl() {},
              syncEdgeAngles() { calls.push("sync"); }
            };
            canvas.memory = {};
            canvas.writeVertexData = () => calls.push("write");
            canvas.renderLabels = () => calls.push("labels");
            canvas.updateRendererGraph = () => calls.push("renderer");
            canvas.requestDraw = () => calls.push("draw");
            canvas.document = {
              createElement() {
                return {
                  style: {},
                  dataset: {},
                  setAttribute() {},
                  addEventListener() {}
                };
              }
            };
            const fragment = { append(button) { buttons.push(button); } };
            const rect = { width: 500, height: 500 };
            const edge = { key: "ab", node1InternalId: "a", node2InternalId: "b" };
            const control = { key: "ab\\0a\\0load-neighbor", action: "load-neighbor", kind: "expand", text: "+", title: "expand", angle: 0, anchorName: "a", otherName: "b" };
            const nodesByName = new Map([["a", { name: "a", viewRadius: 34 }]]);

            function parsePoint(button) {
              const match = button.style.transform.match(/translate\(([-0-9.]+)px, ([-0-9.]+)px\)/);
              return { x: Number(match[1]), y: Number(match[2]) };
            }

            function anchorScreen(name) {
              const position = canvas.positions.get(name);
              return {
                x: position.x * canvas.view.scale + canvas.view.x,
                y: position.y * canvas.view.scale + canvas.view.y
              };
            }

            function renderOffset(controlToRender) {
              buttons.length = 0;
              canvas.renderEdgeEndpointControl(fragment, rect, edge, controlToRender, nodesByName, new Set());
              const point = parsePoint(buttons[0]);
              const anchor = anchorScreen(controlToRender.anchorName);
              return {
                x: point.x - anchor.x,
                y: point.y - anchor.y,
                action: buttons[0].dataset.edgeAction
              };
            }

            const firstOffset = renderOffset(control);
            canvas.positions.set("a", { x: 100, y: 80 });
            const secondOffset = renderOffset(control);
            canvas.view = { x: -30, y: 17, scale: 2.5 };
            const scaledOffset = renderOffset(control);
            const loadedControl = { ...control, key: "ab\\0a\\0collapse-edge", action: "collapse-edge", kind: "collapse", text: "-", angle: null };
            canvas.positions.set("a", { x: 10, y: 20 });
            canvas.positions.set("b", { x: 110, y: 20 });
            const loadedOffset = renderOffset(loadedControl);
            canvas.updateDynamicGraph();

            globalThis.__result = buttons.length === 1
              && firstOffset.action === "load-neighbor"
              && loadedOffset.action === "collapse-edge"
              && Math.abs(firstOffset.x - 43) < 0.000001
              && Math.abs(firstOffset.y) < 0.000001
              && Math.abs(secondOffset.x - firstOffset.x) < 0.000001
              && Math.abs(secondOffset.y - firstOffset.y) < 0.000001
              && Math.abs(scaledOffset.x - 94) < 0.000001
              && Math.abs(scaledOffset.y) < 0.000001
              && Math.abs(loadedOffset.x - scaledOffset.x) < 0.000001
              && Math.abs(loadedOffset.y - scaledOffset.y) < 0.000001
              && calls.join(",") === "sync,write,labels,renderer,draw";
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void WebGpuCanvas_SelectsNodesAndEdgesWithShiftBoxAndEdgeHitTest() {
        var engine = CreateUiEngine(("Api/wwwroot/src/ui/WebGpuGraphCanvas.js", "WebGpuGraphCanvas"));

        engine.Execute(
            """
            const canvas = Object.create(WebGpuGraphCanvas.prototype);
            canvas.view = { x: 0, y: 0, scale: 1 };
            canvas.canvas = {
              getBoundingClientRect() {
                return { left: 100, top: 50, width: 400, height: 300 };
              }
            };
            canvas.positions = new Map([
              ["a", { x: 0, y: 0 }],
              ["b", { x: 100, y: 0 }],
              ["c", { x: 240, y: 120 }],
              ["d", { x: 300, y: 120 }]
            ]);
            canvas.memory = {
              nodeCount: 4,
              edgeCount: 2,
              nodes: [
                { name: "a", viewRadius: 10 },
                { name: "b", viewRadius: 10 },
                { name: "c", viewRadius: 10 },
                { name: "d", viewRadius: 10 }
              ],
              edges: [
                { key: "ab", node1InternalId: "a", node2InternalId: "b" },
                { key: "cd", node1InternalId: "c", node2InternalId: "d" }
              ]
            };

            let selected = null;
            canvas.callbacks = {
              selectGraphElements(nodes, edges, append) {
                selected = {
                  nodeNames: nodes.map(node => node.name),
                  edgeKeys: edges.map(edge => edge.key),
                  append
                };
              }
            };

            canvas.selectElementsInBox({
              startX: 92,
              startY: 42,
              x: 212,
              y: 72,
              append: true
            });

            const hit = canvas.pickNearestEdge(160, 53);
            const miss = canvas.pickNearestEdge(160, 90);

            globalThis.__debug = { selected, hitKey: hit?.key ?? null, miss };
            globalThis.__result = selected.nodeNames.join(",") === "a,b"
              && selected.edgeKeys.join(",") === "ab"
              && selected.append === true
              && hit?.key === "ab"
              && miss === null;
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean(), engine.Evaluate("JSON.stringify(__debug)").AsString());
    }

    [TestMethod]
    public void WebGpuCanvas_DragsSelectedNodesTogether() {
        var engine = CreateUiEngine(("Api/wwwroot/src/ui/WebGpuGraphCanvas.js", "WebGpuGraphCanvas"));

        engine.Execute(
            """
            const canvas = Object.create(WebGpuGraphCanvas.prototype);
            canvas.view = { x: 0, y: 0, scale: 2 };
            canvas.positions = new Map([
              ["a", { x: 0, y: 0 }],
              ["b", { x: 100, y: 10 }],
              ["c", { x: 300, y: 40 }]
            ]);
            canvas.memory = {
              nodes: [
                { name: "a" },
                { name: "b" },
                { name: "c" }
              ]
            };

            const selectedNames = new Set(["a", "b"]);
            let updateCount = 0;
            canvas.callbacks = {
              isNodeSelected(name) {
                return selectedNames.has(name);
              }
            };
            canvas.updateDynamicGraph = () => updateCount += 1;

            canvas.beginNodeDrag({ name: "a" }, {
              clientX: 10,
              clientY: 20,
              pointerType: "mouse"
            });
            canvas.dragSelectedNodes({
              clientX: 18,
              clientY: 26
            });

            const a = canvas.positions.get("a");
            const b = canvas.positions.get("b");
            const c = canvas.positions.get("c");
            const selectedDragNames = canvas.dragging.names.join(",");
            const unselectedDragNames = canvas.dragNodeNames("c").join(",");
            globalThis.__debug = { a, b, c, selectedDragNames, unselectedDragNames, updateCount };
            globalThis.__result = selectedDragNames === "a,b"
              && unselectedDragNames === "c"
              && a.x === 4
              && a.y === 3
              && b.x === 104
              && b.y === 13
              && c.x === 300
              && c.y === 40
              && canvas.dragging.x === 18
              && canvas.dragging.y === 26
              && updateCount === 1;
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean(), engine.Evaluate("JSON.stringify(__debug)").AsString());
    }

    [TestMethod]
    public void WebGpuCanvas_RendersClickableEdgeEndpointControls() {
        foreach (var path in new[] {
            "Api/wwwroot/src/ui/WebGpuGraphCanvas.ts",
            "Api/wwwroot/src/ui/WebGpuGraphCanvas.js"
        }) {
            var source = ReadUiFile(path);

            StringAssert.Contains(source, "renderEdgeEndpointControls(fragment, rect)");
            StringAssert.Contains(source, "graph-edge-control");
            StringAssert.Contains(source, "edge.controls ?? []");
            StringAssert.Contains(source, "callbacks.activateEdgeControl?.(edge, control)");
            StringAssert.Contains(source, "pointAtAngle(anchor, control.angle");
            Assert.IsFalse(source.Contains("graph-node-collapse", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void GraphRenderers_ShareRendererInterfaceAndSvgImplementation() {
        var contract = ReadUiFile("Api/wwwroot/src/ui/GraphRenderer.ts");
        var webGpu = ReadUiFile("Api/wwwroot/src/ui/WebGpuRenderer.ts");
        var svg = ReadUiFile("Api/wwwroot/src/ui/SvgRenderer.ts");
        var htmlCanvas = ReadUiFile("Api/wwwroot/src/ui/HtmlCanvasRenderer.ts");
        var canvas = ReadUiFile("Api/wwwroot/src/ui/WebGpuGraphCanvas.ts");
        var html = ReadUiFile("Api/wwwroot/index.html");
        var css = ReadUiFile("Api/wwwroot/styles.css");

        StringAssert.Contains(contract, "export interface GraphRenderer");
        StringAssert.Contains(contract, "updateGraph(memory: GraphRenderMemory | null): void");
        StringAssert.Contains(contract, "draw(view: GraphView): number");
        StringAssert.Contains(webGpu, "implements GraphRenderer");
        StringAssert.Contains(svg, "implements GraphRenderer");
        StringAssert.Contains(htmlCanvas, "implements GraphRenderer");
        StringAssert.Contains(svg, "mode = \"svg\"");
        StringAssert.Contains(htmlCanvas, "mode = \"html-canvas\"");
        StringAssert.Contains(htmlCanvas, "layoutsubtree");
        StringAssert.Contains(htmlCanvas, "drawElementImage");
        StringAssert.Contains(htmlCanvas, "requestPaint");
        StringAssert.Contains(canvas, "new SvgRenderer(host)");
        StringAssert.Contains(canvas, "new HtmlCanvasRenderer(host)");
        StringAssert.Contains(canvas, "new WebGpuRenderer(host)");
        StringAssert.Contains(canvas, "[\"webgpu\", \"svg\", \"html-canvas\"]");
        StringAssert.Contains(canvas, "normalizeRendererMode");
        StringAssert.Contains(canvas, "checkRendererAvailability()");
        StringAssert.Contains(canvas, "detectRendererAvailability()");
        StringAssert.Contains(canvas, "option.disabled = !available");
        StringAssert.Contains(canvas, "this.rendererSelect.disabled = !this.rendererAvailability");
        StringAssert.Contains(html, "<div id=\"graph\"");
        StringAssert.Contains(html, "<select id=\"renderer-select\"");
        StringAssert.Contains(css, ".renderer-picker");
        StringAssert.Contains(css, ".graph-html-canvas-layer > .html-canvas-node");
    }

    [TestMethod]
    public void GraphViewer_EdgeEndpointExpandControlsLoadUnloadedNeighbors() {
        foreach (var path in new[] {
            "Api/wwwroot/src/GraphViewer.ts",
            "Api/wwwroot/src/GraphViewer.js"
        }) {
            var source = ReadUiFile(path);

            StringAssert.Contains(source, "activateEdgeControl: (edge, control) => this.handleEdgeControl(edge, control)");
            StringAssert.Contains(source, "control.action === \"load-neighbor\"");
            StringAssert.Contains(source, "this.loadNeighbor(anchorName, control.neighborLocalId");
            Assert.IsFalse(source.Contains("collapseNode", StringComparison.Ordinal));
        }

        foreach (var path in new[] {
            "Api/wwwroot/src/domain/GraphEdge.ts",
            "Api/wwwroot/src/domain/GraphEdge.js"
        }) {
            var source = ReadUiFile(path);

            StringAssert.Contains(source, "GraphEdgeControl.loadNeighbor");
            StringAssert.Contains(source, "GraphEdgeControl.expandEdge");
            StringAssert.Contains(source, "GraphEdgeControl.collapseEdge");
        }
    }

    [TestMethod]
    public void GraphViewer_RunSimulationOnlyFromFitButton() {
        foreach (var path in new[] {
            "Api/wwwroot/src/GraphViewer.ts",
            "Api/wwwroot/src/GraphViewer.js"
        }) {
            var source = ReadUiFile(path);

            Assert.AreEqual(
                1,
                Regex.Matches(source, @"this\.runSimulation\s*\(").Count,
                $"Unexpected implicit runSimulation call in {path}.");
            AssertMatches(
                source,
                @"fitButton\.addEventListener\(""click"",\s*\(\)\s*=>\s*\{\s*this\.fitView\(\);\s*this\.runSimulation\(40,\s*\(\)\s*=>\s*this\.fitView\(\)\);",
                path);
        }
    }

    [TestMethod]
    public void GraphViewer_LoadGraphRootsSendsExplicitSubgraphRequest() {
        foreach (var path in new[] {
            "Api/wwwroot/src/GraphViewer.ts",
            "Api/wwwroot/src/GraphViewer.js"
        }) {
            var source = ReadUiFile(path);

            AssertMatches(
                source,
                @"loadGraphRoots\s*\(\)\s*\{[\s\S]*loadSubgraphForRoots\s*\(\s*\[\s*\]\s*,\s*0\s*\)",
                path);
            Assert.IsFalse(
                source.Contains("loadSubgraphForKeys({})", StringComparison.Ordinal),
                $"Root loading must not send an empty subgraph request body in {path}.");
        }
    }

    [TestMethod]
    public void GraphViewer_ServerErrorsRenderOverlay() {
        var html = ReadUiFile("Api/wwwroot/index.html");
        var css = ReadUiFile("Api/wwwroot/styles.css");

        StringAssert.Contains(html, "id=\"server-error-overlay\"");
        StringAssert.Contains(html, "role=\"alertdialog\"");
        StringAssert.Contains(html, "id=\"server-error-message\"");
        StringAssert.Contains(css, ".server-error-overlay");
        StringAssert.Contains(css, ".server-error-message");

        foreach (var path in new[] {
            "Api/wwwroot/src/infrastructure/GraphApi.ts",
            "Api/wwwroot/src/infrastructure/GraphApi.js"
        }) {
            var source = ReadUiFile(path);

            StringAssert.Contains(source, "errorFromResponse");
            StringAssert.Contains(source, "responseText");
            StringAssert.Contains(source, "error.status = response.status");
        }

        foreach (var path in new[] {
            "Api/wwwroot/src/GraphViewer.ts",
            "Api/wwwroot/src/GraphViewer.js"
        }) {
            var source = ReadUiFile(path);

            StringAssert.Contains(source, "ensureServerErrorElements()");
            StringAssert.Contains(source, "document.createElement(\"div\")");
            StringAssert.Contains(source, "bindServerErrors()");
            StringAssert.Contains(source, "showServerError(error");
            StringAssert.Contains(source, "this.serverErrorOverlay.hidden = false");
            StringAssert.Contains(source, "GraphApi.errorFromResponse(response, \"/api/graph/raw/search/nodes\", \"POST\")");
            Assert.IsFalse(source.Contains("requireElement(\"#server-error-overlay\")", StringComparison.Ordinal));
        }
    }

    private static void AssertMatches(string source, string pattern, string path) {
        Assert.IsTrue(
            Regex.IsMatch(source, pattern, RegexOptions.Singleline),
            $"Expected UI regression pattern not found in {path}: {pattern}");
    }

    private static string ReadUiFile(string relativePath) {
        var fullPath = Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath) && relativePath.StartsWith("Api/wwwroot/src/", StringComparison.Ordinal)) {
            var editorRelativePath = relativePath["Api/wwwroot/".Length..];
            var editorPath = relativePath.EndsWith(".js", StringComparison.Ordinal)
                ? "Editor/obj/ts/" + editorRelativePath
                : "Editor/" + editorRelativePath;
            var candidate = Path.Combine(RepoRoot(), editorPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                fullPath = candidate;
        }

        return File.ReadAllText(fullPath, Encoding.UTF8);
    }

    private static Engine CreateUiEngine(params (string Path, string ExportedName)[] modules) {
        var engine = new Engine();
        engine.Execute(
            """
            const graphElementAttribute = "graphElement";
            const graphKindAttribute = "graphKind";
            const graphTypeNameAttribute = "graphTypeName";
            const projectionCollapsedAttribute = "projectionCollapsed";
            const projectionColorAttribute = "projectionColor";
            const projectionDirectedAttribute = "projectionDirected";
            const projectionInfoAttribute = "projectionInfo";
            const projectionLabelVisibleAttribute = "projectionLabelVisible";
            const projectionRankAttribute = "projectionRank";
            const projectionVisibleAttribute = "projectionVisible";
            const graphRoleAttribute = "graphRole";
            const defaultBasis = {
              nodeTypeRoot: "",
              edgeTypeRoot: "",
              relationRoot: ""
            };
            const nodeRadius = 68;
            const GraphId = {
              localId(globalId) {
                const text = String(globalId ?? "");
                const slash = text.lastIndexOf("/");
                return slash < 0 ? text : text.slice(slash + 1);
              },
              isChildOf(globalId, parentGlobalId) {
                return String(globalId).startsWith(String(parentGlobalId) + "/");
              },
              parse(value) { return Array.isArray(value) ? value : String(value).split("/"); },
              toQuery(value) { return "globalId=" + encodeURIComponent(String(value)); }
            };
            const GraphType = {
              formatRank(value) { return String(value); },
              formatRankInput(value) { return String(value ?? ""); },
              readRank(value, fallback) { return Number.isFinite(Number(value)) ? Number(value) : fallback; },
              roundRank(value) { return Math.round(value); },
              rankToRadius() { return nodeRadius; },
              fromNode(node) { return { globalId: node.globalId, label: node.displayName, visible: true }; }
            };
            globalThis.GraphProjection = class GraphProjection {
              constructor(model) { this.model = model; }
              projectFromCache() {
                return {
                  nodes: [...this.model.primitiveNodeViews()],
                  edges: [...this.model.primitiveEdgeViews()]
                };
              }
              project(physical) { return physical; }
              rank(graph) { return graph; }
              relations() { return []; }
              relationsFromCache() { return []; }
              portEndpoint() { return null; }
              nodeTypeAssignments() { return new Map(); }
              nodeTypeAssignmentsFromCache() { return new Map(); }
            };
            globalThis.GraphNode = class GraphNode {
              static from(node) { return node; }
              static fromApi(node) { return node; }
            };
            globalThis.GraphApi = class GraphApi {};
            globalThis.HtmlCanvasRenderer = class HtmlCanvasRenderer {};
            globalThis.SvgRenderer = class SvgRenderer {};
            globalThis.WebGpuRenderer = class WebGpuRenderer {};
            """);

        foreach (var module in modules) {
            engine.Execute(ReadUiJsModule(module.Path, module.ExportedName));
        }

        return engine;
    }

    private static string ReadUiJsModule(string relativePath, string exportedName) {
        var source = ReadUiFile(relativePath);
        source = Regex.Replace(source, @"^\s*import\s+[\s\S]*?;\s*", "", RegexOptions.Multiline);
        source = source.Replace($"export class {exportedName}", $"class {exportedName}");
        return source + Environment.NewLine + $"globalThis.{exportedName} = {exportedName};";
    }

    private static string RepoRoot([CallerFilePath] string sourcePath = "") {
        foreach (var candidate in new[] {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
            Path.GetDirectoryName(sourcePath)
        }) {
            var directory = new DirectoryInfo(candidate!);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GraphData.slnx"))) {
                directory = directory.Parent;
            }

            if (directory is not null) {
                return directory.FullName;
            }
        }

        Assert.Fail("Could not locate GraphData.slnx from test output directory, current directory, or source path.");
        throw new InvalidOperationException("Unreachable after Assert.Fail.");
    }
}
