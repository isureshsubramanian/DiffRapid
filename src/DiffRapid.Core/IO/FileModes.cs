using System;
using System.IO;
using System.Reflection;

namespace DiffRapid.Core.IO;

/// <summary>
/// Unix permission bits and symbolic links through the .NET 6/7 file APIs, which netstandard2.0 cannot call directly:
/// they are found once by reflection. On Windows and .NET Framework the mode calls do nothing and return null.
/// </summary>
internal static class FileModes
{
    /// <summary>The three execute bits (owner, group, others) of a Unix file mode.</summary>
    internal const int AnyExecute = 0b001_001_001;

    private static readonly bool IsWindows = Path.DirectorySeparatorChar == '\\';

    private static readonly MethodInfo? GetMode = IsWindows ? null : typeof(File).GetMethod("GetUnixFileMode", new[] { typeof(string) });

    private static readonly MethodInfo? SetMode = GetMode is null ? null
        : typeof(File).GetMethod("SetUnixFileMode", new[] { typeof(string), GetMode.ReturnType });

    private static readonly PropertyInfo? LinkTarget = typeof(FileSystemInfo).GetProperty("LinkTarget");

    private static readonly MethodInfo? ResolveLinkTarget = typeof(FileSystemInfo).GetMethod("ResolveLinkTarget", new[] { typeof(bool) });

    /// <summary>The Unix mode of <paramref name="path"/> (following links), or null where modes are not available.</summary>
    /// <exception cref="IOException">The mode could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The mode could not be read.</exception>
    internal static int? Get(string path) => GetMode is null ? null : Convert.ToInt32(Invoke(GetMode, null, path));

    /// <summary>Sets the Unix mode of <paramref name="path"/>; does nothing where modes are not available.</summary>
    /// <exception cref="IOException">The mode could not be set.</exception>
    /// <exception cref="UnauthorizedAccessException">The mode could not be set.</exception>
    internal static void Set(string path, int mode)
    {
        if (SetMode is not null)
        {
            Invoke(SetMode, null, path, Enum.ToObject(SetMode.GetParameters()[1].ParameterType, mode));
        }
    }

    /// <summary>
    /// The full path a symbolic link at <paramref name="path"/> finally points to, or null when the entry is not a link
    /// (or links cannot be read, as on .NET Framework).
    /// </summary>
    /// <exception cref="IOException">The link could not be read, or its chain is too long or circular.</exception>
    /// <exception cref="UnauthorizedAccessException">The link could not be read.</exception>
    internal static string? FinalLinkTarget(string path)
    {
        var info = new FileInfo(path);
        if (LinkTarget is null || ResolveLinkTarget is null || Invoke(LinkTarget.GetGetMethod()!, info) is null)
        {
            return null;
        }

        return (Invoke(ResolveLinkTarget, info, true) as FileSystemInfo)?.FullName;
    }

    // Reflection wraps the method's own exception; callers expect the documented IOException and similar.
    private static object? Invoke(MethodInfo method, object? target, params object[] arguments)
    {
        try
        {
            return method.Invoke(target, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
