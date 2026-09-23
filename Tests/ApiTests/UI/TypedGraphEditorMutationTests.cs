using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Jint;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Ui;

[RelevantTestClass]
public sealed class TypedGraphEditorMutationTests {
    [TestMethod]
    public void DeleteSelection_RejectsAllRemovalsBeforeMutatingAnEarlierRelation() {
        var engine = CreateEngine();
        var result = engine.Evaluate(
            """
            (async () => {
              const editor = makeEditor();
              const optional = { id: "optional", typeIds: [], members: { Source: ["a"], Target: ["b"] } };
              const required = { id: "required", typeIds: ["Link"], members: { Source: ["a"], Target: ["b"] } };
              editor.elements = new Map([[optional.id, optional], [required.id, required]]);
              editor.types.set("Link", { members: [{ name: "Source", min: 1, max: 1 }, { name: "Target", min: 1, max: 1 }] });
              const writes = [];
              editor.api = { replaceMembers: async id => writes.push(id), delete: async id => writes.push(id) };
              await editor.deleteSelection([], [
                { relationGlobalId: "optional", memberName: "Source", node2InternalId: "a" },
                { relationGlobalId: "required", memberName: "Source", node2InternalId: "a" }
              ]);
              return writes.length === 0 && optional.members.Source[0] === "a"
                && required.members.Source[0] === "a" && editor.error.includes("required")
                && editor.error.includes("Source");
            })()
            """).UnwrapIfPromise(TimeSpan.FromSeconds(3));
        Assert.IsTrue(result.AsBoolean());
    }

    [TestMethod]
    public void DeleteSelection_RefreshesAfterARequestFailsFollowingAStoredRemoval() {
        var engine = CreateEngine();
        var result = engine.Evaluate(
            """
            (async () => {
              const editor = makeEditor();
              const first = { id: "first", typeIds: [], members: { Source: ["a"], Target: ["b"] } };
              const second = { id: "second", typeIds: [], members: { Source: ["a"], Target: ["b"] } };
              editor.elements = new Map([[first.id, first], [second.id, second]]);
              editor.api = { replaceMembers: async (id, members) => {
                if (id === "second") throw new Error("request failed");
                first.members = members;
              } };
              await editor.deleteSelection([], [
                { relationGlobalId: "first", memberName: "Source", node2InternalId: "a" },
                { relationGlobalId: "second", memberName: "Source", node2InternalId: "a" }
              ]);
              return first.members.Source.length === 0 && second.members.Source[0] === "a"
                && editor.refreshCount === 1 && editor.error === "request failed";
            })()
            """).UnwrapIfPromise(TimeSpan.FromSeconds(3));
        Assert.IsTrue(result.AsBoolean());
    }

    [TestMethod]
    public void FailedGroupsRefreshButFailedAttributeSaveKeepsTheDraft() {
        var engine = CreateEngine();
        var result = engine.Evaluate(
            """
            (async () => {
              const assigned = makeEditor();
              let assignments = 0;
              assigned.api = {
                get: async id => ({ id, kind: "instance", typeIds: [] }),
                replaceTypes: async () => { if (++assignments === 2) throw new Error("invalid type"); }
              };
              await assigned.assignType(["a", "b"], "Item");
              const created = makeEditor();
              let nodes = 0;
              created.api = {
                createInstance: async () => { nodes++; },
                createRelation: async () => { throw new Error("missing participant"); }
              };
              await created.createNode("new", { linkedNodeNames: ["missing"] });
              const edited = makeEditor();
              edited.api = { replaceAttributes: async () => { throw new Error("invalid value"); } };
              await edited.saveAttributes("a", { count: "draft" });
              return assignments === 2 && assigned.refreshCount === 1 && nodes === 1
                && created.refreshCount === 1 && edited.refreshCount === 0;
            })()
            """).UnwrapIfPromise(TimeSpan.FromSeconds(3));
        Assert.IsTrue(result.AsBoolean());
    }

    static Engine CreateEngine([CallerFilePath] string sourcePath = "") {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../../.."));
        var source = File.ReadAllText(Path.Combine(root, "Editor/obj/ts/src/ui/TypedGraphEditor.js"), Encoding.UTF8);
        source = Regex.Replace(source, @"^\s*import\s+[\s\S]*?;\s*", "", RegexOptions.Multiline);
        source = source.Replace("export class TypedGraphEditor", "class TypedGraphEditor", StringComparison.Ordinal);
        var engine = new Engine();
        engine.Execute(source);
        engine.Execute(
            """
            const structuredClone = value => JSON.parse(JSON.stringify(value));
            function makeEditor() {
              const editor = Object.create(TypedGraphEditor.prototype);
              editor.elements = new Map();
              editor.types = new Map();
              editor.refreshCount = 0;
              editor.error = "";
              editor.viewer = {
                setBusy() {}, setStatus() {},
                showServerError(error) { editor.error = error.message; },
                graph: { clearSelection() {}, loaded: new Map() }
              };
              editor.readElements = async () => [];
              editor.refresh = async () => { editor.refreshCount++; };
              editor.readInstanceAttributes = () => ({});
              editor.relationId = () => "new-relation";
              return editor;
            }
            """);
        return engine;
    }
}
