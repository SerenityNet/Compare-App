using Compare.Contracts;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;

namespace Compare.Gateway;

public sealed record AgentEndpoint(string Id, string Name, Uri Address, string ApiKey);

/// <summary>
/// Looks up an agent's address and API key from the YARP configuration (ReverseProxy section), so that
/// section is the only place they are defined. The address comes from cluster "agent-{id}" and the key
/// from the X-Api-Key header transform on route "agent-{id}". Reads the live config, so edits to
/// appsettings.json take effect without a restart.
/// </summary>
public sealed class AgentDirectory(IProxyConfigProvider proxy, IOptions<GatewayOptions> options)
{
    public AgentEndpoint Resolve(string serverId)
    {
        var server = options.Value.Servers.FirstOrDefault(s => s.Id == serverId)
            ?? throw new InvalidOperationException($"Server '{serverId}' is not listed under Gateway:Servers.");

        var name = $"agent-{serverId}";
        var config = proxy.GetConfig();

        var address = config.Clusters.FirstOrDefault(c => c.ClusterId == name)
            ?.Destinations?.Values.FirstOrDefault()?.Address;
        if (string.IsNullOrWhiteSpace(address))
            throw new InvalidOperationException($"ReverseProxy has no cluster '{name}' with a destination address.");

        var key = config.Routes.FirstOrDefault(r => r.RouteId == name)?.Transforms?
            .Where(t => t.TryGetValue("RequestHeader", out var header)
                        && header.Equals(ApiKeyAuth.Header, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.GetValueOrDefault("Set"))
            .FirstOrDefault(v => !string.IsNullOrEmpty(v));
        if (key is null)
            throw new InvalidOperationException($"ReverseProxy route '{name}' does not set the {ApiKeyAuth.Header} header.");

        return new AgentEndpoint(server.Id, server.Name, new Uri(address), key);
    }
}
