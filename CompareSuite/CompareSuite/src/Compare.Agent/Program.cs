using Compare.Agent;
using Compare.Contracts;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();
builder.Host.UseSystemd();

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.AddSingleton<PathResolver>();
builder.Services.AddSingleton<HashCache>();
builder.Services.AddSingleton<ManifestBuilder>();

var app = builder.Build();

var agent = app.Services.GetRequiredService<IOptions<AgentOptions>>().Value;
app.UseApiKey(agent.ApiKey, "/health");

app.MapGet("/health", () => Results.Ok(new { status = "ok", host = Environment.MachineName }));

// Returns a hash manifest for the watched paths. Only hashes travel, never file contents.
app.MapPost("/manifest", async (ManifestRequest request, ManifestBuilder manifests, CancellationToken ct) =>
{
    try
    {
        var entries = await manifests.BuildAsync(request.Paths, ct);
        return Results.Ok(new ManifestResponse(Environment.MachineName, entries));
    }
    catch (PathNotAllowedException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Streams one file; the gateway calls this only when a user opens a diff.
app.MapGet("/file", (string path, PathResolver resolver) =>
{
    try
    {
        var info = new FileInfo(resolver.Resolve(path).FullPath);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return Results.NotFound();

        var stream = new FileStream(info.FullName, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan | FileOptions.Asynchronous
        });
        return Results.File(stream, "application/octet-stream", enableRangeProcessing: true);
    }
    catch (PathNotAllowedException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.Run();
