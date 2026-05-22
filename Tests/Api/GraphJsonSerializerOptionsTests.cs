using GraphData.Api.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Api;

[TestClass]
public sealed class GraphJsonSerializerOptionsTests
{
    [TestMethod]
    public void Create_ReturnsOptionsThatCanBeMarkedReadOnly()
    {
        var options = GraphJsonSerializerOptions.Create();

        options.MakeReadOnly();

        Assert.IsTrue(options.IsReadOnly);
    }
}
