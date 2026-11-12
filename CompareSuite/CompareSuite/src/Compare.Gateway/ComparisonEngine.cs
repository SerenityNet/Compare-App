using Compare.Contracts;

namespace Compare.Gateway;

public static class ComparisonEngine
{
    /// <summary>
    /// One row per file or folder path, one cell per requested server (in the order given), returned in
    /// tree order so a folder is immediately followed by everything inside it.
    /// Files are compared by hash; folders by presence. A path that is a file on one server and a
    /// folder on another is flagged as WrongType. Unreachable servers are ignored when classifying.
    /// </summary>
    public static List<ComparisonRow> Pivot(
        IReadOnlyList<string> serverIds, IReadOnlyDictionary<string, ManifestResponse> manifests)
    {
        var lookups = manifests.ToDictionary(
            m => m.Key, m => m.Value.Entries.ToDictionary(e => e.RelativePath, StringComparer.Ordinal));

        // '/' becomes a control character so "a/b" sorts before "a-b" and children stay contiguous.
        var allPaths = lookups.Values
            .SelectMany(files => files.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p.Replace('/', '\u0001'), StringComparer.OrdinalIgnoreCase);

        var rows = new List<ComparisonRow>();

        foreach (var path in allPaths)
        {
            var found = lookups.Values.Select(files => files.GetValueOrDefault(path)).OfType<FileEntry>().ToList();
            var isFolder = found.Count(e => e.IsFolder) >= found.Count(e => !e.IsFolder);

            var cells = serverIds.Select(id =>
            {
                if (!lookups.TryGetValue(id, out var files))
                    return new ServerCell(id, CellState.Unreachable, null, null);
                if (!files.TryGetValue(path, out var entry))
                    return new ServerCell(id, CellState.Missing, null, null);
                if (entry.IsFolder != isFolder)
                    return new ServerCell(id, CellState.WrongType, null, null);
                if (isFolder)
                    return new ServerCell(id, CellState.Present, null, null);
                return entry.Error is null
                    ? new ServerCell(id, CellState.Present, entry.Sha256, entry.Size)
                    : new ServerCell(id, CellState.Unreadable, null, entry.Size);
            }).ToList();

            var live = cells.Where(c => c.State != CellState.Unreachable).ToList();
            var groups = isFolder
                ? new List<IGrouping<string?, ServerCell>>()
                : live.Where(c => c.State == CellState.Present).GroupBy(c => c.Hash).OrderByDescending(g => g.Count()).ToList();

            var status =
                live.Any(c => c.State == CellState.Missing) ? RowStatus.Missing :
                live.Any(c => c.State is CellState.Unreadable or CellState.WrongType) || groups.Count > 1 ? RowStatus.Differs :
                RowStatus.Identical;

            var majority = groups.Count == 1 || (groups.Count > 1 && groups[0].Count() > groups[1].Count())
                ? groups[0].Key
                : null;

            rows.Add(new ComparisonRow(path, status, majority, cells, isFolder));
        }

        return rows;
    }
}
