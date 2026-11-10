using System.IO.Enumeration;
using Compare.Contracts;
using Microsoft.Extensions.Options;

namespace Compare.Agent;

public sealed class ManifestBuilder(PathResolver resolver, HashCache hashes, IOptions<AgentOptions> options)
{
    public async Task<List<FileEntry>> BuildAsync(IEnumerable<WatchedPath> watched, CancellationToken ct)
    {
        // logical path ("alias/sub/file.txt") -> file or folder
        var items = new Dictionary<string, FileSystemInfo>(StringComparer.Ordinal);

        foreach (var wp in watched)
        {
            var rp = resolver.Resolve(wp.Path);

            if (File.Exists(rp.FullPath))
            {
                Add(items, rp, new FileInfo(rp.FullPath), allowLink: false);
            }
            else if (Directory.Exists(rp.FullPath))
            {
                var root = new DirectoryInfo(rp.FullPath);
                var enumeration = new EnumerationOptions
                {
                    RecurseSubdirectories = wp.Recursive,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint   // never follow links out of the root
                };

                if (wp.CheckFolders)
                {
                    Add(items, rp, root, allowLink: true);
                    foreach (var dir in root.EnumerateDirectories("*", enumeration))
                        Add(items, rp, dir, allowLink: false);
                }

                foreach (var file in root.EnumerateFiles("*", enumeration))
                    if (NameMatches(file.Name, wp)) Add(items, rp, file, allowLink: false);
            }
            // A watched path that doesn't exist on this server yields nothing,
            // which the gateway reports as "missing" wherever other servers have it.
        }

        var gate = new SemaphoreSlim(Math.Max(1, options.Value.MaxConcurrentHashes));

        var entries = await Task.WhenAll(items.Select(async kv =>
        {
            if (kv.Value is DirectoryInfo dir)
                return new FileEntry(kv.Key, 0, dir.LastWriteTimeUtc, "", null, IsFolder: true);

            var file = (FileInfo)kv.Value;
            await gate.WaitAsync(ct);
            try
            {
                return new FileEntry(kv.Key, file.Length, file.LastWriteTimeUtc, await hashes.GetHashAsync(file, ct));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new FileEntry(kv.Key, file.Length, file.LastWriteTimeUtc, "", ex.Message);
            }
            finally { gate.Release(); }
        }));

        return entries.OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void Add(Dictionary<string, FileSystemInfo> items, ResolvedPath rp, FileSystemInfo info, bool allowLink)
    {
        if (!allowLink && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;

        var relative = Path.GetRelativePath(rp.Root, info.FullName).Replace('\\', '/');
        var key = relative == "." ? rp.Alias : $"{rp.Alias}/{relative}";
        items.TryAdd(key, info);
    }

    static bool NameMatches(string name, WatchedPath wp)
    {
        bool Any(string[] patterns) =>
            patterns.Any(p => FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true));

        if (wp.Include.Length > 0 && !Any(wp.Include)) return false;
        if (wp.Exclude.Length > 0 && Any(wp.Exclude)) return false;
        return true;
    }
}
