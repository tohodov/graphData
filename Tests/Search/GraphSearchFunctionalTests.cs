using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GraphData.Core.Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Search;

[TestClass]
public sealed class GraphSearchFunctionalTests
{
    [TestMethod]
    public async Task Search_ShouldFindVerticesWithoutEdges()
    {
        await using var scope = TestGraphStorageScope.Create();
        var isolated = (await scope.Storage.Create("isolated")).Value!;
        var connected = (await scope.Storage.Create("connected")).Value!;
        var neighbor = (await scope.Storage.Create("neighbor")).Value!;
        await scope.Storage.Connect(connected, neighbor);

        var matches = await Search(scope.Storage, new NodeSearchQuery
        {
            Return = ["x"],
            Where = All(
                Node("x"),
                Not(Exists("y", Connected("x", "y"))))
        });

        CollectionAssert.AreEquivalent(
            new[] { isolated.LocalId },
            Names(matches));
    }

    [TestMethod]
    public async Task Search_ShouldNotReturnIgnoredSelfConnectionsAsEdges()
    {
        await using var scope = TestGraphStorageScope.Create();
        var node = (await scope.Storage.Create("self")).Value!;
        await scope.Storage.Connect(node, node);

        var matches = await Search(scope.Storage, new NodeSearchQuery
        {
            Return = ["x"],
            Where = Connected("x", "x")
        });

        Assert.AreEqual(0, matches.Count);
    }

    [TestMethod]
    public async Task Search_ShouldTreatZeroLengthPathAsSelfRelationWhenRequested()
    {
        await using var scope = TestGraphStorageScope.Create();
        var first = (await scope.Storage.Create("first")).Value!;
        var second = (await scope.Storage.Create("second")).Value!;

        var matches = await Search(scope.Storage, new NodeSearchQuery
        {
            Return = ["x"],
            Where = new NodePathSearchExpression
            {
                Left = Var("x"),
                Right = Var("x"),
                MinDepth = 0,
                MaxDepth = 0,
                IncludeSelf = true
            }
        });

        CollectionAssert.AreEquivalent(
            new[] { first.LocalId, second.LocalId },
            Names(matches));
    }

    [TestMethod]
    public async Task Search_ShouldReturnVerticesWithHighestDegreeFirst()
    {
        await using var scope = TestGraphStorageScope.Create();
        var hub = (await scope.Storage.Create("hub")).Value!;
        var mid = (await scope.Storage.Create("mid")).Value!;
        var h1 = (await scope.Storage.Create("h1")).Value!;
        var h2 = (await scope.Storage.Create("h2")).Value!;
        var h3 = (await scope.Storage.Create("h3")).Value!;
        var h4 = (await scope.Storage.Create("h4")).Value!;
        var m1 = (await scope.Storage.Create("m1")).Value!;
        var m2 = (await scope.Storage.Create("m2")).Value!;

        foreach (var node in new[] { h1, h2, h3, h4 })
        {
            await scope.Storage.Connect(hub, node);
        }

        foreach (var node in new[] { m1, m2 })
        {
            await scope.Storage.Connect(mid, node);
        }

        var matches = await Search(scope.Storage, new NodeSearchQuery
        {
            Return = ["x"],
            Where = Node("x"),
            Limit = 2
        });

        CollectionAssert.AreEqual(
            new[] { hub.LocalId, mid.LocalId },
            Names(matches));
    }

    [TestMethod]
    public async Task Search_ShouldFindVerticesDirectlyConnectedToEveryAnchor()
    {
        await using var scope = TestGraphStorageScope.Create();
        var a = (await scope.Storage.Create("a")).Value!;
        var b = (await scope.Storage.Create("b")).Value!;
        var c = (await scope.Storage.Create("c")).Value!;
        var target = (await scope.Storage.Create("target", attributes: new Dictionary<string, string> { ["role"] = "candidate" })).Value!;
        var partial = (await scope.Storage.Create("partial", attributes: new Dictionary<string, string> { ["role"] = "candidate" })).Value!;

        foreach (var anchor in new[] { a, b, c })
        {
            await scope.Storage.Connect(target, anchor);
        }

        await scope.Storage.Connect(partial, a);
        await scope.Storage.Connect(partial, b);

        var matches = await Search(scope.Storage, new NodeSearchQuery
        {
            Return = ["x"],
            Where = All(
                Attribute("x", "role", "candidate"),
                Connected("x", Literal(a.LocalId)),
                Connected("x", Literal(b.LocalId)),
                Connected("x", Literal(c.LocalId)))
        });

        CollectionAssert.AreEquivalent(
            new[] { target.LocalId },
            Names(matches));
    }

    [TestMethod]
    public async Task Search_ShouldFindVerticesConnectedToEveryAnchorWithinThreeSteps()
    {
        await using var scope = TestGraphStorageScope.Create();
        var a = (await scope.Storage.Create("anchor-a")).Value!;
        var b = (await scope.Storage.Create("anchor-b")).Value!;
        var target = (await scope.Storage.Create("target", attributes: new Dictionary<string, string> { ["role"] = "candidate" })).Value!;
        var tooFar = (await scope.Storage.Create("too-far", attributes: new Dictionary<string, string> { ["role"] = "candidate" })).Value!;
        var partial = (await scope.Storage.Create("partial", attributes: new Dictionary<string, string> { ["role"] = "candidate" })).Value!;

        await ConnectPath(scope.Storage, target, "target-a-1", "target-a-2", a);
        await ConnectPath(scope.Storage, target, "target-b-1", b);
        await ConnectPath(scope.Storage, tooFar, "far-a-1", "far-a-2", "far-a-3", a);
        await ConnectPath(scope.Storage, tooFar, "far-b-1", b);
        await ConnectPath(scope.Storage, partial, "partial-a-1", a);

        var matches = await Search(scope.Storage, new NodeSearchQuery
        {
            Return = ["x"],
            Where = All(
                Attribute("x", "role", "candidate"),
                Path("x", Literal(a.LocalId), maxDepth: 3),
                Path("x", Literal(b.LocalId), maxDepth: 3))
        });

        CollectionAssert.AreEquivalent(
            new[] { target.LocalId },
            Names(matches));
    }

    [TestMethod]
    public async Task Search_ShouldFindVerticesConnectedToAnyAnchor()
    {
        await using var scope = TestGraphStorageScope.Create();
        var a = (await scope.Storage.Create("a")).Value!;
        var b = (await scope.Storage.Create("b")).Value!;
        var first = (await scope.Storage.Create("first", attributes: new Dictionary<string, string> { ["role"] = "candidate" })).Value!;
        var second = (await scope.Storage.Create("second", attributes: new Dictionary<string, string> { ["role"] = "candidate" })).Value!;
        await scope.Storage.Create("unrelated", attributes: new Dictionary<string, string> { ["role"] = "candidate" });

        await scope.Storage.Connect(first, a);
        await scope.Storage.Connect(second, b);

        var matches = await Search(scope.Storage, new NodeSearchQuery
        {
            Return = ["x"],
            Where = All(
                Attribute("x", "role", "candidate"),
                new AnyNodeSearchExpression
                {
                    Expressions = [
                        Connected("x", Literal(a.LocalId)),
                        Connected("x", Literal(b.LocalId))
                    ]
                })
        });

        CollectionAssert.AreEquivalent(
            new[] { first.LocalId, second.LocalId },
            Names(matches));
    }

    [TestMethod]
    public async Task Search_ShouldFindDescendantsWithinHierarchyDepth()
    {
        await using var scope = TestGraphStorageScope.Create();
        var root = (await scope.Storage.Create("weapons")).Value!;
        var pistols = (await scope.Storage.Create("pistols", root)).Value!;
        var revolvers = (await scope.Storage.Create("revolvers", pistols)).Value!;
        await scope.Storage.Create("smith-wesson", revolvers);
        await scope.Storage.Create("vehicles");

        var matches = await Search(scope.Storage, new NodeSearchQuery
        {
            Return = ["x"],
            Where = new NodeDescendantSearchExpression
            {
                Ancestor = Literal(root.LocalId),
                Descendant = Var("x"),
                MaxDepth = 2
            }
        });

        CollectionAssert.AreEquivalent(
            new[] { pistols.LocalId, revolvers.LocalId },
            Names(matches));
    }

    [TestMethod]
    public async Task Search_ShouldCombineTextAndAttributePredicates()
    {
        await using var scope = TestGraphStorageScope.Create();
        var match = (await scope.Storage.Create(
            "alpha",
            attributes: new Dictionary<string, string>
            {
                ["kind"] = "weapon",
                ["description"] = "steel frame"
            })).Value!;
        await scope.Storage.Create(
            "beta",
            attributes: new Dictionary<string, string>
            {
                ["kind"] = "weapon",
                ["description"] = "polymer"
            });

        var matches = await Search(scope.Storage, new NodeSearchQuery
        {
            Return = ["x"],
            Where = All(
                Attribute("x", "kind", "weapon"),
                new NodeTextSearchExpression
                {
                    Node = Var("x"),
                    Value = "steel"
                })
        });

        CollectionAssert.AreEquivalent(
            new[] { match.LocalId },
            Names(matches));
    }

    [TestMethod]
    public async Task SearchStream_ShouldYieldMatchesAsAsyncEnumerable()
    {
        await using var scope = TestGraphStorageScope.Create();
        var first = (await scope.Storage.Create("first")).Value!;
        var second = (await scope.Storage.Create("second")).Value!;

        var service = new GraphSearchService(scope.Storage);
        var matches = new List<NodeSearchMatch>();
        await foreach (var match in service.SearchNodesStreamAsync(new NodeSearchQuery
        {
            Return = ["x"],
            Where = Node("x"),
            Limit = 2
        }))
        {
            matches.Add(match);
        }

        CollectionAssert.AreEquivalent(
            new[] { first.LocalId, second.LocalId },
            Names(matches));
    }

    private static async Task<IReadOnlyCollection<NodeSearchMatch>> Search(
        IGraphStorage storage,
        NodeSearchQuery query)
    {
        return await new GraphSearchService(storage).SearchNodesStreamAsync(query).ToArrayAsync();
    }

    private static string[] Names(IEnumerable<NodeSearchMatch> matches)
    {
        return matches.Select(static match => match.Node.LocalId).ToArray();
    }

    private static AllNodeSearchExpression All(params NodeSearchExpression[] expressions)
    {
        return new AllNodeSearchExpression { Expressions = expressions };
    }

    private static NotNodeSearchExpression Not(NodeSearchExpression expression)
    {
        return new NotNodeSearchExpression { Expression = expression };
    }

    private static ExistsNodeSearchExpression Exists(string variable, NodeSearchExpression expression)
    {
        return new ExistsNodeSearchExpression
        {
            Variables = [variable],
            Expression = expression
        };
    }

    private static NodeExistsSearchExpression Node(string variable)
    {
        return new NodeExistsSearchExpression { Node = Var(variable) };
    }

    private static NodeAttributeSearchExpression Attribute(string variable, string key, string value)
    {
        return new NodeAttributeSearchExpression
        {
            Node = Var(variable),
            Key = key,
            Value = value
        };
    }

    private static NodeConnectedSearchExpression Connected(string left, string right)
    {
        return Connected(Var(left), Var(right));
    }

    private static NodeConnectedSearchExpression Connected(string left, NodeSearchNodeSelector right)
    {
        return Connected(Var(left), right);
    }

    private static NodeConnectedSearchExpression Connected(NodeSearchNodeSelector left, NodeSearchNodeSelector right)
    {
        return new NodeConnectedSearchExpression
        {
            Left = left,
            Right = right
        };
    }

    private static NodePathSearchExpression Path(string left, NodeSearchNodeSelector right, int maxDepth)
    {
        return new NodePathSearchExpression
        {
            Left = Var(left),
            Right = right,
            MaxDepth = maxDepth
        };
    }

    private static NodeVariableSearchSelector Var(string name)
    {
        return new NodeVariableSearchSelector { Name = name };
    }

    private static NodeLiteralSearchSelector Literal(string name)
    {
        return new NodeLiteralSearchSelector { Name = name };
    }

    private static async Task ConnectPath(IGraphStorage storage, Node first, params object[] path)
    {
        var current = first;
        foreach (var segment in path)
        {
            var next = segment switch
            {
                Node node => node,
                string name => (await storage.Create(name)).Value!,
                _ => throw new ArgumentException("Path segment must be a node or a node name.", nameof(path))
            };

            await storage.Connect(current, next);
            current = next;
        }
    }
}
