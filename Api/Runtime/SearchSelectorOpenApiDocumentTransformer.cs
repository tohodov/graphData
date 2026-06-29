using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace GraphData.Api.Runtime;

public sealed class SearchSelectorOpenApiDocumentTransformer : IOpenApiDocumentTransformer
{
    private const string SelectorSchemaName = "NodeSearchNodeSelectorRequest";
    private const string VariableSelectorSchemaName = "NodeSearchNodeSelectorRequestNodeVariableSearchSelectorRequest";
    private const string LiteralSelectorSchemaName = "NodeSearchNodeSelectorRequestNodeLiteralSearchSelectorRequest";

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        var schemas = document.Components?.Schemas;
        if (schemas is null)
            return Task.CompletedTask;

        schemas[VariableSelectorSchemaName] = CreateSelectorSchema("var");
        schemas[LiteralSelectorSchemaName] = CreateSelectorSchema("literal");

        if (schemas.TryGetValue(SelectorSchemaName, out var selectorSchemaValue))
        {
            var selectorSchema = selectorSchemaValue as OpenApiSchema ?? new OpenApiSchema();
            selectorSchema.Type = JsonSchemaType.Object;
            selectorSchema.Required = new HashSet<string> { "kind" };
            selectorSchema.AnyOf =
            [
                new OpenApiSchemaReference(VariableSelectorSchemaName, document),
                new OpenApiSchemaReference(LiteralSelectorSchemaName, document)
            ];
            selectorSchema.Discriminator = new OpenApiDiscriminator
            {
                PropertyName = "kind",
                Mapping = new Dictionary<string, OpenApiSchemaReference>
                {
                    ["var"] = new(VariableSelectorSchemaName, document),
                    ["literal"] = new(LiteralSelectorSchemaName, document)
                }
            };
            schemas[SelectorSchemaName] = selectorSchema;
        }

        return Task.CompletedTask;
    }

    private static OpenApiSchema CreateSelectorSchema(string kind) =>
        new()
        {
            Type = JsonSchemaType.Object,
            Required = new HashSet<string> { "kind", "name" },
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["kind"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Enum = [JsonValue.Create(kind)!]
                },
                ["name"] = new OpenApiSchema { Type = JsonSchemaType.String }
            }
        };
}
