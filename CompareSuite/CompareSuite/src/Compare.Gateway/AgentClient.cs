using System.Net;
using System.Runtime.CompilerServices;
using Compare.Contracts;
using Microsoft.Extensions.Options;

namespace Compare.Gateway;

public sealed class AgentClient(IHttpClientFactory factory, IOptions<GatewayOptions> options)
{
    HttpClient Create(AgentEndpoint server)
    {
        var client = factory.CreateClient("agent");
        client.BaseAddress = server.Address;
        client.Timeout = TimeSpan.FromSeconds(options.Value.AgentTimeoutSeconds);
        client.DefaultRequestHeaders.Add(ApiKeyAuth.Header, server.ApiKey);
        return client;
    }

    /// <summary>Collects a streamed manifest into one list for comparison.</summary>
    public async Task<ManifestResponse> GetManifestAsync(AgentEndpoint server, List<WatchedPath> paths, CancellationToken ct)
    {
        var entries = new List<FileEntry>();
        await foreach (var entry in StreamManifestAsync(server, paths, ct))
            entries.Add(entry);

        return new ManifestResponse(server.Name, entries);
    }

    /// <summary>
    /// Reads the agent's manifest entry by entry as it arrives, so callers can process it in batches
    /// (for example, bulk-insert every few thousand entries) instead of buffering all of it.
    /// AgentTimeoutSeconds is an idle timeout here: it restarts every time an entry arrives.
    /// </summary>
    public async IAsyncEnumerable<FileEntry> StreamManifestAsync(
        AgentEndpoint server, List<WatchedPath> paths, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var idleLimit = TimeSpan.FromSeconds(options.Value.AgentTimeoutSeconds);

        using var client = Create(server);
        using var request = new HttpRequestMessage(HttpMethod.Post, "manifest")
        {
            Content = JsonContent.Create(new ManifestRequest { Paths = paths })
        };
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(idleLimit);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token);
        response.EnsureSuccessStatusCode();

        await foreach (var entry in response.Content.ReadFromJsonAsAsyncEnumerable<FileEntry>(idle.Token))
        {
            idle.CancelAfter(idleLimit);   // progress was made, so restart the idle clock
            if (entry is not null) yield return entry;
        }
    }

    /// <summary>
    /// Downloads a file unless it exceeds maxBytes.
    /// (null, null) = not found on that server; (null, size) = too large; (bytes, size) = downloaded.
    /// </summary>
    public async Task<(byte[]? Data, long? Size)> GetFileAsync(AgentEndpoint server, string path, long maxBytes, CancellationToken ct)
    {
        using var client = Create(server);
        using var response = await client.GetAsync(
            $"file?path={Uri.EscapeDataString(path)}", HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return (null, null);
        response.EnsureSuccessStatusCode();

        var length = response.Content.Headers.ContentLength;
        if (length > maxBytes) return (null, length);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return (buffer.ToArray(), buffer.Length);
    }
}
