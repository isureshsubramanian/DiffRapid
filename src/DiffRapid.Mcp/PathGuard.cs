namespace DiffRapid.Mcp;

/// <summary>
/// The folders the server may read. Every tool path goes through <see cref="Resolve"/> before any
/// file-system call; a path outside every root is refused.
/// </summary>
/// <remarks>
/// Worst case per path: one <see cref="Path.GetFullPath(string, string)"/> and one link check per component
/// below the root, at most about 2,048 components (a 4,096-character path of 1-character names; an estimate of
/// the hostile maximum). Following links is limited to <see cref="MaxLinkHops"/>.
/// </remarks>
public sealed class PathGuard
{
    /// <summary>The refusal text. It names neither the roots nor the requested path, because agents echo tool errors.</summary>
    public const string OutsideMessage = "Path is outside the folders this server may read; start the server with --root <folder>.";

    // The Linux MAXSYMLINKS limit (40) on links followed while resolving one path.
    private const int MaxLinkHops = 40;

    // Windows and macOS default volumes ignore case; Linux file systems do not.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly IReadOnlyList<Root> _roots;

    /// <summary>Validates each root: local, existing folder, made absolute, with links resolved once.</summary>
    /// <exception cref="ArgumentException">A root is empty, a network or device path, or not an existing folder.</exception>
    public PathGuard(IEnumerable<string> roots)
    {
        _roots = roots.Select(CreateRoot).ToList().AsReadOnly();
        if (_roots.Count == 0)
        {
            throw new ArgumentException("At least one root folder is required.", nameof(roots));
        }
    }


    /// <summary>The roots as given, made absolute; the first one resolves relative tool paths.</summary>
    public IReadOnlyList<string> Roots => _roots.Select(root => root.Given).ToList();


    /// <summary>
    /// Returns the absolute form of <paramref name="path"/> when it lies inside a root, after resolving every link
    /// below the root and checking where it points.
    /// </summary>
    /// <exception cref="ArgumentException">The path is empty, contains NUL, or is a network or device path.</exception>
    /// <exception cref="UnauthorizedAccessException">The path, or a link on it, leads outside every root.</exception>
    public string Resolve(string path)
    {
        // String checks only, before any file-system call.
        CheckLocal(path);
        var full = Path.GetFullPath(path, _roots[0].Given);
        if (OperatingSystem.IsWindows() && IsNetworkOrDevice(full))
        {
            throw new ArgumentException("The path is not a local path; network and device paths are not supported.");
        }

        var root = _roots.Select(candidate => (Root: candidate, Base: Within(full, candidate.Given) ? candidate.Given : Within(full, candidate.Real) ? candidate.Real : null))
            .FirstOrDefault(match => match.Base is not null);
        if (root.Base is null)
        {
            throw new UnauthorizedAccessException(OutsideMessage);
        }

        // Walk the components below the root without following them; re-check every link's target.
        var current = root.Base;
        var hops = 0;
        foreach (var component in Path.GetRelativePath(root.Base, full).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".")
            {
                continue;
            }

            current = Path.Combine(current, component);
            var entry = new FileInfo(current);
            if (entry.LinkTarget is null)
            {
                continue;
            }

            if (++hops > MaxLinkHops)
            {
                throw new UnauthorizedAccessException(OutsideMessage);
            }

            var target = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? throw new UnauthorizedAccessException(OutsideMessage);
            current = RealPath(target);
            if (!_roots.Any(candidate => Within(current, candidate.Real)))
            {
                throw new UnauthorizedAccessException(OutsideMessage);
            }
        }

        return full;
    }

    private static Root CreateRoot(string path)
    {
        CheckLocal(path);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (OperatingSystem.IsWindows() && IsNetworkOrDevice(full))
        {
            throw new ArgumentException("A root folder must be a local path.");
        }

        if (!Directory.Exists(full))
        {
            throw new ArgumentException("A root folder does not exist.");
        }

        return new Root(full, RealPath(full));
    }

    private static void CheckLocal(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\0', StringComparison.Ordinal) || IsNetworkOrDevice(path))
        {
            throw new ArgumentException("The path is not a local path; network and device paths are not supported.");
        }
    }

    // \\server\share, //server/share, \\?\ and \\.\ all start with two slashes of either kind.
    private static bool IsNetworkOrDevice(string path) => path.Length >= 2 && path[0] is '\\' or '/' && path[1] is '\\' or '/';

    /// <summary>The path with every link on it resolved, component by component from the volume root.</summary>
    private static string RealPath(string fullPath)
    {
        var hops = 0;
        return RealPath(fullPath, ref hops);
    }

    private static string RealPath(string fullPath, ref int hops)
    {
        var volume = Path.GetPathRoot(fullPath) ?? string.Empty;
        var current = volume;
        foreach (var component in fullPath[volume.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            var entry = new FileInfo(current);
            if (entry.LinkTarget is null)
            {
                continue;
            }

            if (++hops > MaxLinkHops)
            {
                throw new UnauthorizedAccessException(OutsideMessage);
            }

            // The target's own folders may be links too (/tmp on macOS), so resolve it the same way.
            var target = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            current = target is null ? current : RealPath(target, ref hops);
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    private static bool Within(string path, string root)
    {
        if (path.Equals(root, PathComparison))
        {
            return true;
        }

        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    private sealed record Root(string Given, string Real);
}
