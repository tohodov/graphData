using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Jint;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Ui;

[TestClass]
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

            const before = model.visibleGraph().edges[0].collapsed === false;
            model.collapseEdge(edge);
            const collapsedGraph = model.visibleGraph();
            const collapsed = collapsedGraph.nodes.length === 2
              && collapsedGraph.edges.length === 1
              && collapsedGraph.edges[0].collapsed === true
              && model.isEdgeCollapsed(edge);
            model.expandEdge(edge);
            const expanded = model.visibleGraph().edges[0].collapsed === false
              && !model.isEdgeCollapsed(edge);
            model.collapseEdge(edge);
            model.resetGraph();
            const reset = model.collapsedEdges.size === 0;

            globalThis.__result = before && collapsed && expanded && reset;
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

            function makeViewer(loadedNames, isCollapsed = false) {
              const calls = [];
              const viewer = Object.create(GraphViewer.prototype);
              viewer.graph = {
                loaded: new Map(loadedNames.map(name => [name, {}])),
                parentByNode: new Map(),
                rootName: "a",
                isEdgeCollapsed() { return isCollapsed; },
                collapseEdge(edge) {
                  isCollapsed = true;
                  calls.push("collapse:" + edge.key);
                },
                expandEdge(edge) {
                  isCollapsed = false;
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
              && !loaded.calls.some(call => call.startsWith("load:"));

            const collapsed = makeViewer(["a", "b"], true);
            const collapsedControl = collapsed.viewer.edgeEndpointControl(edge, "a");
            collapsed.viewer.handleEndpointClick(edge, "a");
            const collapsedClickExpandsOnly = collapsed.calls.includes("expand:" + edge.key)
              && collapsed.calls.includes("render")
              && !collapsed.calls.some(call => call.startsWith("load:"));

            const frontier = makeViewer(["a"]);
            const frontierControl = frontier.viewer.edgeEndpointControl(edge, "a");
            frontier.viewer.handleEndpointClick(edge, "a");
            const frontierClickLoadsNeighbor = frontier.calls.includes("load:a:b-local")
              && !frontier.calls.some(call => call.startsWith("collapse:"));

            const treeFromParent = makeViewer(["a", "b"]);
            treeFromParent.viewer.graph.parentByNode.set("b", "a");
            treeFromParent.viewer.handleEndpointClick(edge, "a");
            const parentEndCollapsesChild = treeFromParent.calls.includes("collapseNode:b");

            const treeFromChild = makeViewer(["a", "b"]);
            treeFromChild.viewer.graph.parentByNode.set("b", "a");
            treeFromChild.viewer.handleEndpointClick(edge, "b");
            const childEndCollapsesChild = treeFromChild.calls.includes("collapseNode:b");

            globalThis.__result = loadedClickCollapsesEdge
              && loadedControl.kind === "collapse"
              && collapsedClickExpandsOnly
              && collapsedControl.kind === "expand"
              && frontierClickLoadsNeighbor
              && frontierControl.kind === "expand"
              && parentEndCollapsesChild
              && childEndCollapsesChild;
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
            StringAssert.Contains(source, "pointOnCircle(anchor, other");
        }
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

    private static void AssertMatches(string source, string pattern, string path) {
        Assert.IsTrue(
            Regex.IsMatch(source, pattern, RegexOptions.Singleline),
            $"Expected UI regression pattern not found in {path}: {pattern}");
    }

    private static string ReadUiFile(string relativePath) {
        var fullPath = Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
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
              nodeTypeRoot: "graphdata/types/nodes",
              edgeTypeRoot: "graphdata/types/edges",
              relationRoot: "graphdata/relations"
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
            class GraphProjection {
              constructor(model) { this.model = model; }
              project(physical) { return physical; }
              rank(graph) { return graph; }
              relations() { return []; }
              portEndpoint() { return null; }
              nodeTypeAssignments() { return new Map(); }
            }
            class GraphNode {
              static from(node) { return node; }
              static fromApi(node) { return node; }
            }
            class GraphApi {}
            class WebGpuRenderer {}
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
