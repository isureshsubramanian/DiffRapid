using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using DiffRapid.Core.Plugins;

namespace DiffRapid.Core.Assemblies;

/// <summary>A package dependency from the nuspec.</summary>
/// <param name="Id">The package id.</param>
/// <param name="VersionRange">The version range as written, or null when none is given.</param>
public sealed record NuGetDependency(string Id, string? VersionRange);

/// <summary>The dependencies declared for one target framework.</summary>
/// <param name="TargetFramework">The target framework as written in the nuspec; empty when the group applies to all.</param>
/// <param name="Dependencies">The dependencies, sorted by id.</param>
public sealed record NuGetDependencyGroup(string TargetFramework, IReadOnlyList<NuGetDependency> Dependencies);

/// <summary>An assembly in the package's lib folder.</summary>
/// <param name="TargetFramework">The folder name under lib, e.g. "net8.0".</param>
/// <param name="Path">The path inside the package, e.g. "lib/net8.0/Example.dll".</param>
/// <param name="Api">The public API of the assembly.</param>
public sealed record NuGetLibrary(string TargetFramework, string Path, ApiSurface Api);

/// <summary>The metadata and library APIs of a .nupkg file.</summary>
/// <param name="Id">The package id.</param>
/// <param name="Version">The package version.</param>
/// <param name="DependencyGroups">The dependency groups, sorted by target framework.</param>
/// <param name="Libraries">The lib/&lt;tfm&gt;/*.dll assemblies, sorted by path.</param>
public sealed record NuGetPackage(string Id, string Version, IReadOnlyList<NuGetDependencyGroup> DependencyGroups, IReadOnlyList<NuGetLibrary> Libraries)
{
    /// <summary>The package metadata as named values, in a fixed order, for display and comparison.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ToProperties()
    {
        var properties = new List<KeyValuePair<string, string>>
        {
            new("Package id", Id),
            new("Package version", Version),
        };
        foreach (var group in DependencyGroups)
        {
            var framework = group.TargetFramework.Length == 0 ? "any" : group.TargetFramework;
            if (group.Dependencies.Count == 0)
            {
                properties.Add(new("Dependencies " + framework, "(none)"));
            }

            foreach (var dependency in group.Dependencies)
            {
                properties.Add(new($"Dependency {framework} {dependency.Id}", dependency.VersionRange ?? "(any)"));
            }
        }

        return properties;
    }
}

/// <summary>Reads a NuGet package (a zip file) without extracting it to disk.</summary>
public static class NuGetPackageReader
{
    /// <summary>
    /// The largest library read (an estimate: well above the biggest managed assemblies shipped on nuget.org,
    /// whose whole packages are limited to 250 MB compressed).
    /// </summary>
    internal const long MaxLibraryBytes = 256L * 1024 * 1024;

    /// <summary>The most library bytes inflated from one package (an estimate: four libraries at the limit).</summary>
    internal const long MaxTotalLibraryBytes = 4 * MaxLibraryBytes;

    /// <summary>
    /// The largest .nuspec read (an estimate: real nuspecs are a few kilobytes, even with long descriptions and
    /// release notes), so a highly compressed one cannot inflate without bound.
    /// </summary>
    internal const long MaxNuspecBytes = 1024 * 1024;

    /// <summary>
    /// The deepest element nesting read from a .nuspec (an estimate: real ones nest five levels, package to
    /// dependency). XElement.Value recurses once per level, so deeper nesting could overflow the stack.
    /// </summary>
    internal const int MaxNuspecDepth = 32;

    /// <summary>Reads a .nupkg file.</summary>
    /// <exception cref="InvalidDataException">The file is not a valid package, or a library is not a valid assembly.</exception>
    public static NuGetPackage Read(string path, CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(path);
        return Read(stream, cancellationToken);
    }

    /// <summary>Reads a package from a seekable stream, which is left open.</summary>
    /// <exception cref="InvalidDataException">The data is not a valid package, or a library is not a valid assembly.</exception>
    public static NuGetPackage Read(Stream stream, CancellationToken cancellationToken = default)
    {
        using var archive = ZipReadBudget.Open(stream);
        var entries = ZipReadBudget.Entries(archive);
        var nuspec = entries.FirstOrDefault(entry => entry.FullName.IndexOfAny(['/', '\\']) < 0
            && entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The package has no .nuspec file at its root.");
        var metadata = ReadNuspec(nuspec, cancellationToken);
        var libraries = new List<NuGetLibrary>();
        var budget = new ZipReadBudget(MaxLibraryBytes, MaxTotalLibraryBytes);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (LibraryFramework(entry.FullName) is { } framework)
            {
                libraries.Add(ReadLibrary(entry, framework, budget, cancellationToken));
            }
        }

        libraries.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
        return metadata with { Libraries = libraries.AsReadOnly() };
    }

    private static NuGetPackage ReadNuspec(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        // The total is set past the entry size only so that the entry size is the limit that reports.
        var bytes = new ZipReadBudget(MaxNuspecBytes, 2 * MaxNuspecBytes).Read(entry, cancellationToken)
            ?? throw new InvalidDataException($"The package's .nuspec is larger than {MaxNuspecBytes} bytes.");
        XDocument document;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxNuspecBytes };
            using (var scan = XmlReader.Create(new MemoryStream(bytes, writable: false), settings))
            {
                while (scan.Read())
                {
                    if (scan.Depth > MaxNuspecDepth)
                    {
                        throw new InvalidDataException($"The package's .nuspec nests more than {MaxNuspecDepth} levels deep.");
                    }
                }
            }

            using var reader = XmlReader.Create(new MemoryStream(bytes, writable: false), settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("The package's .nuspec is not valid XML: " + exception.Message, exception);
        }

        var metadata = document.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "metadata")
            ?? throw new InvalidDataException("The package's .nuspec has no metadata element.");
        var id = Child(metadata, "id")?.Value.Trim() ?? string.Empty;
        var version = Child(metadata, "version")?.Value.Trim() ?? string.Empty;
        return new NuGetPackage(id, version, ReadDependencyGroups(Child(metadata, "dependencies")), Array.Empty<NuGetLibrary>());
    }

    private static IReadOnlyList<NuGetDependencyGroup> ReadDependencyGroups(XElement? dependencies)
    {
        var groups = new List<NuGetDependencyGroup>();
        if (dependencies is null)
        {
            return groups;
        }

        // Older nuspecs list dependencies directly; newer ones group them by target framework.
        var ungrouped = Dependencies(dependencies);
        if (ungrouped.Count > 0)
        {
            groups.Add(new NuGetDependencyGroup(string.Empty, ungrouped));
        }

        foreach (var group in dependencies.Elements().Where(element => element.Name.LocalName == "group"))
        {
            var framework = (string?)group.Attribute("targetFramework") ?? string.Empty;
            groups.Add(new NuGetDependencyGroup(framework, Dependencies(group)));
        }

        groups.Sort((left, right) => string.Compare(left.TargetFramework, right.TargetFramework, StringComparison.OrdinalIgnoreCase));
        return groups.AsReadOnly();
    }

    private static IReadOnlyList<NuGetDependency> Dependencies(XElement parent) => parent.Elements()
        .Where(element => element.Name.LocalName == "dependency")
        .Select(element => new NuGetDependency((string?)element.Attribute("id") ?? string.Empty, (string?)element.Attribute("version")))
        .OrderBy(dependency => dependency.Id, StringComparer.OrdinalIgnoreCase)
        .ToList()
        .AsReadOnly();

    private static XElement? Child(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(element => element.Name.LocalName == name);

    /// <summary>The target framework of a lib/&lt;tfm&gt;/&lt;name&gt;.dll entry, or null for any other entry.</summary>
    private static string? LibraryFramework(string fullName)
    {
        var parts = fullName.Replace('\\', '/').Split('/');
        var isLibrary = parts.Length == 3 && parts[0].Equals("lib", StringComparison.OrdinalIgnoreCase)
            && parts[2].EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        return isLibrary ? Uri.UnescapeDataString(parts[1]) : null;
    }

    private static NuGetLibrary ReadLibrary(ZipArchiveEntry entry, string framework, ZipReadBudget budget, CancellationToken cancellationToken)
    {
        var path = Uri.UnescapeDataString(entry.FullName.Replace('\\', '/'));

        // The metadata reader needs a seekable stream, which a zip entry is not.
        var bytes = budget.Read(entry, cancellationToken)
            ?? throw new InvalidDataException($"{path}: the library is larger than {MaxLibraryBytes} bytes.");
        using var buffer = new MemoryStream(bytes, writable: false);
        try
        {
            return new NuGetLibrary(framework, path, ApiReader.Read(buffer, cancellationToken));
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException(path + ": " + exception.Message, exception);
        }
    }
}
