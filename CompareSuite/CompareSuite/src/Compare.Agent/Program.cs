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

// Paths and hex hashes compress very well. The manifest carries no secrets (the API key is a request
// header), so compression over HTTPS is safe here.
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);

var app = builder.Build();

app.UseResponseCompression();

var agent = app.Services.GetRequiredService<IOptions<AgentOptions>>().Value;
app.UseApiKey(agent.ApiKey, "/health");

app.MapGet("/health", () => Results.Ok(new { status = "ok", host = Environment.MachineName }));

// Streams the hash manifest as a JSON array of entries, written as each file is hashed, so neither the
// agent nor the gateway ever holds the whole manifest in one buffer. Only hashes travel, never contents.
app.MapPost("/manifest", (ManifestRequest request, ManifestBuilder manifests, CancellationToken ct) =>
{
    try
    {
        manifests.Validate(request.Paths);   // fail with 400 now, before any bytes are sent
    }
    catch (PathNotAllowedException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    return Results.Ok(manifests.StreamAsync(request.Paths, ct));
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
