using System;
using System.Collections.Generic;

namespace DiffRapid.Core.Structured;

/// <summary>The file formats that can be compared by structure.</summary>
public enum StructuredFormat
{
    /// <summary>Chooses the format from the file name, then from the content.</summary>
    Auto,

    /// <summary>Generic XML: elements keyed by name plus an Include/Update/Remove/name/id/key attribute.</summary>
    Xml,

    /// <summary>JSON (RFC 8259).</summary>
    Json,

    /// <summary>The block-style YAML subset read by <see cref="YamlTreeReader"/>.</summary>
    Yaml,

    /// <summary>MSBuild project files: items and properties lifted out of their groups, metadata as attributes.</summary>
    MSBuild,

    /// <summary>.resx resource files: data keyed by name, the schema block left out.</summary>
    Resx,
}

/// <summary>What a node of a structured tree stands for.</summary>
public enum StructuredNodeKind
{
    /// <summary>The root of a file.</summary>
    Document,

    /// <summary>An XML element.</summary>
    Element,

    /// <summary>An XML attribute, or MSBuild item metadata written either way.</summary>
    Attribute,

    /// <summary>Text inside an XML element that also has child elements.</summary>
    Text,

    /// <summary>A JSON object or a YAML map.</summary>
    Object,

    /// <summary>A JSON array or a YAML sequence.</summary>
    Array,

    /// <summary>A string scalar.</summary>
    String,

    /// <summary>A number scalar.</summary>
    Number,

    /// <summary>A true or false scalar.</summary>
    Boolean,

    /// <summary>A null scalar (or an empty YAML value).</summary>
    Null,
}

/// <summary>A range of 1-based lines, both ends included.</summary>
/// <param name="StartLine">The first line.</param>
/// <param name="EndLine">The last line.</param>
public readonly record struct StructuredLineSpan(int StartLine, int EndLine)
{
    /// <inheritdoc />
    public override string ToString() => StartLine == EndLine ? StartLine.ToString() : StartLine + "-" + EndLine;
}

/// <summary>One node of a structured file: an element, attribute, map entry, array item or scalar.</summary>
public sealed class StructuredNode
{
    /// <summary>Creates a node.</summary>
    public StructuredNode(
        StructuredNodeKind kind,
        string name,
        string key,
        string path,
        string? value,
        IReadOnlyList<StructuredNode> children,
        StructuredLineSpan span)
    {
        Kind = kind;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Value = value;
        Children = children ?? throw new ArgumentNullException(nameof(children));
        Span = span;
    }

    /// <summary>What the node stands for.</summary>
    public StructuredNodeKind Kind { get; }

    /// <summary>The element, attribute or property name; empty for array items and the document.</summary>
    public string Name { get; }

    /// <summary>The key that matches this node with its counterpart among its siblings, unique among them.</summary>
    public string Key { get; }

    /// <summary>The keys from the root down to this node, e.g. /Project/PackageReference[Include=Xunit]/@Version.</summary>
    public string Path { get; }

    /// <summary>The scalar value, or an element's own text (empty when it has child elements); null for maps and arrays.</summary>
    public string? Value { get; }

    /// <summary>The child nodes in file order.</summary>
    public IReadOnlyList<StructuredNode> Children { get; }

    /// <summary>The lines the node covers in its file.</summary>
    public StructuredLineSpan Span { get; }

    /// <summary>The attribute whose value is part of <see cref="Key"/> (e.g. Include or name), or null.</summary>
    public string? IdentityAttribute { get; init; }

    /// <summary>True when the node is an array item keyed by its position.</summary>
    public bool IsKeyedByIndex { get; init; }

    /// <inheritdoc />
    public override string ToString() => Value is null ? Path : Path + " = " + Value;
}

/// <summary>Thrown when a structured file cannot be read; carries the line and column of the problem.</summary>
public sealed class StructuredFormatException : FormatException
{
    /// <summary>Creates the exception for a position in the file.</summary>
    public StructuredFormatException(string reason, int line, int column)
        : base(column > 0 ? $"line {line}, column {column}: {reason}" : $"line {line}: {reason}")
    {
        Reason = reason;
        Line = line;
        Column = column;
    }

    /// <summary>What is wrong, without the position.</summary>
    public string Reason { get; }

    /// <summary>The 1-based line of the problem.</summary>
    public int Line { get; }

    /// <summary>The 1-based column of the problem, or 0 when unknown.</summary>
    public int Column { get; }
}
