using System.Text;
using Compare.Contracts;
using DiffPlex;
using DiffPlex.DiffBuilder;
using Microsoft.Extensions.Options;

namespace Compare.Gateway;

public sealed class DiffService(AgentClient agents, IOptions<GatewayOptions> options)
{
    public async Task<DiffResult> BuildAsync(
        AgentEndpoint a, AgentEndpoint b, string path, bool ignoreLineEndings, CancellationToken ct)
    {
        var o = options.Value;

        var fetchA = agents.GetFileAsync(a, path, o.MaxTextDiffBytes, ct);
        var fetchB = agents.GetFileAsync(b, path, o.MaxTextDiffBytes, ct);
        await Task.WhenAll(fetchA, fetchB);
        var (dataA, sizeA) = fetchA.Result;
        var (dataB, sizeB) = fetchB.Result;

        if (dataA is null || dataB is null)
        {
            if (sizeA is null || sizeB is null)
            {
                var where = sizeA is null && sizeB is null ? "either server"
                          : sizeA is null ? a.Name : b.Name;
                return new DiffResult("missing", $"The file does not exist on {where}.", sizeA, sizeB, null, null, null, null);
            }

            return new DiffResult("tooLarge",
                $"A file is larger than the {o.MaxTextDiffBytes:N0} byte limit for diffs. Compare the hashes instead.",
                sizeA, sizeB, null, null, null, null);
        }

        if (dataA.AsSpan().SequenceEqual(dataB))
            return new DiffResult("identical", "The files are byte-for-byte identical.", sizeA, sizeB, null, null, null, null);

        if (IsBinary(dataA) || IsBinary(dataB))
        {
            var first = FirstDifference(dataA, dataB);
            return new DiffResult("binary", null, sizeA, sizeB, null, first, Hex(dataA, first), Hex(dataB, first));
        }

        var textA = ReadText(dataA, ignoreLineEndings);
        var textB = ReadText(dataB, ignoreLineEndings);

        var model = new SideBySideDiffBuilder(new Differ()).BuildDiffModel(textA, textB);
        var all = model.OldText.Lines
            .Zip(model.NewText.Lines, (l, r) => new SideBySideLine(
                l.Type.ToString(), l.Position, l.Text, r.Type.ToString(), r.Position, r.Text))
            .ToList();

        string? message = null;
        if (all.All(l => l.LeftType == "Unchanged" && l.RightType == "Unchanged"))
            message = ignoreLineEndings
                ? "No differences apart from line endings."
                : "Line content is identical; the bytes differ (line endings or text encoding).";
        else if (all.Count > o.MaxDiffLines)
            message = $"Showing the first {o.MaxDiffLines:N0} of {all.Count:N0} lines.";

        return new DiffResult("text", message, sizeA, sizeB, all.Take(o.MaxDiffLines).ToList(), null, null, null);
    }

    static bool IsBinary(byte[] bytes)
    {
        // UTF-16 text legitimately contains NUL bytes, so trust its byte-order mark.
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            return false;
        return Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0;
    }

    static string ReadText(byte[] bytes, bool normalizeLineEndings)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        return normalizeLineEndings ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;
    }

    static long FirstDifference(byte[] a, byte[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
            if (a[i] != b[i]) return i;
        return n; // one file is a prefix of the other
    }

    /// <summary>Five 16-byte rows around the first difference.</summary>
    static List<string> Hex(byte[] data, long center)
    {
        var start = Math.Max(0, center / 16 - 2) * 16;
        var lines = new List<string>();

        for (var offset = start; offset < Math.Min(data.Length, start + 80); offset += 16)
        {
            var chunk = data.AsSpan((int)offset, (int)Math.Min(16, data.Length - offset)).ToArray();
            var hex = string.Join(' ', chunk.Select(x => x.ToString("X2")));
            var ascii = new string(chunk.Select(x => x is >= 32 and < 127 ? (char)x : '.').ToArray());
            lines.Add($"{offset:X8}  {hex,-47}  {ascii}");
        }

        return lines;
    }
}
