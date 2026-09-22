using System.Reflection;
using GraphData.Api.Runtime;
using GraphData.Core.Services;
using GraphData.Typed;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddJsonOptions(static options => GraphJsonSerializerOptions.Configure(options.JsonSerializerOptions));
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<SearchSelectorOpenApiDocumentTransformer>());
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ICancellationTokenAccessor, HttpContextCancellationTokenAccessor>();
builder.Services.AddConfiguredGraphStorage(builder.Configuration);

var app = builder.Build();
var isOpenApiDocumentGeneration = Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

if (!isOpenApiDocumentGeneration)
{
    if (app.Services.GetRequiredService<GraphStorageSelection>().Mode == GraphStorageMode.Legacy)
        await app.Services.GetRequiredService<CarrierGraph>().OpenAsync().ConfigureAwait(false);
    else
        _ = app.Services.GetRequiredService<ITypedGraph>();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseMiddleware<TypedGraphCompatibilityMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();
app.MapGet("/clients/{*path}", () => Results.Redirect("/"))
    .ExcludeFromDescription();
app.Run();
