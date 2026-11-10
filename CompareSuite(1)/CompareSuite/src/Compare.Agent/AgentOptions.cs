namespace Compare.Agent;

public sealed class AgentOptions
{
    public string ApiKey { get; set; } = "";
    public int MaxConcurrentHashes { get; set; } = 4;

    /// <summary>Alias -> physical folder. Clients can only ever name an alias, never a raw path.</summary>
    public Dictionary<string, string> Roots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
