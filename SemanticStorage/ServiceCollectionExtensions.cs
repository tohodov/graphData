using GraphData.Typed;
using Microsoft.Extensions.DependencyInjection;

namespace SemanticStorage;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSemanticRecordStorage(this IServiceCollection services, string rootPath)
    {
        services.AddSingleton<ITypedGraphStore>(_ => new SemanticRecordStore(rootPath));
        services.AddSingleton<ITypedGraph, TypedGraphService>();
        return services;
    }
}
