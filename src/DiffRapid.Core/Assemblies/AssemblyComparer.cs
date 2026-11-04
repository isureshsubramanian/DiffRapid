using System;
using System.Collections.Generic;
using System.Linq;

namespace DiffRapid.Core.Assemblies;

/// <summary>How a declaration differs between two versions of an API.</summary>
public enum ApiChangeKind
{
    /// <summary>The declaration exists only in the new version.</summary>
    Added,

    /// <summary>The declaration exists only in the old version.</summary>
    Removed,

    /// <summary>A declaration with the same name has a different signature or modifiers.</summary>
    Changed,
}

/// <summary>A difference in the declared API.</summary>
/// <param name="Kind">Added, removed or changed.</param>
/// <param name="Entry">The old declaration for a removal or change, the new one for an addition.</param>
/// <param name="NewEntry">The new declaration of a change; null otherwise.</param>
public sealed record ApiChange(ApiChangeKind Kind, ApiEntry Entry, ApiEntry? NewEntry = null)
{
    /// <summary>Removing or changing a visible declaration can break code built against the old version.</summary>
    public bool IsBreaking => Kind != ApiChangeKind.Added;

    /// <summary>One line: a marker (+, - or ~), the declaring type for members, and the declaration(s).</summary>
    public override string ToString()
    {
        var owner = Entry.Kind == ApiEntryKind.Member ? Entry.TypeName + ": " : string.Empty;
        return Kind switch
        {
            ApiChangeKind.Added => "+ " + owner + Entry.Declaration,
            ApiChangeKind.Removed => "- " + owner + Entry.Declaration,
            _ => "~ " + owner + Entry.Declaration + "  =>  " + NewEntry?.Declaration,
        };
    }
}

/// <summary>A difference in assembly or package information, such as a version or a dependency.</summary>
/// <param name="Name">The property name, e.g. "Version" or "Reference System.Runtime".</param>
/// <param name="Left">The old value, or null when the property is new.</param>
/// <param name="Right">The new value, or null when the property was removed.</param>
/// <param name="IsBreaking">Whether the change stops existing callers from binding (identity, signing or a removed library).</param>
public sealed record ApiInfoChange(string Name, string? Left, string? Right, bool IsBreaking)
{
    public override string ToString() => $"{Name}: {Left ?? "(none)"} -> {Right ?? "(none)"}";
}

/// <summary>The differences between two assemblies.</summary>
/// <param name="InfoChanges">Differences in assembly information.</param>
/// <param name="Changes">Differences in the API, sorted by namespace and type.</param>
public sealed record ApiComparison(IReadOnlyList<ApiInfoChange> InfoChanges, IReadOnlyList<ApiChange> Changes)
{
    public bool IsIdentical => InfoChanges.Count == 0 && Changes.Count == 0;

    public bool HasBreakingChanges => InfoChanges.Any(change => change.IsBreaking) || Changes.Any(change => change.IsBreaking);
}

/// <summary>The comparison of one library path present in both packages.</summary>
/// <param name="Path">The path inside the package, e.g. "lib/net8.0/Example.dll".</param>
/// <param name="Comparison">The assembly comparison.</param>
public sealed record PackageLibraryComparison(string Path, ApiComparison Comparison);

/// <summary>The differences between two NuGet packages.</summary>
/// <param name="InfoChanges">Differences in package metadata and in which libraries the package contains.</param>
/// <param name="Libraries">The comparisons of libraries found in both packages, sorted by path.</param>
public sealed record PackageComparison(IReadOnlyList<ApiInfoChange> InfoChanges, IReadOnlyList<PackageLibraryComparison> Libraries)
{
    public bool IsIdentical => InfoChanges.Count == 0 && Libraries.All(library => library.Comparison.IsIdentical);

    public bool HasBreakingChanges => InfoChanges.Any(change => change.IsBreaking) || Libraries.Any(library => library.Comparison.HasBreakingChanges);
}

/// <summary>
/// Compares public API surfaces. A removed declaration is breaking; a declaration whose name stays but
/// whose text changes (signature, modifiers, base types, constant value) is a breaking change; an added
/// declaration is not. Members of an added or removed type are folded into the type's own entry.
/// </summary>
public static class AssemblyComparer
{
    private static readonly HashSet<string> BreakingInfo = new(StringComparer.Ordinal) { "Name", "Public key token", "Package id" };

    /// <summary>Compares two assembly APIs.</summary>
    public static ApiComparison Compare(ApiSurface left, ApiSurface right)
    {
        if (left is null)
        {
            throw new ArgumentNullException(nameof(left));
        }

        if (right is null)
        {
            throw new ArgumentNullException(nameof(right));
        }

        return new ApiComparison(CompareProperties(left.Info.ToProperties(), right.Info.ToProperties()), CompareEntries(left.Entries, right.Entries));
    }

    /// <summary>Compares two packages: metadata, the set of libraries, and the API of each library in both.</summary>
    public static PackageComparison Compare(NuGetPackage left, NuGetPackage right)
    {
        if (left is null)
        {
            throw new ArgumentNullException(nameof(left));
        }

        if (right is null)
        {
            throw new ArgumentNullException(nameof(right));
        }

        var infoChanges = CompareProperties(left.ToProperties(), right.ToProperties()).ToList();
        var rightLibraries = right.Libraries.ToDictionary(library => library.Path, StringComparer.OrdinalIgnoreCase);
        var leftPaths = new HashSet<string>(left.Libraries.Select(library => library.Path), StringComparer.OrdinalIgnoreCase);
        var libraries = new List<PackageLibraryComparison>();
        foreach (var library in left.Libraries)
        {
            if (rightLibraries.TryGetValue(library.Path, out var match))
            {
                libraries.Add(new PackageLibraryComparison(library.Path, Compare(library.Api, match.Api)));
            }
            else
            {
                infoChanges.Add(new ApiInfoChange("Library " + library.Path, "present", null, true));
            }
        }

        foreach (var library in right.Libraries.Where(library => !leftPaths.Contains(library.Path)))
        {
            infoChanges.Add(new ApiInfoChange("Library " + library.Path, null, "present", false));
        }

        return new PackageComparison(infoChanges.AsReadOnly(), libraries.AsReadOnly());
    }

    private static IReadOnlyList<ApiInfoChange> CompareProperties(
        IReadOnlyList<KeyValuePair<string, string>> left,
        IReadOnlyList<KeyValuePair<string, string>> right)
    {
        var leftValues = ToDictionary(left);
        var rightValues = ToDictionary(right);
        var changes = new List<ApiInfoChange>();
        foreach (var name in left.Select(property => property.Key).Concat(right.Select(property => property.Key)).Distinct())
        {
            leftValues.TryGetValue(name, out var leftValue);
            rightValues.TryGetValue(name, out var rightValue);
            if (leftValue != rightValue)
            {
                changes.Add(new ApiInfoChange(name, leftValue, rightValue, BreakingInfo.Contains(name)));
            }
        }

        return changes.AsReadOnly();
    }

    private static Dictionary<string, string> ToDictionary(IReadOnlyList<KeyValuePair<string, string>> properties)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            values[property.Key] = property.Value;
        }

        return values;
    }

    private static IReadOnlyList<ApiChange> CompareEntries(IReadOnlyList<ApiEntry> left, IReadOnlyList<ApiEntry> right)
    {
        var leftSet = new HashSet<ApiEntry>(left);
        var rightSet = new HashSet<ApiEntry>(right);
        var leftTypes = TypeNames(left);
        var rightTypes = TypeNames(right);

        // A member of a type that exists on one side only is covered by the type's own change.
        var removed = left.Where(entry => !rightSet.Contains(entry) && (entry.Kind == ApiEntryKind.Type || rightTypes.Contains(TypeKey(entry)))).ToList();
        var added = right.Where(entry => !leftSet.Contains(entry) && (entry.Kind == ApiEntryKind.Type || leftTypes.Contains(TypeKey(entry)))).ToList();

        var addedByIdentity = new Dictionary<(ApiEntryKind, string, string, string), Queue<ApiEntry>>();
        foreach (var entry in added)
        {
            if (!addedByIdentity.TryGetValue(entry.Identity, out var queue))
            {
                queue = new Queue<ApiEntry>();
                addedByIdentity[entry.Identity] = queue;
            }

            queue.Enqueue(entry);
        }

        var changes = new List<ApiChange>();
        foreach (var entry in removed)
        {
            var hasMatch = addedByIdentity.TryGetValue(entry.Identity, out var queue) && queue.Count > 0;
            changes.Add(hasMatch ? new ApiChange(ApiChangeKind.Changed, entry, queue!.Dequeue()) : new ApiChange(ApiChangeKind.Removed, entry));
        }

        changes.AddRange(addedByIdentity.Values.SelectMany(queue => queue).Select(entry => new ApiChange(ApiChangeKind.Added, entry)));
        return changes.OrderBy(change => change.Entry, ApiEntry.Order).ToList().AsReadOnly();
    }

    private static HashSet<(string, string)> TypeNames(IEnumerable<ApiEntry> entries) =>
        new(entries.Where(entry => entry.Kind == ApiEntryKind.Type).Select(TypeKey));

    private static (string, string) TypeKey(ApiEntry entry) => (entry.Namespace, entry.TypeName);
}
