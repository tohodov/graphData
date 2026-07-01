using Abstractions;
using GraphData.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GraphData.Core.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDomain(
        this IServiceCollection services,
        Action<GraphRuntimeTypeOptions>? configureRuntimeTypes = null) {
        var runtimeTypeOptions = new GraphRuntimeTypeOptions();
        configureRuntimeTypes?.Invoke(runtimeTypeOptions);
        services.AddSingleton(GraphSchemaRegistry.Create(runtimeTypeOptions));
        services.AddSingleton(static provider =>
            new Graph(
                provider.GetRequiredService<IGraphStorage>(),
                provider.GetRequiredService<GraphSchemaRegistry>()));
        services.AddScoped<GraphSearchService>(static provider =>
            new GraphSearchService(provider.GetRequiredService<IGraphStorage>()));
        services.AddScoped<GraphService>(static provider =>
            new GraphService(
                provider.GetRequiredService<Graph>(),
                provider.GetRequiredService<GraphSearchService>()
            ));
        services.AddScoped<GraphBackupService>();
        return services;
    }
}
