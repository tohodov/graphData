using System.Text.Json;

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
    }
}
