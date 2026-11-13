using System.Text.Json.Serialization;

namespace Compare.Contracts;

// ---- Configuration shapes (shared by Gateway config and Agent requests) ----

/// <summary>
/// A file or folder to watch. Path is "rootAlias" or "rootAlias/sub/path", where rootAlias is
/// defined in each agent's own config, so physical locations can differ per server.
/// Include/Exclude are simple wildcards (*, ?) matched against file names only.
/// ExcludeFolders are folder names or wildcards; a matching folder is skipped along with everything inside it.
/// </summary>
public sealed class WatchedPath
{
    public string Path { get; set; } = "";
    public bool Recursive { get; set; } = true;

    /// <summary>Also compare the folders themselves (including empty ones), not just the files inside.</summary>
    public bool CheckFolders { get; set; } = true;
    public string[] Include { get; set; } = [];
    public string[] Exclude { get; set; } = [];
    public string[] ExcludeFolders { get; set; } = [];
}

public sealed class ComparisonSet
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> ServerIds { get; set; } = [];
    public List<WatchedPath> Paths { get; set; } = [];
}

// ---- Agent API: POST /manifest streams a JSON array of FileEntry ----

public sealed class ManifestRequest
{
    public List<WatchedPath> Paths { get; set; } = [];
}

/// <summary>A file (with hash) or, when IsFolder is true, a folder (presence only).</summary>
public sealed record FileEntry(
    string RelativePath, long Size, DateTimeOffset Modified, string Sha256, string? Error = null, bool IsFolder = false);

/// <summary>Assembled by the gateway from the entries an agent streams back (Host is the server's display name).</summary>
public sealed record ManifestResponse(string Host, List<FileEntry> Entries);

// ---- Gateway API ----

public sealed record ServerSummary(string Id, string Name);

[JsonConverter(typeof(JsonStringEnumConverter))] public enum RunState { Queued, Running, Completed, Failed }
[JsonConverter(typeof(JsonStringEnumConverter))] public enum ServerState { Pending, Done, Unreachable }
[JsonConverter(typeof(JsonStringEnumConverter))] public enum RowStatus { Identical, Differs, Missing }
[JsonConverter(typeof(JsonStringEnumConverter))] public enum CellState { Present, Missing, Unreachable, Unreadable, WrongType }

public sealed record ServerRunState(string ServerId, ServerState State, string? Error, int FileCount);

public sealed record ServerCell(string ServerId, CellState State, string? Hash, long? Size);

/// <summary>MajorityHash is set only when one hash is held by strictly more servers than any other.</summary>
public sealed record ComparisonRow(string Path, RowStatus Status, string? MajorityHash, List<ServerCell> Cells, bool IsFolder = false);

public sealed record ComparisonRun
{
    public string Id { get; init; } = "";
    public string SetId { get; init; } = "";
    public RunState State { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? Error { get; init; }
    public List<ServerRunState> Servers { get; init; } = [];
    public List<ComparisonRow> Rows { get; init; } = [];
}

public sealed record StartRunRequest(string SetId, List<string> ServerIds);
public sealed record StartRunResponse(string RunId);

public sealed record SideBySideLine(string LeftType, int? LeftNo, string? LeftText, string RightType, int? RightNo, string? RightText);

/// <summary>Kind: text | binary | identical | missing | tooLarge</summary>
public sealed record DiffResult(
    string Kind, string? Message, long? SizeA, long? SizeB,
    List<SideBySideLine>? Lines, long? FirstDifferenceOffset, List<string>? HexA, List<string>? HexB);
