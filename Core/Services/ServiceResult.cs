namespace GraphData.Core.Services;

public enum ServiceResultStatus {
    Ok,
    BadRequest,
    NotFound,
    Conflict,
    InternalServerError,//TODO rethink exception handling
}

public sealed record ServiceResult(ServiceResultStatus Status, string? Error = null) {
    public static ServiceResult Ok() => new(ServiceResultStatus.Ok);
    public static ServiceResult BadRequest(string? error = null) => new(ServiceResultStatus.BadRequest, error);
    public static ServiceResult NotFound(string? error = null) => new(ServiceResultStatus.NotFound, error);
    public static ServiceResult Conflict(string? error = null) => new(ServiceResultStatus.Conflict, error);
    public static ServiceResult InternalServerError(string? error = null) => new(ServiceResultStatus.InternalServerError, error);
    public static ServiceResult From<T>(ServiceResult<T> result) => new(result.Status, result.Error);
}

public sealed record ServiceResult<T>(ServiceResultStatus Status, T? Value = default, string? Error = null) {
    public static ServiceResult<T> Ok(T value) => new(ServiceResultStatus.Ok, value);
    public static ServiceResult<T> BadRequest(string? error = null) => new(ServiceResultStatus.BadRequest, Error: error);
    public static ServiceResult<T> NotFound(string? error = null) => new(ServiceResultStatus.NotFound, Error: error);
    public static ServiceResult<T> Conflict(string? error = null) => new(ServiceResultStatus.Conflict, Error: error);
    public static ServiceResult<T> InternalServerError(string? error = null) => new(ServiceResultStatus.InternalServerError, Error: error);
    public static ServiceResult<T> From<TOther>(ServiceResult<TOther> result) => new(result.Status, Error: result.Error);
}
