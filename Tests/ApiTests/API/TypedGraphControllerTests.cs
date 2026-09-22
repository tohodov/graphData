using System.Text.Json;
using GraphData.Api.Controllers;
using GraphData.Api.Models;
using GraphData.Api.Runtime;
using GraphData.Typed;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphData.Tests.Api;

[RelevantTestClass]
public sealed class TypedGraphControllerTests
{
    [DataTestMethod]
    [DataRow(TypedGraphError.NotFound, 404)]
    [DataRow(TypedGraphError.Conflict, 409)]
    [DataRow(TypedGraphError.Invalid, 400)]
    [DataRow(TypedGraphError.Unsupported, 501)]
    [DataRow(TypedGraphError.Corrupt, 500)]
    public async Task DomainErrors_MapToExplicitProblemStatus(TypedGraphError error, int expectedStatus)
    {
        var controller = Controller(new StubGraph { Error = error });

        var result = await controller.GetElementAsync("example");

        var response = result.Result as ObjectResult;
        Assert.IsNotNull(response);
        Assert.AreEqual(expectedStatus, response.StatusCode);
        var problem = response.Value as ProblemDetails;
        Assert.IsNotNull(problem);
        Assert.AreEqual(expectedStatus, problem.Status);
        Assert.AreEqual("test failure", problem.Detail);
    }

    [TestMethod]
    public async Task RelationCreation_PreservesNamedParticipantsAndOpaqueIdentity()
    {
        var graph = new StubGraph();
        var controller = Controller(graph);
        var request = new TypedRelationRequest
        {
            Id = "transfer/with space",
            TypeId = "Transfer",
            Members = new(StringComparer.Ordinal)
            {
                ["sender"] = ["A"],
                ["receiver"] = ["B"],
                ["items"] = ["C", "D"]
            },
            Attributes = new() { ["source"] = "record" }
        };

        var result = await controller.CreateRelationAsync(request);

        var created = result.Result as CreatedResult;
        Assert.IsNotNull(created);
        Assert.AreEqual("/api/typed-graph/elements?id=transfer%2Fwith%20space", created.Location);
        var relation = created.Value as TypedElementResponse;
        Assert.IsNotNull(relation);
        Assert.AreEqual(TypedElementKindDto.Relation, relation.Kind);
        Assert.AreEqual(request.Id, relation.Id);
        CollectionAssert.AreEqual(new[] { "C", "D" }, relation.Members["items"]);
        Assert.AreEqual("record", relation.Attributes["source"]);
        Assert.IsNotNull(graph.LastRelation);
        CollectionAssert.AreEqual(new[] { "Transfer" }, graph.LastRelation.TypeIds.ToArray());
        request.Members["items"][0] = "changed";
        Assert.AreEqual("C", relation.Members["items"][0]);
    }

    [TestMethod]
    public async Task UnknownDtoKind_IsRejectedWithoutCallingDomain()
    {
        var graph = new StubGraph();
        var result = await Controller(graph).CreateTypeAsync(new TypedTypeRequest
        {
            Id = "invalid",
            Kind = (TypedElementKindDto)900
        });

        Assert.AreEqual(400, (result.Result as ObjectResult)?.StatusCode);
        Assert.IsFalse(graph.TypeCreationCalled);
    }

    [TestMethod]
    public async Task RestrictDeletionConflict_UsesSameProblemContract()
    {
        var result = await Controller(new StubGraph { Error = TypedGraphError.Conflict }).DeleteElementAsync("in-use");

        var response = result.Result as ObjectResult;
        Assert.IsNotNull(response);
        Assert.AreEqual(409, response.StatusCode);
        Assert.IsInstanceOfType<ProblemDetails>(response.Value);
    }

    [DataTestMethod]
    [DataRow("""{"id":"first","id":"second","typeId":"NodeTypes/Transfer","members":{}}""")]
    [DataRow("""{"id":"transfer","typeId":"NodeTypes/Transfer","members":{"sender":["A"],"sender":["B"]}}""")]
    [DataRow("""{"id":"transfer","typeId":"NodeTypes/Transfer","members":{},"attributes":{"source":"first","source":"second"}}""")]
    public void JsonContract_RejectsDuplicatePropertiesAndRoles(string json)
    {
        Assert.ThrowsException<JsonException>(() => JsonSerializer.Deserialize<TypedRelationRequest>(json, GraphJsonSerializerOptions.Create()));
    }

    [DataTestMethod]
    [DataRow("""{"id":null,"typeId":"NodeTypes/Transfer","members":{}}""")]
    [DataRow("""{"id":"transfer","typeId":null,"members":{}}""")]
    [DataRow("""{"id":"transfer","typeId":"NodeTypes/Transfer","members":null}""")]
    [DataRow("""{"id":"transfer","typeId":"NodeTypes/Transfer","members":{},"attributes":null}""")]
    public void JsonContract_RejectsNullNonNullableProperties(string json)
    {
        Assert.ThrowsException<JsonException>(() => JsonSerializer.Deserialize<TypedRelationRequest>(json, GraphJsonSerializerOptions.Create()));
    }

    [DataTestMethod]
    [DataRow("type", """{"id":"NodeTypes/Invalid","kind":"Instance","requiredTypeIds":[null]}""")]
    [DataRow("type", """{"id":"NodeTypes/Invalid","kind":"Relation","members":[null]}""")]
    [DataRow("type", """{"id":"NodeTypes/Invalid","kind":"Instance","attributes":[null]}""")]
    [DataRow("instance", """{"id":"invalid","typeIds":[null]}""")]
    [DataRow("instance", """{"id":"invalid","attributes":{"label":null}}""")]
    [DataRow("relation", """{"id":"invalid","typeId":"NodeTypes/Transfer","members":{"sender":null}}""")]
    [DataRow("relation", """{"id":"invalid","typeId":"NodeTypes/Transfer","members":{"sender":[null]}}""")]
    [DataRow("relation", """{"id":"invalid","typeId":"NodeTypes/Transfer","members":{},"attributes":{"source":null}}""")]
    [DataRow("attributes", """{"attributes":{"label":null}}""")]
    [DataRow("type", "null")]
    [DataRow("instance", "null")]
    [DataRow("relation", "null")]
    [DataRow("attributes", "null")]
    public async Task JsonCollectionNulls_AreRejectedBeforeAnyDomainMutation(string action, string json)
    {
        var graph = new StubGraph();
        var controller = Controller(graph);
        var options = GraphJsonSerializerOptions.Create();
        var result = action switch
        {
            "type" => (await controller.CreateTypeAsync(JsonSerializer.Deserialize<TypedTypeRequest>(json, options)!)).Result,
            "instance" => (await controller.CreateInstanceAsync(JsonSerializer.Deserialize<TypedInstanceRequest>(json, options)!)).Result,
            "relation" => (await controller.CreateRelationAsync(JsonSerializer.Deserialize<TypedRelationRequest>(json, options)!)).Result,
            "attributes" => (await controller.ReplaceAttributesAsync("existing", JsonSerializer.Deserialize<TypedAttributesRequest>(json, options)!)).Result,
            _ => throw new AssertFailedException("Unknown test action.")
        };

        var problem = result as ObjectResult;
        Assert.IsNotNull(problem);
        Assert.AreEqual(400, problem.StatusCode);
        Assert.IsInstanceOfType<ProblemDetails>(problem.Value);
        Assert.AreEqual(0, graph.MutationCalls);
    }

    [TestMethod]
    public async Task JsonContract_AllowsExplicitlyNullableMemberDefinitionValues()
    {
        var graph = new StubGraph();
        var request = JsonSerializer.Deserialize<TypedTypeRequest>(
            """{"id":"NodeTypes/Group","kind":"Relation","members":[{"name":"participants","typeId":null,"min":0,"max":null}]}""",
            GraphJsonSerializerOptions.Create())!;

        var result = await Controller(graph).CreateTypeAsync(request);

        var created = result.Result as CreatedResult;
        Assert.IsNotNull(created);
        var response = created.Value as TypedTypeResponse;
        Assert.IsNotNull(response);
        Assert.IsNull(response.Members.Single().TypeId);
        Assert.IsNull(response.Members.Single().Max);
        Assert.AreEqual(1, graph.MutationCalls);
    }

    static TypedGraphController Controller(ITypedGraph graph) => new(graph)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };

    sealed class StubGraph : ITypedGraph
    {
        public TypedGraphError? Error { get; init; }
        public TypedElement? LastRelation { get; set; }
        public bool TypeCreationCalled { get; set; }
        public int MutationCalls { get; set; }

        public Task<TypedType> GetTypeAsync(string id) => throw new AssertFailedException("Unexpected call.");
        public Task<IReadOnlyList<TypedType>> GetTypesAsync() => throw new AssertFailedException("Unexpected call.");
        public Task<TypedElement> GetAsync(string id) => Task.FromException<TypedElement>(Failure());
        public Task<IReadOnlyList<TypedElement>> FindIncidentRelationsAsync(string participantId) => throw new AssertFailedException("Unexpected call.");
        public Task<TypedType> CreateTypeAsync(TypedType definition)
        {
            MutationCalls++;
            TypeCreationCalled = true;
            return Task.FromResult(definition);
        }

        public Task<TypedElement> CreateInstanceAsync(string id, IReadOnlyList<string> typeIds, IReadOnlyDictionary<string, string> attributes)
        {
            MutationCalls++;
            throw new AssertFailedException("Unexpected call.");
        }

        public Task<TypedElement> CreateRelationAsync(string id, string typeId, IReadOnlyDictionary<string, IReadOnlyList<string>> members, IReadOnlyDictionary<string, string> attributes)
        {
            MutationCalls++;
            LastRelation = new TypedElement(id, TypedElementKind.Relation, [typeId], attributes, members);
            return Task.FromResult(LastRelation);
        }

        public Task<TypedElement> ReplaceAttributesAsync(string id, IReadOnlyDictionary<string, string> attributes)
        {
            MutationCalls++;
            throw new AssertFailedException("Unexpected call.");
        }

        public Task DeleteAsync(string id)
        {
            MutationCalls++;
            return Task.FromException(Failure());
        }

        TypedGraphException Failure() => new(Error ?? TypedGraphError.Unsupported, "test failure");
    }
}
