using System.Reflection;
using GraphData.Api.Runtime;
using GraphData.Core.Extensions;
using GraphData.Core.Services;
using Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddJsonOptions(static options => GraphJsonSerializerOptions.Configure(options.JsonSerializerOptions));
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<SearchSelectorOpenApiDocumentTransformer>());
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ICancellationTokenAccessor, HttpContextCancellationTokenAccessor>();
builder.Services.AddDomain();
builder.Services.AddSymLinkStorage(builder.Configuration.GetSection("GraphStorage"));

var app = builder.Build();
var isOpenApiDocumentGeneration = Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

if (!isOpenApiDocumentGeneration)
{

}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();
app.MapGet("/clients/{*path}", () => Results.Redirect("/"))
    .ExcludeFromDescription();
app.Run();
