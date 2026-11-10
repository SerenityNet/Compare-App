using System.Net;
using Compare.Contracts;
using Microsoft.Extensions.Options;

namespace Compare.Gateway;

public sealed class AgentClient(IHttpClientFactory factory, IOptions<GatewayOptions> options)
{
    HttpClient Create(ServerInfo server)
    {
        var client = factory.CreateClient("agent");
        client.BaseAddress = new Uri(server.AgentUrl);
        client.Timeout = TimeSpan.FromSeconds(options.Value.AgentTimeoutSeconds);
        client.DefaultRequestHeaders.Add(ApiKeyAuth.Header, server.ApiKey);
        return client;
    }

    public async Task<ManifestResponse> GetManifestAsync(ServerInfo server, List<WatchedPath> paths, CancellationToken ct)
    {
        using var client = Create(server);
        using var response = await client.PostAsJsonAsync("manifest", new ManifestRequest { Paths = paths }, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ManifestResponse>(ct))!;
    }

    /// <summary>
    /// Downloads a file unless it exceeds maxBytes.
    /// (null, null) = not found on that server; (null, size) = too large; (bytes, size) = downloaded.
    /// </summary>
    public async Task<(byte[]? Data, long? Size)> GetFileAsync(ServerInfo server, string path, long maxBytes, CancellationToken ct)
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
