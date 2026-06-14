using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Ui;

[TestClass]
public sealed class GraphUiRegressionTests {
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
