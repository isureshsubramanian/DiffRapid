using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DiffRapid.Core.Structured;

/// <summary>A mutable node used by the readers; frozen into <see cref="StructuredNode"/> once the file is read.</summary>
internal sealed class NodeBuilder(StructuredNodeKind kind, string name, int startLine)
{
    /// <summary>Deepest nesting the readers accept, so that comparing never runs out of stack.</summary>
    public const int MaxDepth = 512;

    public StructuredNodeKind Kind { get; set; } = kind;

    public string Name { get; } = name;

    public string? Value { get; set; }

    public int StartLine { get; } = startLine;

    public int EndLine { get; set; } = startLine;

    public List<NodeBuilder> Children { get; } = [];

    /// <summary>The key before duplicates are numbered; the name when null.</summary>
    public string? BaseKey { get; set; }

    public string? IdentityAttribute { get; set; }

    public NodeBuilder Add(NodeBuilder child)
    {
        Children.Add(child);
        return child;
    }

    /// <summary>Assigns keys and paths and returns the immutable tree.</summary>
    public StructuredNode Freeze(string? arrayKey, CancellationToken cancellationToken) =>
        Freeze(this, "/", "/", false, arrayKey, cancellationToken);

    private static StructuredNode Freeze(NodeBuilder builder, string key, string path, bool byIndex, string? arrayKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var children = new StructuredNode[builder.Children.Count];
        var keys = ChildKeys(builder, arrayKey);
        for (var i = 0; i < children.Length; i++)
        {
            var childKey = keys[i].Key;
            var childPath = childKey.StartsWith("[", StringComparison.Ordinal) ? path + childKey : JoinPath(path, childKey);
            children[i] = Freeze(builder.Children[i], childKey, childPath, keys[i].ByIndex, arrayKey, cancellationToken);
        }

        var span = new StructuredLineSpan(builder.StartLine, Math.Max(builder.StartLine, builder.EndLine));
        return new StructuredNode(builder.Kind, builder.Name, key, path, builder.Value, children, span)
        {
            IdentityAttribute = builder.IdentityAttribute,
            IsKeyedByIndex = byIndex,
        };
    }

    private static string JoinPath(string parent, string key) => parent == "/" ? "/" + key : parent + "/" + key;

    private static (string Key, bool ByIndex)[] ChildKeys(NodeBuilder parent, string? arrayKey)
    {
        var keys = new (string Key, bool ByIndex)[parent.Children.Count];
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var itemIdentity = parent.Kind == StructuredNodeKind.Array ? arrayKey ?? IdentityOf(parent) : null;
        for (var i = 0; i < keys.Length; i++)
        {
            var child = parent.Children[i];
            var byIndex = false;
            string baseKey;
            if (parent.Kind == StructuredNodeKind.Array)
            {
                var itemKey = ArrayItemKey(child, itemIdentity);
                byIndex = itemKey is null;
                baseKey = itemKey ?? "[" + i + "]";
            }
            else
            {
                baseKey = child.BaseKey ?? child.Name;
            }

            seen.TryGetValue(baseKey, out var count);
            seen[baseKey] = count + 1;
            keys[i] = (count == 0 ? baseKey : baseKey + "#" + (count + 1), byIndex);
        }

        return keys;
    }

    // The member names that identify an object in a list, as Include and name do for XML elements.
    private static readonly string[] ItemIdentities = ["id", "Id", "ID", "name", "Name", "key", "Key"];

    /// <summary>
    /// Without a given array key, the member that names every item of a list of objects: present in each with a
    /// scalar value, and unique. Null when there is none, so the items are matched by position.
    /// </summary>
    private static string? IdentityOf(NodeBuilder list)
    {
        if (list.Children.Count == 0 || list.Children.Any(child => child.Kind != StructuredNodeKind.Object))
        {
            return null;
        }

        foreach (var identity in ItemIdentities)
        {
            var values = new HashSet<string>(StringComparer.Ordinal);
            if (list.Children.All(child => child.Children.FirstOrDefault(member => member.Name == identity) is { Value: { } value } member
                    && member.Children.Count == 0 && values.Add(value)))
            {
                return identity;
            }
        }

        return null;
    }

    private static string? ArrayItemKey(NodeBuilder item, string? arrayKey)
    {
        if (arrayKey is null || item.Kind != StructuredNodeKind.Object)
        {
            return null;
        }

        foreach (var property in item.Children)
        {
            if (property.Name == arrayKey && property.Value is not null)
            {
                return "[" + arrayKey + "=" + property.Value + "]";
            }
        }

        return null;
    }
}
