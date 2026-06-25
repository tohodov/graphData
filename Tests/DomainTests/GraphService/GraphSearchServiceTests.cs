using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Abstractions;
using GraphData.Core.Models;
using GraphData.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Search;

[RelevantTestClass]
public sealed class GraphSearchServiceTests : StorageTests
{
    [TestMethod]
    public async Task Search_ShouldFindVerticesWithoutEdges()
    {
        var isolated = await Storage.Create(new("isolated"));
        var connected = await Storage.Create(new("connected"));
        var neighbor = await Storage.Create(new("neighbor"));
        await Storage.Connect(connected.GlobalId, neighbor.GlobalId);

        var matches = await Search(Storage, new NodeSearchQuery
        {
            Return = ["x"],
            Where = All(
                Node("x"),
                Not(Exists("y", Connected("x", "y"))))
        });

        CollectionAssert.AreEquivalent(
            new[] { isolated.LocalId },
            matches.Select(x => x.Node.LocalId).ToArray());
    }

    [TestMethod]
    public async Task Search_ShouldNotReturnIgnoredSelfConnectionsAsEdges()
    {
        var node = await Storage.Create(new("self"));
        await Storage.Connect(node.GlobalId, node.GlobalId);

        var matches = await Search(Storage, new NodeSearchQuery
        {
            Return = ["x"],
            Where = Connected("x", "x")
        });

        Assert.AreEqual(0, matches.Count);
    }

    [TestMethod]
    public async Task Search_ShouldTreatZeroLengthPathAsSelfRelationWhenRequested()
    {
        var first = await Storage.Create(new("first"));
        var second = await Storage.Create(new("second"));

        var matches = await Search(Storage, new NodeSearchQuery
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
            matches.Select(x => x.Node.LocalId).ToArray());
    }

    [TestMethod]
    public async Task Search_ShouldReturnMatchingVerticesWithoutImplicitRanking()
    {
        var hub = await Storage.Create(new("hub"));
        var mid = await Storage.Create(new("mid"));
        var h1 = await Storage.Create(new("h1"));
        var h2 = await Storage.Create(new("h2"));
        var h3 = await Storage.Create(new("h3"));
        var h4 = await Storage.Create(new("h4"));
        var m1 = await Storage.Create(new("m1"));
        var m2 = await Storage.Create(new("m2"));

        foreach (var node in new[] { h1, h2, h3, h4 })
        {
            await Storage.Connect(hub.GlobalId, node.GlobalId);
        }

        foreach (var node in new[] { m1, m2 })
        {
            await Storage.Connect(mid.GlobalId, node.GlobalId);
        }

        var matches = await Search(Storage, new NodeSearchQuery
        {
            Return = ["x"],
            Where = Node("x"),
            Limit = 8
        });

        CollectionAssert.AreEquivalent(
            new[] { hub.LocalId, mid.LocalId, h1.LocalId, h2.LocalId, h3.LocalId, h4.LocalId, m1.LocalId, m2.LocalId },
            matches.Select(x => x.Node.LocalId).ToArray());
    }

    [TestMethod]
    public async Task Search_ShouldFindVerticesDirectlyConnectedToEveryAnchor()
    {
        var a = await Storage.Create("a");
        var b = await Storage.Create("b");
        var c = await Storage.Create("c");
        var target = await Storage.Create("target", attributes: new Dictionary<string, string> { ["role"] = "candidate" });
        var partial = await Storage.Create("partial", attributes: new Dictionary<string, string> { ["role"] = "candidate" });

        foreach (var anchor in new[] { a, b, c })
        {
            await Storage.Connect(target.GlobalId, anchor.GlobalId);
        }

        await Storage.Connect(partial.GlobalId, a.GlobalId);
        await Storage.Connect(partial.GlobalId, b.GlobalId);

        var matches = await Search(Storage, new NodeSearchQuery
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
            matches.Select(x => x.Node.LocalId).ToArray());
    }

    [TestMethod]
    public async Task Search_ShouldFindVerticesConnectedToEveryAnchorWithinThreeSteps()
    {
        var a = await Storage.Create("anchor-a");
        var b = await Storage.Create("anchor-b");
        var target = await Storage.Create("target", attributes: new Dictionary<string, string> { ["role"] = "candidate" });
        var tooFar = await Storage.Create("too-far", attributes: new Dictionary<string, string> { ["role"] = "candidate" });
        var partial = await Storage.Create("partial", attributes: new Dictionary<string, string> { ["role"] = "candidate" });

        await ConnectPath(Storage, target, "target-a-1", "target-a-2", a);
        await ConnectPath(Storage, target, "target-b-1", b);
        await ConnectPath(Storage, tooFar, "far-a-1", "far-a-2", "far-a-3", a);
        await ConnectPath(Storage, tooFar, "far-b-1", b);
        await ConnectPath(Storage, partial, "partial-a-1", a);

        var matches = await Search(Storage, new NodeSearchQuery
        {
            Return = ["x"],
            Where = All(
                Attribute("x", "role", "candidate"),
                Path("x", Literal(a.LocalId), maxDepth: 3),
                Path("x", Literal(b.LocalId), maxDepth: 3))
        });

        CollectionAssert.AreEquivalent(
            new[] { target.LocalId },
            matches.Select(x => x.Node.LocalId).ToArray());
    }

    [TestMethod]
    public async Task Search_ShouldFindVerticesConnectedToAnyAnchor()
    {
        var a = await Storage.Create("a");
        var b = await Storage.Create("b");
        var first = await Storage.Create("first", attributes: new Dictionary<string, string> { ["role"] = "candidate" });
        var second = await Storage.Create("second", attributes: new Dictionary<string, string> { ["role"] = "candidate" });
        await Storage.Create("unrelated", attributes: new Dictionary<string, string> { ["role"] = "candidate" });

        await Storage.Connect(first.GlobalId, a.GlobalId);
        await Storage.Connect(second.GlobalId, b.GlobalId);

        var matches = await Search(Storage, new NodeSearchQuery
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
            matches.Select(x => x.Node.LocalId).ToArray());
    }

    [TestMethod]
    public async Task Search_ShouldFindDescendantsWithinHierarchyDepth()
    {
        var root = await Storage.Create("weapons");
        var pistols = await Storage.Create("pistols", root.GlobalId);
        var revolvers = await Storage.Create("revolvers", pistols.GlobalId);
        await Storage.Create("smith-wesson", revolvers.GlobalId);
        await Storage.Create("vehicles");

        var matches = await Search(Storage, new NodeSearchQuery
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
            new[] { pistols.GlobalId.ToString(), revolvers.GlobalId.ToString() },
            matches.Select(x => x.Node.GlobalId.ToString()).ToArray());
    }

    [TestMethod]
    public async Task Search_ShouldCombineTextAndAttributePredicates()
    {
        var match = await Storage.Create(
            "alpha",
            attributes: new Dictionary<string, string>
            {
                ["kind"] = "weapon",
                ["description"] = "steel frame"
            });
        await Storage.Create(
            "beta",
            attributes: new Dictionary<string, string>
            {
                ["kind"] = "weapon",
                ["description"] = "polymer"
            });

        var matches = await Search(Storage, new NodeSearchQuery
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
            matches.Select(x => x.Node.LocalId).ToArray());
    }

    [TestMethod]
    public async Task SearchStream_ShouldYieldMatchesAsAsyncEnumerable()
    {
        var first = await Storage.Create("first");
        var second = await Storage.Create("second");

        var service = new GraphSearchService(Storage);
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
            matches.Select(x => x.Node.LocalId).ToArray());
    }

    [TestMethod]
    public async Task SearchStream_ShouldYieldFirstMatchBeforeInspectingLaterCandidates()
    {
        var first = new StreamingProbeNode(
            "a-match",
            () => new Dictionary<string, string> { ["kind"] = "target" });
        var laterCandidateWasRead = false;
        var laterCandidate = new StreamingProbeNode(
            "z-later",
            () =>
            {
                laterCandidateWasRead = true;
                throw new AssertFailedException("Search inspected a later candidate before yielding the first match.");
            });
        var service = new GraphSearchService(new StreamingProbeStorage(first, laterCandidate));

        await using var matches = service.SearchNodesStreamAsync(new NodeSearchQuery
        {
            Return = ["x"],
            Where = Attribute("x", "kind", "target"),
            Limit = 1
        }).GetAsyncEnumerator();

        Assert.IsTrue(await matches.MoveNextAsync());
        Assert.AreEqual(first.LocalId, matches.Current.Node.LocalId);
        Assert.IsFalse(laterCandidateWasRead);
    }

    private static async Task<IReadOnlyCollection<NodeSearchMatch>> Search(
        IGraphStorage storage,
        NodeSearchQuery query)
    {
        return await new GraphSearchService(storage).SearchNodesStreamAsync(query).ToArrayAsync();
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

    private static async Task ConnectPath(IGraphStorage storage, NodeState first, params object[] path)
    {
        var current = first;
        foreach (var segment in path)
        {
            var next = segment switch
            {
                NodeState node => node,
                string name => await storage.Create(new(name)),
                _ => throw new ArgumentException("Path segment must be a node or a node name.", nameof(path))
            };

            await storage.Connect(current.GlobalId, next.GlobalId);
            current = next;
        }
    }

    private sealed class StreamingProbeStorage(params NodeState[] nodes) : IGraphStorage
    {
        public Task Disconnect(NodeRef sourcePath, NodeRef targetPath) => throw new NotSupportedException();
        Task<NodeState> IGraphStorage.Create(NodeLocalId name, NodeRef? parent, IDictionary<string, string>? attributes) => throw new NotImplementedException();
        Task<NodeState?> IGraphStorage.Get(NodeRef path) => throw new NotImplementedException();
        Task IGraphStorage.Delete(NodeRef path) => throw new NotImplementedException();
        Task IGraphStorage.Connect(NodeRef sourcePath, NodeRef targetPath) => throw new NotImplementedException();
        Task IGraphStorage.Disconnect(NodeRef sourcePath, NodeRef targetPath) => throw new NotImplementedException();
        IAsyncEnumerable<NodeState> IGraphStorage.GetNeighbors(NodeRef path) => throw new NotImplementedException();
        IAsyncEnumerable<NodeState> IGraphStorage.GetCommonIntersection(NodeRef first, NodeRef second, params NodeRef[] other) => throw new NotImplementedException();
        NodeState IGraphStorage.Root => throw new NotImplementedException();
        Task IGraphStorage.Delete(NodeState node) => throw new NotImplementedException();

        async IAsyncEnumerable<NodeState> IGraphStorage.EnumerateNodesAsync([EnumeratorCancellation] CancellationToken cancellationToken) {
            foreach (var node in nodes) {
                yield return node;
                await Task.Yield();
            }
        }
    }

    private sealed class StreamingProbeNode : NodeState
    {
        private readonly Func<IReadOnlyDictionary<string, string>> _readAttributes;

        public StreamingProbeNode(string name, Func<IReadOnlyDictionary<string, string>> readAttributes)
        {
            LocalId = new(name);
            GlobalId = new(name);
            _readAttributes = readAttributes;
        }

        public override NodeLocalId LocalId { get; }

        public override InternalId GlobalId { get; }

        public override ICollection<EdgeState> Edges { get; } = Array.Empty<EdgeState>();

        public override ILazyCollection<NodeState> Nodes => throw new NotSupportedException();

        public override IDictionary<string, string> Attributes
        {
            get => new Dictionary<string, string>(_readAttributes(), StringComparer.OrdinalIgnoreCase);
            set => throw new NotSupportedException();
        }
    }
}
