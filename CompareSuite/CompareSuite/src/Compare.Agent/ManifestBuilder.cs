using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Compare.Contracts;
using Microsoft.Extensions.Options;

namespace Compare.Agent;

public sealed class ManifestBuilder(PathResolver resolver, HashCache hashes, IOptions<AgentOptions> options)
{
    /// <summary>
    /// Resolves every watched path. Call this before streaming starts, so an unknown root or an escaping
    /// path becomes a 400 response instead of a failure halfway through a response that already began.
    /// </summary>
    public void Validate(IEnumerable<WatchedPath> watched)
    {
        foreach (var wp in watched) resolver.Resolve(wp.Path);
    }

    /// <summary>
    /// Yields manifest entries as they are produced: files are hashed in parallel (MaxConcurrentHashes) and
    /// handed over through a bounded channel, so memory stays flat however many files there are, and a slow
    /// reader slows the hashing down instead of piling entries up. Entries arrive in no particular order.
    /// </summary>
    public async IAsyncEnumerable<FileEntry> StreamAsync(
        IEnumerable<WatchedPath> watched, [EnumeratorCancellation] CancellationToken ct)
    {
        var paths = watched.ToList();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var channel = Channel.CreateBounded<FileEntry>(new BoundedChannelOptions(1000) { SingleReader = true });

        var producer = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(
                    EnumerateItems(paths, stop.Token),
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Max(1, options.Value.MaxConcurrentHashes),
                        CancellationToken = stop.Token
                    },
                    async (item, token) =>
                        await channel.Writer.WriteAsync(await ToEntryAsync(item.Key, item.Info, token), token));

                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);   // surfaces to the reader below
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var entry in channel.Reader.ReadAllAsync(ct))
                yield return entry;
        }
        finally
        {
            stop.Cancel();                        // finished, failed or client disconnected: stop walking and hashing
            try { await producer; } catch { }     // any error already reached the reader through the channel
        }
    }

    /// <summary>Lazily walks the watched paths, one folder at a time, without hashing anything.</summary>
    IEnumerable<(string Key, FileSystemInfo Info)> EnumerateItems(List<WatchedPath> watched, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var wp in watched)
        {
            var rp = resolver.Resolve(wp.Path);

            if (File.Exists(rp.FullPath))
            {
                var file = new FileInfo(rp.FullPath);
                if (TryKey(rp, file, allowLink: false, out var key) && seen.Add(key))
                    yield return (key, file);
            }
            else if (Directory.Exists(rp.FullPath))
            {
                var root = new DirectoryInfo(rp.FullPath);
                var enumeration = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint   // never follow links out of the root
                };

                // The watched folder itself is always included; ExcludeFolders only applies to folders inside it.
                if (wp.CheckFolders && TryKey(rp, root, allowLink: true, out var rootKey) && seen.Add(rootKey))
                    yield return (rootKey, root);

                // Walk one folder at a time so an excluded folder is skipped along with everything under it.
                var pending = new Stack<DirectoryInfo>();
                pending.Push(root);
                while (pending.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var current = pending.Pop();

                    foreach (var file in current.EnumerateFiles("*", enumeration))
                        if (NameMatches(file.Name, wp) && TryKey(rp, file, allowLink: false, out var key) && seen.Add(key))
                            yield return (key, file);

                    foreach (var dir in current.EnumerateDirectories("*", enumeration))
                    {
                        if (FolderExcluded(dir.Name, wp)) continue;

                        if (wp.CheckFolders && TryKey(rp, dir, allowLink: false, out var key) && seen.Add(key))
                            yield return (key, dir);

                        if (wp.Recursive) pending.Push(dir);
                    }
                }
            }
            // A watched path that doesn't exist on this server yields nothing,
            // which the gateway reports as "missing" wherever other servers have it.
        }
    }

    async Task<FileEntry> ToEntryAsync(string key, FileSystemInfo info, CancellationToken ct)
    {
        if (info is DirectoryInfo dir)
            return new FileEntry(key, 0, dir.LastWriteTimeUtc, "", null, IsFolder: true);

        var file = (FileInfo)info;
        try
        {
            return new FileEntry(key, file.Length, file.LastWriteTimeUtc, await hashes.GetHashAsync(file, ct));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileEntry(key, file.Length, file.LastWriteTimeUtc, "", ex.Message);
        }
    }

    static bool TryKey(ResolvedPath rp, FileSystemInfo info, bool allowLink, out string key)
    {
        key = "";
        if (!allowLink && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;

        var relative = Path.GetRelativePath(rp.Root, info.FullName).Replace('\\', '/');
        key = relative == "." ? rp.Alias : $"{rp.Alias}/{relative}";
        return true;
    }

    static bool FolderExcluded(string folderName, WatchedPath wp) =>
        wp.ExcludeFolders.Any(p => FileSystemName.MatchesSimpleExpression(p, folderName, ignoreCase: true));

    static bool NameMatches(string name, WatchedPath wp)
    {
        bool Any(string[] patterns) =>
            patterns.Any(p => FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true));

        if (wp.Include.Length > 0 && !Any(wp.Include)) return false;
        if (wp.Exclude.Length > 0 && Any(wp.Exclude)) return false;
        return true;
    }
}
