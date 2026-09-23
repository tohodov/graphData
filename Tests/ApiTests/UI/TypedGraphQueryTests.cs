using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Jint;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Ui;

[RelevantTestClass]
public sealed class TypedGraphQueryTests
{
    [TestMethod]
    public void TextAndAttributes_FindKnowledgeByContentAndRejectInheritedProperties()
    {
        var engine = CreateEngine();
        engine.Execute("""
            const matches = searchTypedGraph(nodes, { where: { kind: "all", expressions: [
              { kind: "text", node: variable("n"), value: "PARIS france" },
              { kind: "attribute", node: variable("n"), key: "country", operator: "EQUALS", value: "France" }
            ] } });
            const inherited = searchTypedGraph(nodes, { where: { kind: "attribute", node: variable("n"), key: "constructor", operator: "exists" } });
            globalThis.result = matches.length === 1 && matches[0].node.name === "paris" && inherited.length === 0;
            """);
        Assert.IsTrue(engine.Evaluate("result").AsBoolean());
    }

    [TestMethod]
    public void JoinAndExists_CorrelateIncidencesAndHideExistentialBindings()
    {
        var engine = CreateEngine();
        engine.Execute("""
            const joined = searchTypedGraph(nodes, { return: ["city", "fact"], where: { kind: "all", expressions: [
              { kind: "attribute", node: variable("city"), key: "country", value: "France" },
              { kind: "connected", left: variable("city"), right: variable("fact") }
            ] } });
            const isolated = searchTypedGraph(nodes, { where: { kind: "not", expression: {
              kind: "exists", variables: ["other"], expression: { kind: "connected", left: variable("n"), right: variable("other") }
            } } });
            const inferred = searchTypedGraph(nodes, { where: { kind: "not", expression: {
              kind: "exists", expression: { kind: "connected", left: variable("n"), right: variable("other") }
            } } });
            globalThis.result = joined.length === 1 && joined[0].bindings.fact.name === "flight"
              && isolated.length === 1 && isolated[0].node.name === "alone"
              && inferred.length === 1 && inferred[0].node.name === "alone"
              && Object.keys(isolated[0].bindings).join() === "n";
            """);
        Assert.IsTrue(engine.Evaluate("result").AsBoolean());
    }

    [TestMethod]
    public void Connected_UsesExplicitIncidencesAndPathCountsRelationNodes()
    {
        var engine = CreateEngine();
        engine.Execute("""
            const neighbors = searchTypedGraph(nodes, { where: { kind: "connected", left: literal("paris"), right: variable("n") } });
            const cities = searchTypedGraph(nodes, { where: { kind: "path", left: literal("paris"), right: variable("n"), minDepth: 2, maxDepth: 2 } });
            globalThis.result = neighbors.length === 1 && neighbors[0].node.name === "flight"
              && cities.length === 1 && cities[0].node.name === "london";
            """);
        Assert.IsTrue(engine.Evaluate("result").AsBoolean());
    }

    [TestMethod]
    public void Path_ReturnsSelfOnlyWhenExplicitlyIncludedAtDepthZero()
    {
        var engine = CreateEngine();
        engine.Execute("""
            const self = (minDepth, includeSelf) => searchTypedGraph(nodes, { where: {
              kind: "all", expressions: [
                { kind: "same", left: variable("n"), right: literal("paris") },
                { kind: "path", left: variable("n"), right: literal("paris"), minDepth, maxDepth: 2, includeSelf }
              ]
            } }).length;
            globalThis.result = self(0, false) === 0 && self(1, true) === 0 && self(0, true) === 1;
            """);
        Assert.IsTrue(engine.Evaluate("result").AsBoolean());
    }

    [TestMethod]
    public void Limit_CountsDistinctReturnedBindingsAfterJoins()
    {
        var engine = CreateEngine();
        engine.Execute("""
            const matches = searchTypedGraph(nodes, { return: ["fact"], limit: 1, where: {
              kind: "connected", left: variable("fact"), right: variable("participant")
            } });
            const empty = searchTypedGraph([], { limit: 1 });
            globalThis.result = matches.length === 1 && Object.keys(matches[0].bindings).join() === "fact" && empty.length === 0;
            """);
        Assert.IsTrue(engine.Evaluate("result").AsBoolean());
    }

    [DataTestMethod]
    [DataRow("[]")]
    [DataRow("{ limit: 0 }")]
    [DataRow("{ where: { kind: 'missing' } }")]
    [DataRow("{ where: { kind: 'not' } }")]
    [DataRow("{ where: { kind: 'any', expressions: [] } }")]
    [DataRow("{ where: { kind: 'node', node: { kind: 'var', name: '' } } }")]
    [DataRow("{ where: { kind: 'attribute', node: variable('n'), key: 'country', operator: 'missing', value: 'France' } }")]
    [DataRow("{ where: { kind: 'path', left: variable('n'), right: literal('paris'), minDepth: 3, maxDepth: 1 } }")]
    [DataRow("{ where: { kind: 'all', expressions: [{ kind: 'same', left: literal('missing'), right: literal('paris') }, { kind: 'unknown' }] } }")]
    public void InvalidQueries_FailBeforeEnumerationIncludingEmptyGraph(string query)
    {
        var engine = CreateEngine();
        engine.Execute($$"""
            let errors = 0;
            for (const graph of [nodes, []]) {
              try { searchTypedGraph(graph, {{query}}); } catch { errors++; }
            }
            globalThis.result = errors === 2;
            """);
        Assert.IsTrue(engine.Evaluate("result").AsBoolean());
    }

    static Engine CreateEngine()
    {
        var engine = new Engine();
        foreach (var (path, name) in new[] {
            ("domain/GraphId", "GraphId"), ("domain/GraphEdge", "GraphEdge"), ("domain/GraphNode", "GraphNode"),
            ("infrastructure/TypedGraphApi", "TypedGraphApi"), ("domain/TypedGraphQuery", "searchTypedGraph")
        })
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "Editor/obj/ts/src", path + ".js"), Encoding.UTF8);
            source = Regex.Replace(source, @"^\s*import\s+[\s\S]*?;\s*", "", RegexOptions.Multiline);
            source = source.Replace("export class ", "class ").Replace("export function ", "function ");
            engine.Execute(source + $"\nglobalThis.{name} = {name};");
        }
        engine.Execute("""
            const variable = name => ({ kind: "var", name });
            const literal = name => ({ kind: "literal", name });
            const api = new TypedGraphApi({});
            const nodes = api.snapshots([
              { id: "paris", kind: "instance", typeIds: [], attributes: { label: "Paris", country: "France" }, members: {} },
              { id: "london", kind: "instance", typeIds: [], attributes: { label: "London", country: "UK" }, members: {} },
              { id: "flight", kind: "relation", typeIds: [], attributes: { label: "Flight" }, members: { departure: ["paris"], arrival: ["london"] } },
              { id: "alone", kind: "instance", typeIds: [], attributes: {}, members: {} }
            ]);
            """);
        return engine;
    }

    static string RepoRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../../.."));
}
