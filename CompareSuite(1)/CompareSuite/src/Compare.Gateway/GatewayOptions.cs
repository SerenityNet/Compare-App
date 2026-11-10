using Compare.Contracts;

namespace Compare.Gateway;

public sealed class ServerInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string AgentUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
}

public sealed class GatewayOptions
{
    public string ApiKey { get; set; } = "";
    public int AgentTimeoutSeconds { get; set; } = 120;
    public int MaxTextDiffBytes { get; set; } = 5_000_000;
    public int MaxDiffLines { get; set; } = 2000;
    public List<ServerInfo> Servers { get; set; } = [];
    public List<ComparisonSet> Sets { get; set; } = [];
}
