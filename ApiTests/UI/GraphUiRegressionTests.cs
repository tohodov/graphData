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
              sourceGlobalId: "a",
              targetGlobalId: "b",
              sourceLocalId: "a",
              targetLocalId: "b"
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
              sourceGlobalId: "a",
              targetGlobalId: "b",
              sourceLocalId: "a",
              targetLocalId: "b"
            })])[0];
            const mergePreservesObjectState = replacement.collapsed === true;

            globalThis.__result = before && collapsed && expanded && mergePreservesObjectState;
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void GraphModel_ProjectedEdgeStateIsStoredOnRelationObject() {
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
                nodeTypeRoot: "graphdata/types/nodes",
                edgeTypeRoot: "graphdata/types/edges",
                relationRoot: "graphdata/relations"
              }
            });
            model.schema.projectionBasis = "relations";

            const relationId = model.basis.relationRoot + "/r1";
            const sourcePortId = relationId + "/source";
            const targetPortId = relationId + "/target";

            model.putNode(new GraphNode({
              globalId: "a",
              displayName: "A",
              edges: [{ sourceGlobalId: "a", targetGlobalId: sourcePortId }]
            }));
            model.putNode(new GraphNode({
              globalId: sourcePortId,
              displayName: "source",
              attributes: { [graphRoleAttribute]: "source" },
              edges: [{ sourceGlobalId: sourcePortId, targetGlobalId: relationId }]
            }));
            model.putNode(new GraphNode({
              globalId: relationId,
              displayName: "R",
              attributes: { [graphElementAttribute]: "edge" },
              edges: [{ sourceGlobalId: relationId, targetGlobalId: targetPortId }]
            }));
            model.putNode(new GraphNode({
              globalId: targetPortId,
              displayName: "target",
              attributes: { [graphRoleAttribute]: "target" },
              edges: [{ sourceGlobalId: targetPortId, targetGlobalId: "b" }]
            }));
            model.putNode(new GraphNode({
              globalId: "b",
              displayName: "B",
              edges: []
            }));

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
    public void GraphModel_AppliesUiSettingsBasis()
    {
        var engine = CreateUiEngine(("Api/wwwroot/src/domain/GraphModel.js", "GraphModel"));

        engine.Execute(
            """
            const model = new GraphModel();
            model.applyUiSettings({
              systemNodeIds: {
                graphDataRoot: "backend/root",
                nodeTypeRoot: "backend/types/nodes",
                edgeTypeRoot: "backend/types/edges",
                relationRoot: "backend/relations"
              },
              baseTypeIds: {
                nodeInstance: "backend/types/nodes/Instance"
              }
            });

            globalThis.__result = model.basis.nodeTypeRoot === "backend/types/nodes"
              && model.basis.edgeTypeRoot === "backend/types/edges"
              && model.basis.relationRoot === "backend/relations"
              && model.defaultBasis().relationRoot === "backend/relations"
              && model.schema.systemNodeIds.graphDataRoot === "backend/root"
              && model.schema.baseTypeIds.nodeInstance === "backend/types/nodes/Instance";
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void FrontendDefaults_DoNotHardcodeGraphSystemNodeIds()
    {
        var attributes = ReadUiFile("Api/wwwroot/src/domain/graphAttributes.ts");
        var html = ReadUiFile("Api/wwwroot/index.html");

        Assert.IsFalse(attributes.Contains("graphdata/types", StringComparison.Ordinal));
        Assert.IsFalse(attributes.Contains("graphdata/relations", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("graphdata/types", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("graphdata/relations", StringComparison.Ordinal));
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
                { key: "collapsed", sourceGlobalId: "a", targetGlobalId: "b", collapsed: true },
                { key: "visible", sourceGlobalId: "b", targetGlobalId: "a" }
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
              { key: "collapsed", sourceGlobalId: "a", targetGlobalId: "b", collapsed: true }
            ]);
            const visibleEdge = runSimulationWith([
              { key: "visible", sourceGlobalId: "a", targetGlobalId: "b" }
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
            ("Api/wwwroot/src/GraphViewer.js", "GraphViewer"));

        engine.Execute(
            """
            const edge = new GraphEdge({
              sourceGlobalId: "a",
              targetGlobalId: "b",
              sourceLocalId: "a",
              targetLocalId: "b-local"
            });

            function makeViewer(loadedNames) {
              const calls = [];
              const viewer = Object.create(GraphViewer.prototype);
              viewer.graph = {
                loaded: new Map(loadedNames.map(name => [name, {}])),
                parentByNode: new Map(),
                rootName: "a",
                isEdgeCollapsed(edge) { return edge.collapsed === true; },
                collapseEdge(edge) {
                  edge.collapsed = true;
                  calls.push("collapse:" + edge.key);
                },
                expandEdge(edge) {
                  edge.collapsed = false;
                  calls.push("expand:" + edge.key);
                }
              };
              viewer.loadNeighbor = (anchorName, neighborLocalId) => calls.push("load:" + anchorName + ":" + neighborLocalId);
              viewer.collapseNode = name => calls.push("collapseNode:" + name);
              viewer.render = () => calls.push("render");
              viewer.runSimulation = frames => calls.push("simulation:" + frames);
              viewer.setStatus = message => calls.push("status:" + message);
              viewer.displayName = id => id;
              return { viewer, calls };
            }

            const loaded = makeViewer(["a", "b"]);
            const loadedControl = loaded.viewer.edgeEndpointControl(edge, "a");
            loaded.viewer.handleEndpointClick(edge, "a");
            const loadedClickCollapsesEdge = loaded.calls.includes("collapse:" + edge.key)
              && edge.collapsed === true
              && !loaded.calls.some(call => call.startsWith("load:"));

            const collapsed = makeViewer(["a", "b"]);
            const collapsedControl = collapsed.viewer.edgeEndpointControl(edge, "a");
            collapsed.viewer.handleEndpointClick(edge, "a");
            const collapsedClickExpandsOnly = collapsed.calls.includes("expand:" + edge.key)
              && edge.collapsed === false
              && collapsed.calls.includes("render")
              && !collapsed.calls.some(call => call.startsWith("load:"));

            edge.collapsed = true;
            const frontier = makeViewer(["a"]);
            const frontierControl = frontier.viewer.edgeEndpointControl(edge, "a");
            frontier.viewer.handleEndpointClick(edge, "a");
            const frontierClickLoadsNeighbor = frontier.calls.includes("load:a:b-local")
              && !frontier.calls.some(call => call.startsWith("collapse:"));
            edge.collapsed = false;

            const treeFromParent = makeViewer(["a", "b"]);
            treeFromParent.viewer.graph.parentByNode.set("b", "a");
            const parentTreeControl = treeFromParent.viewer.edgeEndpointControl(edge, "a");
            treeFromParent.viewer.handleEndpointClick(edge, "a");
            const parentEndCollapsesChild = treeFromParent.calls.includes("collapseNode:b");

            const treeFromChild = makeViewer(["a", "b"]);
            treeFromChild.viewer.graph.parentByNode.set("b", "a");
            const childTreeControl = treeFromChild.viewer.edgeEndpointControl(edge, "b");
            treeFromChild.viewer.handleEndpointClick(edge, "b");
            const childEndCollapsesChild = treeFromChild.calls.includes("collapseNode:b");

            globalThis.__result = loadedClickCollapsesEdge
              && loadedControl.kind === "collapse"
              && collapsedClickExpandsOnly
              && collapsedControl.kind === "expand"
              && frontierClickLoadsNeighbor
              && frontierControl.kind === "expand"
              && parentTreeControl?.kind === "collapse"
              && childTreeControl?.kind === "collapse"
              && parentEndCollapsesChild
              && childEndCollapsesChild;
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void GraphViewer_NodeCollapseControlsSkipEdgeElementNodes() {
        var engine = CreateUiEngine(
            ("Api/wwwroot/src/domain/GraphNode.js", "GraphNode"),
            ("Api/wwwroot/src/GraphViewer.js", "GraphViewer"));

        engine.Execute(
            """
            const viewer = Object.create(GraphViewer.prototype);
            viewer.graph = {
              rootName: "root",
              loaded: new Map([
                ["root", { globalId: "root", attributes: {} }],
                ["node-child", { globalId: "node-child", attributes: { [graphElementAttribute]: "node" } }],
                ["edge-child", { globalId: "edge-child", attributes: { [graphElementAttribute]: "edge" } }]
              ]),
              parentByNode: new Map([
                ["node-child", "root"],
                ["edge-child", "root"]
              ])
            };

            globalThis.__result = viewer.canCollapseNode("node-child") === true
              && viewer.canCollapseNode("edge-child") === false;
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void GraphViewer_EdgeCollapseExpandDoesNotMoveExistingNodes() {
        var engine = CreateUiEngine(
            ("Api/wwwroot/src/domain/GraphEdge.js", "GraphEdge"),
            ("Api/wwwroot/src/domain/GraphModel.js", "GraphModel"),
            ("Api/wwwroot/src/GraphViewer.js", "GraphViewer"));

        engine.Execute(
            """
            const model = new GraphModel();
            const edge = new GraphEdge({
              sourceGlobalId: "a",
              targetGlobalId: "b",
              sourceLocalId: "a",
              targetLocalId: "b"
            });

            function loadedNode(name, displayName, edges) {
              return {
                name,
                displayName,
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
              sourceGlobalId: "root",
              targetGlobalId: "child",
              sourceLocalId: "root",
              targetLocalId: "child"
            });

            function loadedNode(name, displayName, edges) {
              return {
                name,
                displayName,
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
              && !model.loaded.has("child")
              && model.positions.has("child")
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
                  sourceGlobalId: "root",
                  targetGlobalId: "root/" + name,
                  sourceLocalId: "root",
                  targetLocalId: name,
                  neighborLocalId: name
                }))
              }],
              edges: []
            }, [], { selectRoot: false });

            const root = viewer.graph.loaded.get("root");
            const edge = root.edges[0];
            const control = viewer.edgeEndpointControl(edge, "root");
            const rootPosition = viewer.graph.positions.get("root");
            const angles = root.edges.map(item => item.frontierAngle).sort((a, b) => a - b);
            const step = Math.PI / 2;
            const evenlySpaced = angles.every((angle, index) => {
              const next = angles[(index + 1) % angles.length] + (index === angles.length - 1 ? Math.PI * 2 : 0);
              return Math.abs((next - angle) - step) < 0.000001;
            });

            viewer.storeNodeExpansion(new GraphNode({
              localId: "a",
              globalId: "root/a",
              edges: [{
                sourceGlobalId: "root",
                targetGlobalId: "root/a",
                sourceLocalId: "root",
                targetLocalId: "a",
                neighborLocalId: "root"
              }]
            }), "root", { select: false });
            const loadedPosition = viewer.graph.positions.get("root/a");
            const loadedOffset = {
              x: loadedPosition.x - rootPosition.x,
              y: loadedPosition.y - rootPosition.y
            };
            const rootEdge = root.edges.find(item => item.targetGlobalId === "root/a");
            const angleAfterLoad = rootEdge.frontierAngle;
            viewer.graph.positions.set("root/a", { x: rootPosition.x + 92, y: rootPosition.y });
            viewer.refreshEdgeAngles();

            globalThis.__result = root.edges.length === 4
              && viewer.graph.physicalGraph().edges.length === 4
              && !viewer.graph.positions.has("root/b")
              && control?.kind === "expand"
              && control?.text === "+"
              && Number.isFinite(control?.angle)
              && evenlySpaced
              && Math.abs(loadedOffset.x) < 0.000001
              && Math.abs(loadedOffset.y + 92) < 0.000001
              && Math.abs(angleAfterLoad + Math.PI / 2) < 0.000001
              && Math.abs(rootEdge.frontierAngle) < 0.000001;
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
    }

    [TestMethod]
    public void WebGpuCanvas_RendersFrontierEndpointControlsAtStoredAngle() {
        var engine = CreateUiEngine(("Api/wwwroot/src/ui/WebGpuGraphCanvas.js", "WebGpuGraphCanvas"));

        engine.Execute(
            """
            const buttons = [];
            const calls = [];
            const canvas = Object.create(WebGpuGraphCanvas.prototype);
            canvas.view = { x: 0, y: 0, scale: 1 };
            canvas.positions = new Map([["a", { x: 10, y: 20 }]]);
            canvas.callbacks = {
              edgeEndpointControl() {
                return { kind: "expand", text: "+", title: "expand", angle: 0, otherName: "b" };
              },
              activateEdgeEndpoint() {},
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
            const edge = { key: "ab", sourceGlobalId: "a", targetGlobalId: "b" };
            const nodesByName = new Map([["a", { name: "a", viewRadius: 34 }]]);

            canvas.renderEdgeEndpointControl(fragment, rect, edge, "a", "b", nodesByName, new Set());
            const first = buttons[0].style.transform.match(/translate\(([-0-9.]+)px, ([-0-9.]+)px\)/);
            buttons.length = 0;
            canvas.positions.set("a", { x: 100, y: 80 });
            canvas.renderEdgeEndpointControl(fragment, rect, edge, "a", "b", nodesByName, new Set());
            const second = buttons[0].style.transform.match(/translate\(([-0-9.]+)px, ([-0-9.]+)px\)/);

            const firstOffset = {
              x: Number(first[1]) - 10,
              y: Number(first[2]) - 20
            };
            const secondOffset = {
              x: Number(second[1]) - 100,
              y: Number(second[2]) - 80
            };
            canvas.updateDynamicGraph();

            globalThis.__result = buttons.length === 1
              && Math.abs(firstOffset.x - 43) < 0.000001
              && Math.abs(firstOffset.y) < 0.000001
              && Math.abs(secondOffset.x - firstOffset.x) < 0.000001
              && Math.abs(secondOffset.y - firstOffset.y) < 0.000001
              && calls.join(",") === "sync,write,labels,renderer,draw";
            """);

        Assert.IsTrue(engine.Evaluate("__result").AsBoolean());
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
            StringAssert.Contains(source, "callbacks.edgeEndpointControl?.(edge, anchorName)");
            StringAssert.Contains(source, "callbacks.activateEdgeEndpoint?.(edge, anchorName)");
            StringAssert.Contains(source, "pointAtAngle(anchor, control.angle");
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

            StringAssert.Contains(source, "edgeEndpointControl: (edge, anchorName) => this.edgeEndpointControl(edge, anchorName)");
            StringAssert.Contains(source, "activateEdgeEndpoint: (edge, anchorName) => this.handleEndpointClick(edge, anchorName)");
            AssertMatches(source, @"if\s*\(anchorLoaded && !otherLoaded\)\s*\{\s*return\s*\{.*?kind:\s*""expand"".*?text:\s*""\+""", path);
            AssertMatches(source, @"if\s*\(anchorLoaded && !otherLoaded\)\s*\{\s*this\.loadNeighbor\(anchorName,\s*this\.edgeNeighborLocalId\(edge,\s*anchorName\)\);", path);
            AssertMatches(source, @"if\s*\(!anchorLoaded && otherLoaded\)\s*\{\s*this\.loadNeighbor\(otherName,\s*this\.edgeNeighborLocalId\(edge,\s*otherName\)\);", path);
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

    private static void AssertMatches(string source, string pattern, string path) {
        Assert.IsTrue(
            Regex.IsMatch(source, pattern, RegexOptions.Singleline),
            $"Expected UI regression pattern not found in {path}: {pattern}");
    }

    private static string ReadUiFile(string relativePath) {
        var fullPath = Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath)
            && relativePath.StartsWith("Api/wwwroot/", StringComparison.Ordinal)
            && relativePath.EndsWith(".js", StringComparison.Ordinal)) {
            var compiledRelativePath = "Api/obj/ts/" + relativePath["Api/wwwroot/".Length..];
            var compiledPath = Path.Combine(RepoRoot(), compiledRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(compiledPath)) {
                fullPath = compiledPath;
            }
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
            const nodeRadius = 34;
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
              project(physical) { return physical; }
              rank(graph) { return graph; }
              relations() { return []; }
              portEndpoint() { return null; }
              nodeTypeAssignments() { return new Map(); }
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
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GraphData.sln"))) {
                directory = directory.Parent;
            }

            if (directory is not null) {
                return directory.FullName;
            }
        }

        Assert.Fail("Could not locate GraphData.sln from test output directory, current directory, or source path.");
        throw new InvalidOperationException("Unreachable after Assert.Fail.");
    }
}
