using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace DiffRapid.Semantic;

/// <summary>The kind of a declaration found in a C# file.</summary>
public enum CSharpMemberKind
{
    Namespace,
    Class,
    Struct,
    Interface,
    Record,
    RecordStruct,
    Enum,
    Delegate,
    Constructor,
    Destructor,
    Method,
    Operator,
    ConversionOperator,
    Property,
    Indexer,
    Field,
    Event,
    EnumMember,
}

/// <summary>A range of a source text. Positions are zero-based offsets; lines are zero-based and inclusive.</summary>
public sealed record MemberSpan(int Start, int Length, int StartLine, int EndLine)
{
    public int End => Start + Length;
}

/// <summary>A namespace, type or type member declared in a C# file.</summary>
public sealed class CSharpMember
{
    private readonly List<CSharpMember> _children = [];

    internal CSharpMember(
        CSharpMemberKind kind,
        string name,
        string signature,
        string key,
        CSharpMember? parent,
        MemberSpan span,
        MemberSpan compareSpan,
        SyntaxNode node,
        SyntaxToken identifier)
    {
        Kind = kind;
        Name = name;
        Signature = signature;
        Key = key;
        Parent = parent;
        Span = span;
        CompareSpan = compareSpan;
        Node = node;
        Identifier = identifier;
    }

    public CSharpMemberKind Kind { get; }

    /// <summary>The simple name, for example <c>Format</c>, <c>this</c> for an indexer or <c>op_Addition</c>.</summary>
    public string Name { get; }

    /// <summary>A readable signature, for example <c>Format&lt;T&gt;(string, ref int)</c>.</summary>
    public string Signature { get; }

    /// <summary>The identity of the member among its siblings: kind, name, generic arity and parameter types.</summary>
    public string Key { get; }

    /// <summary>The namespace or type that declares this member; null at the top of the file.</summary>
    public CSharpMember? Parent { get; }

    /// <summary>Members declared inside this namespace or type, in source order.</summary>
    public IReadOnlyList<CSharpMember> Children => _children;

    /// <summary>The position of this member among its parent's children.</summary>
    public int Index { get; internal set; }

    /// <summary>The whole declaration, including comments directly above it.</summary>
    public MemberSpan Span { get; }

    /// <summary>
    /// The text compared to decide whether the member changed: the whole declaration for members, and only the
    /// header (up to the opening brace) for namespaces and types, whose members are compared on their own.
    /// </summary>
    public MemberSpan CompareSpan { get; }

    /// <summary>True for namespaces, types and enums, which hold other members.</summary>
    public bool IsContainer => Kind <= CSharpMemberKind.Enum;

    /// <summary>The signature qualified with the containing namespaces and types.</summary>
    public string QualifiedName => Parent is null ? Signature : Parent.QualifiedName + "." + Signature;

    internal SyntaxNode Node { get; }

    internal SyntaxToken Identifier { get; }

    internal void AddChild(CSharpMember child)
    {
        child.Index = _children.Count;
        _children.Add(child);
    }

    public override string ToString() => Kind + " " + QualifiedName;
}
