using System.Collections.Concurrent;
using Compare.Contracts;
using Microsoft.Extensions.Options;

namespace Compare.Gateway;

/// <summary>Processes queued runs one at a time; inside a run, all agents are queried in parallel.</summary>
public sealed class RunWorker(
    RunQueue queue, RunStore store, AgentClient agents, AgentDirectory directory,
    IOptions<GatewayOptions> options, ILogger<RunWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await foreach (var job in queue.Reader.ReadAllAsync(stop))
        {
            try
            {
                await RunAsync(job, stop);
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                log.LogError(ex, "Run {RunId} failed", job.RunId);
                var run = store.Get(job.RunId);
                if (run is not null)
                    store.Set(run with { State = RunState.Failed, CompletedAt = DateTimeOffset.UtcNow, Error = ex.Message });
            }
        }
    }

    async Task RunAsync(RunJob job, CancellationToken stop)
    {
        var set = options.Value.Sets.First(s => s.Id == job.SetId);

        var run = store.Get(job.RunId)! with { State = RunState.Running };
        store.Set(run);

        var gate = new object();
        var manifests = new ConcurrentDictionary<string, ManifestResponse>();

        await Task.WhenAll(job.ServerIds.Select(async serverId =>
        {
            ServerRunState state;
            try
            {
                // Resolving inside the try means a server with missing proxy config shows up as
                // unreachable with a clear reason instead of failing the whole run.
                var server = directory.Resolve(serverId);
                var manifest = await agents.GetManifestAsync(server, set.Paths, stop);
                manifests[serverId] = manifest;
                state = new ServerRunState(serverId, ServerState.Done, null, manifest.Entries.Count);
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                var reason = ex is OperationCanceledException ? "Timed out waiting for the agent" : ex.Message;
                log.LogWarning("Agent {Server} failed: {Reason}", serverId, reason);
                state = new ServerRunState(serverId, ServerState.Unreachable, reason, 0);
            }

            lock (gate)
            {
                run = run with { Servers = run.Servers.Select(s => s.ServerId == serverId ? state : s).ToList() };
                store.Set(run);
            }
        }));

        var rows = ComparisonEngine.Pivot(job.ServerIds, manifests);

        lock (gate)
        {
            run = run with { State = RunState.Completed, CompletedAt = DateTimeOffset.UtcNow, Rows = rows };
            store.Set(run);
        }
    }
}
