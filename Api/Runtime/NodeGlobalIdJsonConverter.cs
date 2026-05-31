using System.Text.Json;
using System.Text.Json.Serialization;
using GraphData.Core.Models;

namespace GraphData.Api.Runtime;

public sealed class NodeGlobalIdJsonConverter : JsonConverter<NodeGlobalId>
{
    public override NodeGlobalId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("GlobalId must be a JSON array of id segments.");

        var segments = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return new NodeGlobalId(segments);

            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("GlobalId segment must be a string.");

            var segment = reader.GetString();
            if (segment is null)
                throw new JsonException("GlobalId segment cannot be null.");

            segments.Add(segment);
        }

        throw new JsonException("GlobalId array is incomplete.");
    }

    public override void Write(Utf8JsonWriter writer, NodeGlobalId value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var segment in value)
            writer.WriteStringValue(segment.ToString());
        writer.WriteEndArray();
    }
}
