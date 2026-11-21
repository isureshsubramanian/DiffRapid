namespace DiffRapid.Mcp;

/// <summary>
/// The folders the server may read. Every tool path goes through <see cref="Resolve"/> before any
/// file-system call; a path outside every root is refused.
/// </summary>
/// <remarks>
/// Worst case per path: one <see cref="Path.GetFullPath(string, string)"/> and one link check per component
/// below the root, at most about 2,048 components (a 4,096-character path of 1-character names; an estimate of
/// the hostile maximum). Following links is limited to <see cref="MaxLinkHops"/> hops in all. At start-up each root
/// costs one name lookup per folder on its path, plus one listing of the parent folder where the name in the other
/// case exists.
/// </remarks>
public sealed class PathGuard
{
    /// <summary>The refusal text. It names neither the roots nor the requested path, because agents echo tool errors.</summary>
    public const string OutsideMessage = "Path is outside the folders this server may read; start the server with --root <folder>.";

    // The Linux MAXSYMLINKS limit (40) on links followed while resolving one path.
    private const int MaxLinkHops = 40;

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
    public IReadOnlyList<string> Roots => _roots.Select(root => root.Given.Path).ToList();


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
        var full = Path.GetFullPath(path, _roots[0].Given.Path);
        if (OperatingSystem.IsWindows() && IsNetworkOrDevice(full))
        {
            throw new ArgumentException("The path is not a local path; network and device paths are not supported.");
        }

        try
        {
            ResolveLinks(full);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Covers refusals, I/O errors and malformed link targets, whose messages can name paths outside the roots.
            throw new UnauthorizedAccessException(OutsideMessage);
        }

        return full;
    }

    /// <summary>
    /// Walks <paramref name="full"/> from its root down without following links. A link is followed one hop at a
    /// time: its target is checked as a string (local, inside a root) before anything at that target is touched.
    /// </summary>
    /// <remarks>
    /// On macOS and Linux the kernel applies a ".." in link text after following the links before it, so
    /// <c>a -&gt; d/up/../x</c> with <c>d/up -&gt; ..</c> leaves the root although the text normalises to
    /// <c>x</c>. Link text is therefore never normalised there: its components join the walk, and ".." steps up from
    /// the real folder reached so far, which has no links left on it. Windows resolves ".." in link targets as text,
    /// so there the target is normalised as the system does.
    /// </remarks>
    private void ResolveLinks(string full)
    {
        var (current, below) = Match(full);
        var pending = new Stack<string>();
        Push(pending, below);
        var hops = 0;
        while (pending.Count > 0)
        {
            var name = pending.Pop();
            if (name == ".")
            {
                continue;
            }

            if (name == "..")
            {
                current = Path.GetDirectoryName(current) ?? throw new UnauthorizedAccessException(OutsideMessage);
                if (!Inside(current))
                {
                    throw new UnauthorizedAccessException(OutsideMessage);
                }

                continue;
            }

            var folder = current;
            current = Path.Combine(current, name);
            var link = new FileInfo(current).LinkTarget;
            if (link is null)
            {
                continue;
            }

            if (++hops > MaxLinkHops || link.Contains('\0', StringComparison.Ordinal) || IsNetworkOrDevice(link))
            {
                throw new UnauthorizedAccessException(OutsideMessage);
            }

            if (OperatingSystem.IsWindows())
            {
                var target = Path.GetFullPath(link, folder);
                if (IsNetworkOrDevice(target))
                {
                    throw new UnauthorizedAccessException(OutsideMessage);
                }

                (current, below) = Match(target);
                Push(pending, below);
                continue;
            }

            var components = link.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (Path.IsPathRooted(link))
            {
                // An absolute target is matched to a root as text, which is exact only without "..".
                if (components.Contains(".."))
                {
                    throw new UnauthorizedAccessException(OutsideMessage);
                }

                (current, below) = Match(Path.GetFullPath(link));
                Push(pending, below);
            }
            else
            {
                current = folder;
                Push(pending, components);
            }
        }
    }

    // Pushes components so that the first is popped first, ahead of what was pending.
    private static void Push(Stack<string> pending, IReadOnlyList<string> components)
    {
        for (var index = components.Count - 1; index >= 0; index--)
        {
            pending.Push(components[index]);
        }
    }

    private bool Inside(string full) => _roots.Any(root => root.Given.Contains(full) || root.Real.Contains(full));

    /// <summary>
    /// The real path of the root that <paramref name="full"/> lies in, and the components below it. The walk starts
    /// from the real path, whose links are resolved, so a later ".." steps up where the kernel does even when the
    /// root was given through a link.
    /// </summary>
    private (string Base, string[] Below) Match(string full)
    {
        foreach (var root in _roots)
        {
            foreach (var spelling in new[] { root.Given, root.Real })
            {
                if (spelling.Contains(full))
                {
                    return (root.Real.Path, full[spelling.Path.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries));
                }
            }
        }

        throw new UnauthorizedAccessException(OutsideMessage);
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

        return new Root(Spelling.Probe(full), Spelling.Probe(RealPath(full)));
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

    /// <summary>
    /// The path with every link on it resolved, component by component from the volume root. Used only for the
    /// roots given at start-up, which are trusted; tool paths go through <see cref="ResolveLinks"/>.
    /// </summary>
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

    private sealed record Root(Spelling Given, Spelling Real);

    /// <summary>
    /// A root path, with whether each of its folders tells names apart by case. Case rules belong to each
    /// folder, not to the operating system: macOS and Windows volumes or folders can be case-sensitive, so a
    /// case variant of a root on such a volume names a different folder.
    /// </summary>
    private sealed class Spelling
    {
        private readonly string _volume;
        private readonly string[] _names;
        private readonly bool[] _ignoreCase;

        private Spelling(string path, string volume, string[] names, bool[] ignoreCase)
        {
            (Path, _volume, _names, _ignoreCase) = (path, volume, names, ignoreCase);
        }

        public string Path { get; }

        /// <summary>Probes each folder of a trusted root by looking up names, never by creating files.</summary>
        public static Spelling Probe(string path)
        {
            var volume = System.IO.Path.GetPathRoot(path) ?? string.Empty;
            var names = path[volume.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var ignoreCase = new bool[names.Length];
            var parent = volume;
            for (var index = 0; index < names.Length; index++)
            {
                ignoreCase[index] = IgnoresCase(parent, names[index]);
                parent = System.IO.Path.Combine(parent, names[index]);
            }

            return new Spelling(path, volume, names, ignoreCase);
        }

        /// <summary>Whether <paramref name="full"/> is this path or lies below it, compared per folder.</summary>
        public bool Contains(string full)
        {
            var separator = System.IO.Path.DirectorySeparatorChar;
            if (full.Length < Path.Length || (full.Length > Path.Length && !System.IO.Path.EndsInDirectorySeparator(Path) && full[Path.Length] != separator)
                || !full.StartsWith(_volume, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return false;
            }

            var offset = _volume.Length;
            for (var index = 0; index < _names.Length; index++)
            {
                if (index > 0 && full[offset++] != separator)
                {
                    return false;
                }

                var comparison = _ignoreCase[index] ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!full.AsSpan(offset, _names[index].Length).Equals(_names[index], comparison))
                {
                    return false;
                }

                offset += _names[index].Length;
            }

            return true;
        }

        // A folder ignores case when the name in the other case finds the same entry: it exists, and the folder
        // does not list it as a separate entry. Any error counts as case-sensitive, which only refuses more.
        private static bool IgnoresCase(string parent, string name)
        {
            var flipped = string.Concat(name.Select(letter => char.IsUpper(letter) ? char.ToLowerInvariant(letter) : char.ToUpperInvariant(letter)));
            if (flipped.Equals(name, StringComparison.Ordinal) || !flipped.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                return System.IO.Path.Exists(System.IO.Path.Combine(parent, flipped))
                    && !Directory.EnumerateFileSystemEntries(parent).Any(entry => System.IO.Path.GetFileName(entry).Equals(flipped, StringComparison.Ordinal));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
