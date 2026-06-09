using GraphData.Api.Runtime;
using GraphData.Api.Services;
using GraphData.Core.Extensions;
using Microsoft.Extensions.FileProviders;
using SymLinkStorage;

var builder = WebApplication.CreateBuilder(args);
var webClientRoot = ResolveWebClientRoot(
    builder.Environment.ContentRootPath,
    builder.Configuration["WebClient:RootPath"]);

builder.Services
    .AddControllers()
    .AddJsonOptions(static options => GraphJsonSerializerOptions.Configure(options.JsonSerializerOptions));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ICancellationTokenAccessor, HttpContextCancellationTokenAccessor>();
builder.Services.AddGraphCore();
builder.Services.AddSymLinkStorage(builder.Configuration.GetSection("GraphStorage"));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
if (webClientRoot is not null) {
    var webClientFiles = new PhysicalFileProvider(webClientRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webClientFiles });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = webClientFiles });
} else {
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.MapControllers();

app.Run();

static string? ResolveWebClientRoot(string contentRootPath, string? configuredRootPath) {
    var candidates = new List<string>();
    if (!string.IsNullOrWhiteSpace(configuredRootPath)) {
        candidates.Add(ToAbsolutePath(contentRootPath, configuredRootPath));
    }

    candidates.Add(Path.GetFullPath(Path.Combine(contentRootPath, "..", "Clients", "VanillaJs")));
    candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Clients", "VanillaJs")));

    return candidates.FirstOrDefault(Directory.Exists);
}

static string ToAbsolutePath(string basePath, string path) {
    return Path.GetFullPath(Path.IsPathRooted(path)
        ? path
        : Path.Combine(basePath, path));
}
