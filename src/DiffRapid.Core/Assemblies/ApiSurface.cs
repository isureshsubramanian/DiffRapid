using System;
using System.Collections.Generic;
using System.Linq;

namespace DiffRapid.Core.Assemblies;

/// <summary>Whether an API entry declares a type or a member of a type.</summary>
public enum ApiEntryKind
{
    Type,
    Member,
}

/// <summary>One declaration in a public API surface.</summary>
/// <param name="Kind">A type declaration or a member declaration.</param>
/// <param name="Namespace">The namespace of the type, empty for the global namespace.</param>
/// <param name="TypeName">The full name of the declaring type (or of the type itself), with its type parameters.</param>
/// <param name="MemberName">
/// The name that identifies the member across versions (".ctor", "this[]", a method or property name);
/// empty for a type. Two declarations with the same name are overloads, or one changed signature.
/// </param>
/// <param name="Declaration">The C#-like declaration text.</param>
public sealed record ApiEntry(ApiEntryKind Kind, string Namespace, string TypeName, string MemberName, string Declaration)
{
    /// <summary>Sorts entries by namespace and type, the type declaration before its members.</summary>
    public static IComparer<ApiEntry> Order { get; } = Comparer<ApiEntry>.Create(CompareEntries);

    /// <summary>The key that pairs a removed declaration with an added one as a changed signature.</summary>
    internal (ApiEntryKind Kind, string Namespace, string TypeName, string MemberName) Identity => (Kind, Namespace, TypeName, MemberName);

    private static int CompareEntries(ApiEntry? left, ApiEntry? right)
    {
        if (left is null || right is null)
        {
            return left is null ? (right is null ? 0 : -1) : 1;
        }

        var result = string.CompareOrdinal(left.Namespace, right.Namespace);
        if (result == 0)
        {
            result = string.CompareOrdinal(left.TypeName, right.TypeName);
        }

        if (result == 0)
        {
            result = left.Kind.CompareTo(right.Kind);
        }

        if (result == 0)
        {
            result = string.CompareOrdinal(left.Declaration, right.Declaration);
        }

        return result;
    }
}

/// <summary>Identity and build information of an assembly.</summary>
/// <param name="Name">The simple assembly name.</param>
/// <param name="Version">The assembly version.</param>
/// <param name="FileVersion">The AssemblyFileVersion attribute, when present.</param>
/// <param name="InformationalVersion">The AssemblyInformationalVersion attribute, when present.</param>
/// <param name="TargetFramework">The TargetFramework attribute, e.g. ".NETStandard,Version=v2.0", when present.</param>
/// <param name="PublicKeyToken">The strong-name public key token in hex, or null when unsigned.</param>
/// <param name="Architecture">The processor architecture from the PE and CLI headers, e.g. "AnyCPU" or "Amd64".</param>
/// <param name="References">The referenced assemblies as display names, sorted.</param>
public sealed record AssemblyInfo(
    string Name,
    string Version,
    string? FileVersion,
    string? InformationalVersion,
    string? TargetFramework,
    string? PublicKeyToken,
    string Architecture,
    IReadOnlyList<string> References)
{
    /// <summary>The information as named values, in a fixed order, for display and comparison.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ToProperties()
    {
        var properties = new List<KeyValuePair<string, string>>
        {
            new("Name", Name),
            new("Version", Version),
        };
        Add(properties, "File version", FileVersion);
        Add(properties, "Informational version", InformationalVersion);
        Add(properties, "Target framework", TargetFramework);
        Add(properties, "Public key token", PublicKeyToken);
        properties.Add(new("Architecture", Architecture));
        foreach (var reference in References)
        {
            var comma = reference.IndexOf(',');
            var name = comma < 0 ? reference : reference.Substring(0, comma);
            properties.Add(new("Reference " + name, reference));
        }

        return properties;
    }

    private static void Add(List<KeyValuePair<string, string>> properties, string name, string? value)
    {
        if (value is not null)
        {
            properties.Add(new(name, value));
        }
    }
}

/// <summary>The public API of an assembly: everything another assembly can see or derive from.</summary>
public sealed class ApiSurface
{
    /// <summary>Creates a surface; duplicate entries are dropped and the rest sorted.</summary>
    public ApiSurface(AssemblyInfo info, IEnumerable<ApiEntry> entries)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        Entries = (entries ?? throw new ArgumentNullException(nameof(entries))).Distinct().OrderBy(entry => entry, ApiEntry.Order).ToList().AsReadOnly();
    }

    public AssemblyInfo Info { get; }

    /// <summary>The declarations, sorted with <see cref="ApiEntry.Order"/>.</summary>
    public IReadOnlyList<ApiEntry> Entries { get; }

    /// <summary>
    /// The API as text lines: a "namespace" line per namespace, each type declaration indented under it and
    /// each member indented under its type. The output is stable for the same input.
    /// </summary>
    public IReadOnlyList<string> ToLines()
    {
        var lines = new List<string>();
        string? space = null;
        foreach (var entry in Entries)
        {
            if (entry.Namespace != space)
            {
                space = entry.Namespace;
                lines.Add("namespace " + (space.Length == 0 ? "<global>" : space));
            }

            lines.Add((entry.Kind == ApiEntryKind.Type ? "  " : "    ") + entry.Declaration);
        }

        return lines;
    }

    /// <summary>The assembly information as "Name: value" lines, followed by <see cref="ToLines"/>.</summary>
    public string ToText()
    {
        var header = Info.ToProperties().Select(property => property.Key + ": " + property.Value);
        return string.Join("\n", header.Concat(new[] { string.Empty }).Concat(ToLines())) + "\n";
    }
}
