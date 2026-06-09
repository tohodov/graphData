using GraphData.Api.Runtime;
using GraphData.Api.Services;
using GraphData.Core.Extensions;
using Microsoft.Extensions.FileProviders;
using SymLinkStorage;

var builder = WebApplication.CreateBuilder(args);
var webClientsRoot = ResolveWebClientsRoot(
    builder.Environment.ContentRootPath,
    builder.Configuration["WebClient:ClientsRootPath"]);
var webClientRoot = ResolveWebClientRoot(
    builder.Environment.ContentRootPath,
    builder.Configuration["WebClient:RootPath"],
    webClientsRoot);

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
if (webClientsRoot is not null) {
    var webClientsFiles = new PhysicalFileProvider(webClientsRoot);
    app.UseDefaultFiles(new DefaultFilesOptions {
        FileProvider = webClientsFiles,
        RequestPath = "/clients"
    });
    app.UseStaticFiles(new StaticFileOptions {
        FileProvider = webClientsFiles,
        RequestPath = "/clients"
    });
}

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

static string? ResolveWebClientsRoot(string contentRootPath, string? configuredRootPath) {
    var candidates = new List<string>();
    if (!string.IsNullOrWhiteSpace(configuredRootPath)) {
        candidates.Add(ToAbsolutePath(contentRootPath, configuredRootPath));
    }

    candidates.Add(Path.GetFullPath(Path.Combine(contentRootPath, "..", "Clients")));
    candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Clients")));

    return candidates.FirstOrDefault(Directory.Exists);
}

static string? ResolveWebClientRoot(string contentRootPath, string? configuredRootPath, string? webClientsRoot) {
    var candidates = new List<string>();
    if (!string.IsNullOrWhiteSpace(configuredRootPath)) {
        candidates.Add(ToAbsolutePath(contentRootPath, configuredRootPath));
    }

    if (webClientsRoot is not null) {
        candidates.Add(Path.Combine(webClientsRoot, "TilePyramid"));
        candidates.Add(Path.Combine(webClientsRoot, "WebGpuRaw"));
        candidates.Add(Path.Combine(webClientsRoot, "VanillaJs"));
    }

    candidates.Add(Path.GetFullPath(Path.Combine(contentRootPath, "..", "Clients", "TilePyramid")));
    candidates.Add(Path.GetFullPath(Path.Combine(contentRootPath, "..", "Clients", "WebGpuRaw")));
    candidates.Add(Path.GetFullPath(Path.Combine(contentRootPath, "..", "Clients", "VanillaJs")));
    candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Clients", "TilePyramid")));
    candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Clients", "WebGpuRaw")));
    candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Clients", "VanillaJs")));

    return candidates.FirstOrDefault(Directory.Exists);
}

static string ToAbsolutePath(string basePath, string path) {
    return Path.GetFullPath(Path.IsPathRooted(path)
        ? path
        : Path.Combine(basePath, path));
}
