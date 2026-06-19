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
        services.AddSingleton(GraphRuntimeTypeCatalog.Create(runtimeTypeOptions));
        services.AddScoped<GraphSearchService>(static provider =>
            new GraphSearchService(provider.GetRequiredService<IGraphStorage>()));
        services.AddScoped<GraphService>(static provider =>
            new GraphService(
                provider.GetRequiredService<IGraphStorage>(),
                provider.GetRequiredService<GraphSearchService>(),
                provider.GetRequiredService<ICancellationTokenAccessor>(),
                provider.GetRequiredService<GraphRuntimeTypeCatalog>()
            ));
        services.AddTransient<GraphStorageInitializer>(static provider =>
            new GraphStorageInitializer(
                provider.GetRequiredService<IGraphStorage>(),
                provider.GetRequiredService<GraphRuntimeTypeCatalog>()));
        return services;
    }
}
