using Microsoft.Extensions.Options;

namespace Compare.Agent;

public sealed class PathNotAllowedException(string message) : Exception(message);

public sealed record ResolvedPath(string Alias, string Root, string FullPath);

public sealed class PathResolver(IOptions<AgentOptions> options)
{
    static readonly StringComparison Cmp =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Turns "alias/sub/path" into a physical path, refusing anything outside the alias root.</summary>
    public ResolvedPath Resolve(string logicalPath)
    {
        var normalized = (logicalPath ?? "").Replace('\\', '/').Trim('/');
        var slash = normalized.IndexOf('/');
        var alias = slash < 0 ? normalized : normalized[..slash];
        var rest = slash < 0 ? "" : normalized[(slash + 1)..];

        if (!options.Value.Roots.TryGetValue(alias, out var configuredRoot))
            throw new PathNotAllowedException($"Unknown root '{alias}'.");

        var root = Path.GetFullPath(configuredRoot);
        var full = Path.GetFullPath(Path.Combine(root, rest));
        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;

        if (!full.Equals(root, Cmp) && !full.StartsWith(rootWithSeparator, Cmp))
            throw new PathNotAllowedException("Path escapes its root.");

        return new ResolvedPath(alias, root, full);
    }
}
