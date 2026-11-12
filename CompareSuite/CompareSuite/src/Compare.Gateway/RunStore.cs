using System.Collections.Concurrent;
using System.Threading.Channels;
using Compare.Contracts;

namespace Compare.Gateway;

public sealed record RunJob(string RunId, string SetId, List<string> ServerIds);

/// <summary>In-memory, keeps the latest 50 runs. Swap for a database if you need history across restarts.</summary>
public sealed class RunStore
{
    readonly ConcurrentDictionary<string, ComparisonRun> runs = new();
    readonly ConcurrentQueue<string> order = new();

    public void Set(ComparisonRun run)
    {
        if (runs.TryAdd(run.Id, run)) order.Enqueue(run.Id);
        else runs[run.Id] = run;

        while (order.Count > 50 && order.TryDequeue(out var old)) runs.TryRemove(old, out _);
    }

    public ComparisonRun? Get(string id) => runs.GetValueOrDefault(id);
}

public sealed class RunQueue
{
    readonly Channel<RunJob> channel = Channel.CreateUnbounded<RunJob>();
    public ChannelWriter<RunJob> Writer => channel.Writer;
    public ChannelReader<RunJob> Reader => channel.Reader;
}
