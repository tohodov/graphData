using Abstractions;
using GraphData.Core.Services;
using GraphData.Typed;
using Microsoft.Extensions.DependencyInjection;

namespace GraphData.Core.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>Adds the typed contract over the existing carrier grammar. Open CarrierGraph before resolving it.</summary>
    public static IServiceCollection AddCarrierTypedGraph(this IServiceCollection services) {
        services.AddSingleton<ITypedGraphStore, CarrierTypedGraphStore>();
        services.AddSingleton<ITypedGraph, TypedGraphService>();
        return services;
    }

    public static IServiceCollection AddDomain(
        this IServiceCollection services,
        Action<GraphRuntimeTypeOptions>? configureRuntimeTypes = null) {
        var runtimeTypeOptions = new GraphRuntimeTypeOptions();
        configureRuntimeTypes?.Invoke(runtimeTypeOptions);
        services.AddSingleton(GraphSchemaRegistry.Create(runtimeTypeOptions));
        services.AddSingleton(static provider =>
            new CarrierGraph(
                provider.GetRequiredService<IGraphStorage>(),
                provider.GetRequiredService<GraphSchemaRegistry>()));
        services.AddScoped<CarrierGraphSearchService>(static provider =>
            new CarrierGraphSearchService(provider.GetRequiredService<IGraphStorage>()));
        services.AddScoped<CarrierGraphService>(static provider =>
            new CarrierGraphService(
                provider.GetRequiredService<CarrierGraph>(),
                provider.GetRequiredService<CarrierGraphSearchService>()
            ));
        services.AddScoped<CarrierGraphBackupService>();
        return services;
    }
}
