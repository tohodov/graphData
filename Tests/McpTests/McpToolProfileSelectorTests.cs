using GraphData.Mcp.Runtime;
using GraphData.Mcp.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[RelevantTestClass]
public sealed class McpToolProfileSelectorTests {
    [TestMethod]
    public void Read_DefaultsToSemanticWhenProfileIsNotConfigured() {
        WithToolProfileEnvironment(null, () => {
            var configuration = new ConfigurationBuilder().Build();

            var profile = McpToolProfileSelector.Read(configuration);

            Assert.AreEqual(McpToolProfile.Semantic, profile);
        });
    }

    [TestMethod]
    public void Read_UsesConfiguredProfile() {
        WithToolProfileEnvironment(null, () => {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> {
                    ["Mcp:ToolProfile"] = "Raw"
                })
                .Build();

            var profile = McpToolProfileSelector.Read(configuration);

            Assert.AreEqual(McpToolProfile.Raw, profile);
        });
    }

    [TestMethod]
    public void Read_EnvironmentVariableOverridesConfiguration() {
        WithToolProfileEnvironment("All", () => {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> {
                    ["Mcp:ToolProfile"] = "Raw"
                })
                .Build();

            var profile = McpToolProfileSelector.Read(configuration);

            Assert.AreEqual(McpToolProfile.All, profile);
        });
    }

    [TestMethod]
    public void Read_RejectsUnknownProfile() {
        WithToolProfileEnvironment(null, () => {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> {
                    ["Mcp:ToolProfile"] = "Everything"
                })
                .Build();

            Assert.ThrowsException<InvalidOperationException>(() => McpToolProfileSelector.Read(configuration));
        });
    }

    [TestMethod]
    public void GetToolTypes_SelectsSemanticToolSurface() {
        CollectionAssert.AreEqual(
            new[] { typeof(GraphDataSemanticTools) },
            McpToolProfileSelector.GetToolTypes(McpToolProfile.Semantic));
    }

    [TestMethod]
    public void GetToolTypes_SelectsRawToolSurface() {
        CollectionAssert.AreEqual(
            new[] { typeof(GraphDataRawTools) },
            McpToolProfileSelector.GetToolTypes(McpToolProfile.Raw));
    }

    [TestMethod]
    public void GetToolTypes_SelectsAllToolSurface() {
        CollectionAssert.AreEqual(
            new[] { typeof(GraphDataTools) },
            McpToolProfileSelector.GetToolTypes(McpToolProfile.All));
    }

    private static void WithToolProfileEnvironment(string? value, Action action) {
        var previous = Environment.GetEnvironmentVariable(McpToolProfileSelector.EnvironmentVariableName);
        try {
            Environment.SetEnvironmentVariable(McpToolProfileSelector.EnvironmentVariableName, value);
            action();
        } finally {
            Environment.SetEnvironmentVariable(McpToolProfileSelector.EnvironmentVariableName, previous);
        }
    }
}
