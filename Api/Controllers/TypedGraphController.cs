using GraphData.Api.Models;
using GraphData.Typed;
using Microsoft.AspNetCore.Mvc;

namespace GraphData.Api.Controllers;

[ApiController]
[Route("api/typed-graph")]
public sealed class TypedGraphController(ITypedGraph graph) : ControllerBase
{
    [HttpGet("types")]
    public Task<ActionResult<TypedTypeResponse[]>> GetTypesAsync() =>
        ExecuteAsync(async () => (await graph.GetTypesAsync()).Select(ToResponse).ToArray());

    [HttpGet("types/item")]
    public Task<ActionResult<TypedTypeResponse>> GetTypeAsync([FromQuery] string id) =>
        ExecuteAsync(async () => ToResponse(await graph.GetTypeAsync(id)));

    [HttpPost("types")]
    public Task<ActionResult<TypedTypeResponse>> CreateTypeAsync([FromBody] TypedTypeRequest request) =>
        ExecuteAsync(async () => ToResponse(await graph.CreateTypeAsync(ToDefinition(request))),
            response => Created($"/api/typed-graph/types/item?id={Uri.EscapeDataString(response.Id)}", response));

    [HttpGet("elements")]
    public Task<ActionResult<TypedElementResponse>> GetElementAsync([FromQuery] string id) =>
        ExecuteAsync(async () => ToResponse(await graph.GetAsync(id)));

    [HttpGet("relations/incident")]
    public Task<ActionResult<TypedElementResponse[]>> GetIncidentRelationsAsync([FromQuery] string participantId) =>
        ExecuteAsync(async () => (await graph.FindIncidentRelationsAsync(participantId)).Select(ToResponse).ToArray());

    [HttpPost("instances")]
    public Task<ActionResult<TypedElementResponse>> CreateInstanceAsync([FromBody] TypedInstanceRequest request) =>
        ExecuteAsync(async () =>
        {
            ValidateInstanceRequest(request);
            return ToResponse(await graph.CreateInstanceAsync(request.Id, request.TypeIds, request.Attributes));
        },
            CreatedElement);

    [HttpPost("relations")]
    public Task<ActionResult<TypedElementResponse>> CreateRelationAsync([FromBody] TypedRelationRequest request) =>
        ExecuteAsync(async () =>
        {
            ValidateRelationRequest(request);
            return ToResponse(await graph.CreateRelationAsync(
                request.Id,
                request.TypeId,
                request.Members.ToDictionary(static member => member.Key, static member => (IReadOnlyList<string>)member.Value, StringComparer.Ordinal),
                request.Attributes));
        },
            CreatedElement);

    [HttpPut("elements/attributes")]
    public Task<ActionResult<TypedElementResponse>> ReplaceAttributesAsync([FromQuery] string id, [FromBody] TypedAttributesRequest request) =>
        ExecuteAsync(async () =>
        {
            RequireValue(id, "id");
            RequireValue(request, "request");
            ValidateAttributes(request.Attributes);
            return ToResponse(await graph.ReplaceAttributesAsync(id, request.Attributes));
        });

    [HttpDelete("elements")]
    public async Task<ActionResult<TypedOperationResponse>> DeleteElementAsync([FromQuery] string id)
    {
        try
        {
            RequireValue(id, "id");
            await graph.DeleteAsync(id);
            return NoContent();
        }
        catch (TypedGraphException error)
        {
            return ToProblem(error);
        }
    }

    async Task<ActionResult<T>> ExecuteAsync<T>(Func<Task<T>> operation, Func<T, ActionResult<T>>? success = null)
    {
        try
        {
            var result = await operation();
            return success is null ? Ok(result) : success(result);
        }
        catch (TypedGraphException error)
        {
            return ToProblem(error);
        }
    }

    ActionResult<TypedElementResponse> CreatedElement(TypedElementResponse response) =>
        Created($"/api/typed-graph/elements?id={Uri.EscapeDataString(response.Id)}", response);

    ObjectResult ToProblem(TypedGraphException error)
    {
        var (status, title) = error.Error switch
        {
            TypedGraphError.NotFound => (StatusCodes.Status404NotFound, "Typed graph element was not found."),
            TypedGraphError.Conflict => (StatusCodes.Status409Conflict, "Typed graph operation conflicts with existing data."),
            TypedGraphError.Invalid => (StatusCodes.Status400BadRequest, "Invalid typed graph operation."),
            TypedGraphError.Unsupported => (StatusCodes.Status501NotImplemented, "Typed graph operation is not supported."),
            _ => (StatusCodes.Status500InternalServerError, "Typed graph data is corrupt or ambiguous.")
        };
        return Problem(statusCode: status, title: title, detail: error.Message);
    }

    static TypedType ToDefinition(TypedTypeRequest request)
    {
        ValidateTypeRequest(request);
        if (!Enum.IsDefined(request.Kind) || request.Attributes.Any(static field => !Enum.IsDefined(field.Kind)))
            throw new TypedGraphException(TypedGraphError.Invalid, "Unknown element or scalar kind.");

        return new TypedType(
            request.Id,
            (TypedElementKind)request.Kind,
            request.IsAbstract,
            request.RequiredTypeIds,
            request.Members.Select(static member => new TypedMemberDefinition(member.Name, member.TypeId, member.Min, member.Max)).ToArray(),
            request.Attributes.Select(static field => new TypedAttributeDefinition(field.Name, (ScalarKind)field.Kind, field.Required)).ToArray());
    }

    // These checks validate an external JSON boundary. Nullable annotations do not cover
    // collection items, dictionary values, or a null root document during deserialization.
    static void ValidateTypeRequest(TypedTypeRequest request)
    {
        RequireValue(request, "request");
        RequireValue(request.Id, "id");
        ValidateIds(request.RequiredTypeIds, "requiredTypeIds");
        foreach (var member in RequireValue(request.Members, "members"))
            RequireValue(RequireValue(member, "members[]").Name, "members[].name");
        foreach (var field in RequireValue(request.Attributes, "attributes"))
            RequireValue(RequireValue(field, "attributes[]").Name, "attributes[].name");
    }

    static void ValidateInstanceRequest(TypedInstanceRequest request)
    {
        RequireValue(request, "request");
        RequireValue(request.Id, "id");
        ValidateIds(request.TypeIds, "typeIds");
        ValidateAttributes(request.Attributes);
    }

    static void ValidateRelationRequest(TypedRelationRequest request)
    {
        RequireValue(request, "request");
        RequireValue(request.Id, "id");
        RequireValue(request.TypeId, "typeId");
        foreach (var member in RequireValue(request.Members, "members"))
            ValidateIds(member.Value, $"members.{member.Key}");
        ValidateAttributes(request.Attributes);
    }

    static void ValidateAttributes(IReadOnlyDictionary<string, string> attributes)
    {
        foreach (var attribute in RequireValue(attributes, "attributes"))
            RequireValue(attribute.Value, $"attributes.{attribute.Key}");
    }

    static void ValidateIds(IReadOnlyList<string> ids, string property)
    {
        foreach (var id in RequireValue(ids, property))
            RequireValue(id, $"{property}[]");
    }

    static T RequireValue<T>(T? value, string property) where T : class =>
        value ?? throw new TypedGraphException(TypedGraphError.Invalid, $"'{property}' must not be null.");

    static TypedTypeResponse ToResponse(TypedType type) => new()
    {
        Id = type.Id,
        Kind = (TypedElementKindDto)type.Kind,
        IsAbstract = type.IsAbstract,
        RequiredTypeIds = type.RequiredTypeIds.ToArray(),
        Members = type.Members.Select(static member => new TypedMemberDefinitionDto
        {
            Name = member.Name,
            TypeId = member.TypeId,
            Min = member.Min,
            Max = member.Max
        }).ToArray(),
        Attributes = type.Attributes.Select(static field => new TypedAttributeDefinitionDto
        {
            Name = field.Name,
            Kind = (TypedScalarKindDto)field.Kind,
            Required = field.Required
        }).ToArray()
    };

    static TypedElementResponse ToResponse(TypedElement element) => new()
    {
        Id = element.Id,
        Kind = (TypedElementKindDto)element.Kind,
        TypeIds = element.TypeIds.ToArray(),
        Attributes = new Dictionary<string, string>(element.Attributes, StringComparer.Ordinal),
        Members = element.Members.ToDictionary(static member => member.Key, static member => member.Value.ToArray(), StringComparer.Ordinal)
    };
}
