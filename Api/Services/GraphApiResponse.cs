namespace GraphData.Api.Services;

public enum GraphApiStatus
{
    Ok,
    Created,
    NoContent,
    BadRequest,
    NotFound,
    NotImplemented
}

public sealed record GraphApiResponse(GraphApiStatus Status, string? Error = null)
{
    public bool Succeeded =>
        Status is GraphApiStatus.Ok or GraphApiStatus.Created or GraphApiStatus.NoContent;

    public static GraphApiResponse NoContent() => new(GraphApiStatus.NoContent);

    public static GraphApiResponse BadRequest(string? error = null) =>
        new(GraphApiStatus.BadRequest, error);

    public static GraphApiResponse NotFound(string? error = null) =>
        new(GraphApiStatus.NotFound, error);
}

public sealed record GraphApiResponse<T>(
    GraphApiStatus Status,
    T? Value = default,
    string? Error = null)
{
    public bool Succeeded =>
        Status is GraphApiStatus.Ok or GraphApiStatus.Created or GraphApiStatus.NoContent;

    public static GraphApiResponse<T> Ok(T value) => new(GraphApiStatus.Ok, value);

    public static GraphApiResponse<T> Created(T value) => new(GraphApiStatus.Created, value);

    public static GraphApiResponse<T> BadRequest(string? error = null) =>
        new(GraphApiStatus.BadRequest, Error: error);

    public static GraphApiResponse<T> NotFound(string? error = null) =>
        new(GraphApiStatus.NotFound, Error: error);

    public static GraphApiResponse<T> NotImplemented(string? error = null) =>
        new(GraphApiStatus.NotImplemented, Error: error);
}
