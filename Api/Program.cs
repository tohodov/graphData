using GraphData.Api.Runtime;
using GraphData.Core.Extensions;
using Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddJsonOptions(static options => GraphJsonSerializerOptions.Configure(options.JsonSerializerOptions));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ICancellationTokenAccessor, HttpContextCancellationTokenAccessor>();
builder.Services.AddDomain();
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
app.MapGet("/clients/{*path}", () => Results.Redirect("/"))
    .ExcludeFromDescription();
app.Run();
