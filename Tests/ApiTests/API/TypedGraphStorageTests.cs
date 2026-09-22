using System.Reflection;
using System.Text.Json;
using Abstractions;
using GraphData.Api.Controllers;
using GraphData.Api.Runtime;
using GraphData.Core.Services;
using GraphData.Typed;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Storage;

namespace GraphData.Tests.Api;

[RelevantTestClass]
public sealed class TypedGraphStorageTests
{
    [TestMethod]
    public async Task MissingMode_KeepsLegacyStorageAndRegistersTypedBoundary()
    {
        using var directory = new TestDirectory();
        var services = new ServiceCollection();
        services.AddSingleton<ICancellationTokenAccessor, CancellationTokensAccessorMock>();
        services.AddConfiguredGraphStorage(Configuration(new()
        {
            ["GraphStorage:RootPath"] = directory.Root
        }));
        using var provider = services.BuildServiceProvider();

        Assert.AreEqual(GraphStorageMode.Legacy, provider.GetRequiredService<GraphStorageSelection>().Mode);
        Assert.IsInstanceOfType<SymLinkGraphStorage>(provider.GetRequiredService<IGraphStorage>());
        await provider.GetRequiredService<Graph>().OpenAsync();
        Assert.IsNotNull(provider.GetRequiredService<ITypedGraph>());
    }

    [TestMethod]
    public async Task SemanticMode_OpensTypedStorageWithoutRawServices()
    {
        using var directory = new TestDirectory();
        var legacyRoot = Path.Combine(directory.Root, "legacy");
        var semanticRoot = Path.Combine(directory.Root, "semantic");
        var configuration = Configuration(new()
        {
            ["GraphStorage:Mode"] = "semantic",
            ["GraphStorage:RootPath"] = legacyRoot,
            ["SemanticGraphStorage:RootPath"] = semanticRoot
        });

        using (var provider = new ServiceCollection().AddConfiguredGraphStorage(configuration).BuildServiceProvider())
        {
            Assert.AreEqual(GraphStorageMode.Semantic, provider.GetRequiredService<GraphStorageSelection>().Mode);
            Assert.IsNull(provider.GetService<IGraphStorage>());
            Assert.IsNull(provider.GetService<Graph>());
            Assert.IsNull(provider.GetService<Core.Services.GraphService>());
            var graph = provider.GetRequiredService<ITypedGraph>();
            await graph.CreateTypeAsync(new TypedType("NodeTypes/Person", TypedElementKind.Instance, false, [], [], []));
            await graph.CreateInstanceAsync("person", ["NodeTypes/Person"], new Dictionary<string, string>());
        }

        using (var reopened = new ServiceCollection().AddConfiguredGraphStorage(configuration).BuildServiceProvider())
        {
            var person = await reopened.GetRequiredService<ITypedGraph>().GetAsync("person");
            CollectionAssert.AreEqual(new[] { "NodeTypes/Person" }, person.TypeIds.ToArray());
        }
        Assert.IsFalse(Directory.Exists(legacyRoot));
    }

    [DataTestMethod]
    [DataRow("unknown")]
    [DataRow("")]
    public void UnknownMode_IsRejectedBeforeStorageRegistration(string mode)
    {
        var services = new ServiceCollection();
        var error = Assert.ThrowsException<InvalidOperationException>(() => services.AddConfiguredGraphStorage(Configuration(new()
        {
            ["GraphStorage:Mode"] = mode
        })));
        StringAssert.Contains(error.Message, "Unknown GraphStorage:Mode");
        Assert.IsFalse(services.Any(static descriptor => descriptor.ServiceType == typeof(ITypedGraphStore)));
    }

    [TestMethod]
    public void SemanticMode_RequiresItsOwnExplicitRoot()
    {
        using var directory = new TestDirectory();
        var error = Assert.ThrowsException<InvalidOperationException>(() => new ServiceCollection().AddConfiguredGraphStorage(Configuration(new()
        {
            ["GraphStorage:Mode"] = "semantic",
            ["GraphStorage:RootPath"] = directory.Root
        })));
        StringAssert.Contains(error.Message, "SemanticGraphStorage:RootPath");
        Assert.IsFalse(Directory.Exists(directory.Root));
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("nested")]
    public void SemanticMode_RejectsLegacyRootOverlap(string child)
    {
        using var directory = new TestDirectory();
        var error = Assert.ThrowsException<InvalidOperationException>(() => new ServiceCollection().AddConfiguredGraphStorage(Configuration(new()
        {
            ["GraphStorage:Mode"] = "semantic",
            ["GraphStorage:RootPath"] = directory.Root,
            ["SemanticGraphStorage:RootPath"] = Path.Combine(directory.Root, child)
        })));
        StringAssert.Contains(error.Message, "must not overlap");
        Assert.IsFalse(Directory.Exists(directory.Root));
    }

    [DataTestMethod]
    [DataRow(typeof(GraphController))]
    [DataRow(typeof(UiController))]
    public async Task SemanticGuard_RejectsLegacyControllerBeforeActivation(Type controllerType)
    {
        var reachedNext = false;
        var context = ContextFor(controllerType);
        var middleware = new TypedGraphCompatibilityMiddleware(_ =>
        {
            reachedNext = true;
            throw new AssertFailedException("Legacy controller activation must not run in semantic mode.");
        }, new GraphStorageSelection(GraphStorageMode.Semantic));

        await middleware.InvokeAsync(context);

        Assert.IsFalse(reachedNext);
        Assert.AreEqual(StatusCodes.Status501NotImplemented, context.Response.StatusCode);
        StringAssert.StartsWith(context.Response.ContentType!, "application/problem+json");
        context.Response.Body.Position = 0;
        using var problem = await JsonDocument.ParseAsync(context.Response.Body);
        StringAssert.Contains(problem.RootElement.GetProperty("detail").GetString()!, "/api/typed-graph");
    }

    [DataTestMethod]
    [DataRow(GraphStorageMode.Legacy, typeof(GraphController))]
    [DataRow(GraphStorageMode.Semantic, typeof(TypedGraphController))]
    public async Task CompatibilityGuard_AllowsSupportedControllers(GraphStorageMode mode, Type controllerType)
    {
        var reachedNext = false;
        var middleware = new TypedGraphCompatibilityMiddleware(_ =>
        {
            reachedNext = true;
            return Task.CompletedTask;
        }, new GraphStorageSelection(mode));

        await middleware.InvokeAsync(ContextFor(controllerType));

        Assert.IsTrue(reachedNext);
    }

    [TestMethod]
    public async Task SemanticMode_HomeExplainsUnavailableEditorInsteadOfLoadingCanvas()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/";
        context.Response.Body = new MemoryStream();
        var middleware = new TypedGraphCompatibilityMiddleware(_ => throw new AssertFailedException("Static editor must not load."),
            new GraphStorageSelection(GraphStorageMode.Semantic));

        await middleware.InvokeAsync(context);

        Assert.AreEqual(StatusCodes.Status501NotImplemented, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        StringAssert.Contains(body, "Текущий редактор");
        StringAssert.Contains(body, "/api/typed-graph/types");
    }

    static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    static DefaultHttpContext ContextFor(Type controllerType)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/test";
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new ControllerActionDescriptor { ControllerTypeInfo = controllerType.GetTypeInfo() }), "test"));
        return context;
    }

    sealed class TestDirectory : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "GraphDataTypedApiTests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
