using GraphData.Api.Runtime;
using GraphData.Api.Services;
using GraphData.Core.Extensions;
using SymLinkStorage;

var builder = WebApplication.CreateBuilder(args);

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
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();

app.Run();
