namespace Abstractions;

internal interface IAsyncCollection<T> : IAsyncEnumerable<T> {
    Task Add(T item);
    Task Remove(T item);
    Task Clear();
    Task<bool> Contains(T item);
}