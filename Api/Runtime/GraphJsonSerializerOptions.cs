using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace GraphData.Api.Runtime;

public static class GraphJsonSerializerOptions
{
    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(options);
        return options;
    }

    public static void Configure(JsonSerializerOptions options)
    {
        options.AllowOutOfOrderMetadataProperties = true;
        options.Converters.Add(new NodeGlobalIdJsonConverter());

        if (options.TypeInfoResolver is null)
        {
            options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
        }
    }
}
