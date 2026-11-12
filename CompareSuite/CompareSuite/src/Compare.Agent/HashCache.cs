using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Compare.Agent;

/// <summary>SHA-256 per file, cached by (path, size, last-write time) so repeat runs only rehash changed files.</summary>
public sealed class HashCache
{
    readonly ConcurrentDictionary<string, (long Size, long Ticks, string Hash)> cache = new();

    public async Task<string> GetHashAsync(FileInfo file, CancellationToken ct)
    {
        var size = file.Length;
        var ticks = file.LastWriteTimeUtc.Ticks;

        if (cache.TryGetValue(file.FullName, out var hit) && hit.Size == size && hit.Ticks == ticks)
            return hit.Hash;

        await using var stream = new FileStream(file.FullName, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan | FileOptions.Asynchronous,
            BufferSize = 81920
        });

        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        cache[file.FullName] = (size, ticks, hash);
        return hash;
    }
}
