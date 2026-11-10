using Compare.Contracts;
using Compare.Gateway;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Model;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();
builder.Host.UseSystemd();

builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection("Gateway"));
builder.Services.AddHttpClient("agent");
builder.Services.AddSingleton<AgentClient>();
builder.Services.AddSingleton<RunStore>();
builder.Services.AddSingleton<RunQueue>();
builder.Services.AddSingleton<DiffService>();
builder.Services.AddHostedService<RunWorker>();

// YARP lives inside the gateway; its routes and clusters come from the "ReverseProxy" section of appsettings.json.
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

var gateway = app.Services.GetRequiredService<IOptions<GatewayOptions>>().Value;
app.UseApiKey(gateway.ApiKey, "/health");

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/sets", (IOptions<GatewayOptions> o) => o.Value.Sets);

// Agent URLs and keys never leave the gateway.
app.MapGet("/servers", (IOptions<GatewayOptions> o) =>
    o.Value.Servers.Select(s => new ServerSummary(s.Id, s.Name)));

app.MapPost("/runs", (StartRunRequest request, IOptions<GatewayOptions> o, RunStore store, RunQueue queue) =>
{
    var set = o.Value.Sets.FirstOrDefault(s => s.Id == request.SetId);
    if (set is null)
        return Results.BadRequest(new { error = "Unknown comparison set." });

    var ids = request.ServerIds.Distinct().ToList();
    if (ids.Count < 2)
        return Results.BadRequest(new { error = "Pick at least two servers." });
    if (ids.Any(id => !set.ServerIds.Contains(id) || o.Value.Servers.All(s => s.Id != id)))
        return Results.BadRequest(new { error = "A selected server is not part of this set." });

    var runId = Guid.NewGuid().ToString("N")[..12];
    store.Set(new ComparisonRun
    {
        Id = runId,
        SetId = set.Id,
        State = RunState.Queued,
        StartedAt = DateTimeOffset.UtcNow,
        Servers = ids.Select(id => new ServerRunState(id, ServerState.Pending, null, 0)).ToList()
    });
    queue.Writer.TryWrite(new RunJob(runId, set.Id, ids));

    return Results.Accepted($"/runs/{runId}", new StartRunResponse(runId));
});

app.MapGet("/runs/{id}", (string id, RunStore store) =>
    store.Get(id) is { } run ? Results.Ok(run) : Results.NotFound());

// Diffs are limited to files that appeared in a run, between servers that took part in it.
app.MapGet("/diff", async (
    string runId, string path, string a, string b, bool? ignoreLineEndings,
    IOptions<GatewayOptions> o, RunStore store, DiffService diffs, CancellationToken ct) =>
{
    var run = store.Get(runId);
    if (run is null) return Results.NotFound();

    var inRun = run.Servers.Select(s => s.ServerId).ToHashSet();
    if (!inRun.Contains(a) || !inRun.Contains(b) || run.Rows.All(r => r.Path != path || r.IsFolder))
        return Results.BadRequest(new { error = "That path or server is not part of this run." });

    var serverA = o.Value.Servers.First(s => s.Id == a);
    var serverB = o.Value.Servers.First(s => s.Id == b);

    try
    {
        return Results.Ok(await diffs.BuildAsync(serverA, serverB, path, ignoreLineEndings ?? true, ct));
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem($"An agent could not be reached: {ex.Message}", statusCode: 502);
    }
});

// Health of each agent, from YARP's active health checks.
app.MapGet("/health/agents", (IProxyStateLookup lookup, IOptions<GatewayOptions> o) =>
    o.Value.Servers.Select(s => new { s.Id, s.Name, Health = AgentHealth(lookup, s.Id) }));

// /agents/{serverId}/... is forwarded to that agent with its own API key added.
// The gateway's API-key check above runs first, so callers only ever need the gateway key.
app.MapReverseProxy();

app.Run();

static string AgentHealth(IProxyStateLookup lookup, string serverId) =>
    lookup.TryGetCluster($"agent-{serverId}", out var cluster) && cluster.DestinationsState.AllDestinations.Count > 0
        ? cluster.DestinationsState.AllDestinations[0].Health.Active.ToString()
        : "Unknown";
