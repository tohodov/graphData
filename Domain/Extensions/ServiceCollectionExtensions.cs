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
        services.AddScoped<GraphSearchService>(static provider =>
            new GraphSearchService(provider.GetRequiredService<IGraphStorage>()));
        services.AddScoped<GraphProvider>(static provider =>
            new GraphProvider(
                provider.GetRequiredService<IGraphStorage>(),
                provider.GetRequiredService<GraphSchemaRegistry>()));
        services.AddScoped<GraphService>(static provider =>
            new GraphService(
                provider.GetRequiredService<GraphProvider>(),
                provider.GetRequiredService<GraphSearchService>()
            ));
        return services;
    }
}
