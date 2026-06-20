namespace GraphData.Api.Models;

public sealed class AssignNodeTypeRequest
{
    public required string[] InternalId { get; init; }

    public required string[] TypeGlobalId { get; init; }
}
