using Compare.Contracts;

namespace Compare.Web.Services;

public sealed class GatewayClient(HttpClient http)
{
    public async Task<List<ComparisonSet>> GetSetsAsync(CancellationToken ct) =>
        await http.GetFromJsonAsync<List<ComparisonSet>>("sets", ct) ?? [];

    public async Task<List<ServerSummary>> GetServersAsync(CancellationToken ct) =>
        await http.GetFromJsonAsync<List<ServerSummary>>("servers", ct) ?? [];

    public async Task<string> StartRunAsync(string setId, List<string> serverIds, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("runs", new StartRunRequest(setId, serverIds), ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StartRunResponse>(ct))!.RunId;
    }

    public Task<ComparisonRun?> GetRunAsync(string runId, CancellationToken ct) =>
        http.GetFromJsonAsync<ComparisonRun>($"runs/{Uri.EscapeDataString(runId)}", ct);

    public Task<DiffResult?> GetDiffAsync(
        string runId, string path, string serverA, string serverB, bool ignoreLineEndings, CancellationToken ct) =>
        http.GetFromJsonAsync<DiffResult>(
            $"diff?runId={Uri.EscapeDataString(runId)}&path={Uri.EscapeDataString(path)}" +
            $"&a={Uri.EscapeDataString(serverA)}&b={Uri.EscapeDataString(serverB)}&ignoreLineEndings={ignoreLineEndings}", ct);
}
