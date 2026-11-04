using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DiffRapid.Semantic.Internal;

/// <summary>Builds the member tree of a parsed C# file.</summary>
internal sealed class MemberCollector
{
    private readonly LineTable _table;

    private MemberCollector(LineTable table)
    {
        _table = table;
    }

    public static List<CSharpMember> Collect(SyntaxNode root, LineTable table)
    {
        var collector = new MemberCollector(table);
        var roots = new List<CSharpMember>();
        if (root is CompilationUnitSyntax unit)
        {
            collector.AddAll(unit.Members, null, roots);
        }

        return roots;
    }

    private void AddAll(IEnumerable<MemberDeclarationSyntax> declarations, CSharpMember? parent, List<CSharpMember> roots)
    {
        foreach (var declaration in declarations)
        {
            foreach (var member in Create(declaration, parent))
            {
                if (parent is null)
                {
                    member.Index = roots.Count;
                    roots.Add(member);
                }
                else
                {
                    parent.AddChild(member);
                }

                AddChildren(member, roots);
            }
        }
    }

    private void AddChildren(CSharpMember member, List<CSharpMember> roots)
    {
        switch (member.Node)
        {
            case BaseNamespaceDeclarationSyntax namespaceDeclaration:
                AddAll(namespaceDeclaration.Members, member, roots);
                break;

            case TypeDeclarationSyntax typeDeclaration:
                AddAll(typeDeclaration.Members, member, roots);
                break;

            case EnumDeclarationSyntax enumDeclaration:
                AddAll(enumDeclaration.Members, member, roots);
                break;
        }
    }

    private IEnumerable<CSharpMember> Create(MemberDeclarationSyntax declaration, CSharpMember? parent)
    {
        switch (declaration)
        {
            case BaseNamespaceDeclarationSyntax namespaceDeclaration:
                var namespaceName = Compact(namespaceDeclaration.Name);
                yield return Member(CSharpMemberKind.Namespace, namespaceName, namespaceName, declaration, parent, default);
                break;

            case BaseTypeDeclarationSyntax typeDeclaration:
                yield return TypeMember(typeDeclaration, parent);
                break;

            case DelegateDeclarationSyntax delegateDeclaration:
                yield return Member(
                    CSharpMemberKind.Delegate,
                    delegateDeclaration.Identifier.Text,
                    delegateDeclaration.Identifier.Text + TypeParameters(delegateDeclaration.TypeParameterList) + Parameters(delegateDeclaration.ParameterList),
                    declaration,
                    parent,
                    delegateDeclaration.Identifier);
                break;

            case MethodDeclarationSyntax method:
                var methodName = ExplicitInterface(method.ExplicitInterfaceSpecifier) + method.Identifier.Text;
                yield return Member(
                    CSharpMemberKind.Method,
                    methodName,
                    methodName + TypeParameters(method.TypeParameterList) + Parameters(method.ParameterList),
                    declaration,
                    parent,
                    method.Identifier);
                break;

            case ConstructorDeclarationSyntax constructor:
                var isStatic = constructor.Modifiers.Any(SyntaxKind.StaticKeyword);
                yield return Member(
                    CSharpMemberKind.Constructor,
                    isStatic ? ".cctor" : ".ctor",
                    (isStatic ? "static " : "") + constructor.Identifier.Text + Parameters(constructor.ParameterList),
                    declaration,
                    parent,
                    default);
                break;

            case DestructorDeclarationSyntax destructor:
                yield return Member(CSharpMemberKind.Destructor, "Finalize", "~" + destructor.Identifier.Text + "()", declaration, parent, default);
                break;

            case OperatorDeclarationSyntax operatorDeclaration:
                var operatorName = "operator " + operatorDeclaration.OperatorToken.Text;
                yield return Member(
                    CSharpMemberKind.Operator,
                    operatorName,
                    operatorName + Parameters(operatorDeclaration.ParameterList),
                    declaration,
                    parent,
                    default);
                break;

            case ConversionOperatorDeclarationSyntax conversion:
                var conversionName = conversion.ImplicitOrExplicitKeyword.Text + " operator " + Compact(conversion.Type);
                yield return Member(
                    CSharpMemberKind.ConversionOperator,
                    conversionName,
                    conversionName + Parameters(conversion.ParameterList),
                    declaration,
                    parent,
                    default);
                break;

            case PropertyDeclarationSyntax property:
                var propertyName = ExplicitInterface(property.ExplicitInterfaceSpecifier) + property.Identifier.Text;
                yield return Member(CSharpMemberKind.Property, propertyName, propertyName, declaration, parent, property.Identifier);
                break;

            case IndexerDeclarationSyntax indexer:
                var indexerName = ExplicitInterface(indexer.ExplicitInterfaceSpecifier) + "this";
                var indexerParameters = indexer.ParameterList.Parameters.Select(Parameter);
                yield return Member(
                    CSharpMemberKind.Indexer,
                    indexerName,
                    indexerName + "[" + string.Join(", ", indexerParameters) + "]",
                    declaration,
                    parent,
                    default);
                break;

            case EventDeclarationSyntax eventDeclaration:
                var eventName = ExplicitInterface(eventDeclaration.ExplicitInterfaceSpecifier) + eventDeclaration.Identifier.Text;
                yield return Member(CSharpMemberKind.Event, eventName, eventName, declaration, parent, eventDeclaration.Identifier);
                break;

            case BaseFieldDeclarationSyntax field:
                var fieldKind = field is EventFieldDeclarationSyntax ? CSharpMemberKind.Event : CSharpMemberKind.Field;
                foreach (var variable in field.Declaration.Variables)
                {
                    yield return Member(fieldKind, variable.Identifier.Text, variable.Identifier.Text, declaration, parent, variable.Identifier);
                }

                break;

            case EnumMemberDeclarationSyntax enumMember:
                yield return Member(CSharpMemberKind.EnumMember, enumMember.Identifier.Text, enumMember.Identifier.Text, declaration, parent, enumMember.Identifier);
                break;
        }
    }

    private CSharpMember TypeMember(BaseTypeDeclarationSyntax declaration, CSharpMember? parent)
    {
        var kind = declaration.Kind() switch
        {
            SyntaxKind.StructDeclaration => CSharpMemberKind.Struct,
            SyntaxKind.InterfaceDeclaration => CSharpMemberKind.Interface,
            SyntaxKind.RecordDeclaration => CSharpMemberKind.Record,
            SyntaxKind.RecordStructDeclaration => CSharpMemberKind.RecordStruct,
            SyntaxKind.EnumDeclaration => CSharpMemberKind.Enum,
            _ => CSharpMemberKind.Class,
        };
        var typeParameters = declaration is TypeDeclarationSyntax type ? TypeParameters(type.TypeParameterList) : "";
        var name = declaration.Identifier.Text;
        return Member(kind, name, name + typeParameters, declaration, parent, declaration.Identifier);
    }

    private CSharpMember Member(
        CSharpMemberKind kind,
        string name,
        string signature,
        MemberDeclarationSyntax node,
        CSharpMember? parent,
        SyntaxToken identifier)
    {
        var start = ExtendedStart(node);
        var span = CreateSpan(start, node.Span.End);
        var compareSpan = CreateSpan(start, HeaderEnd(node));
        var key = kind + ":" + GenericPositions(RemoveParameterNames(signature), MethodTypeParameters(node));
        return new CSharpMember(kind, name, signature, key, parent, span, compareSpan, node, identifier);
    }

    private MemberSpan CreateSpan(int start, int end)
    {
        var lastLine = _table.LineOf(end > start ? end - 1 : start);
        return new MemberSpan(start, end - start, _table.LineOf(start), lastLine);
    }

    /// <summary>The start of the comments directly above a declaration, or of the declaration itself.</summary>
    private static int ExtendedStart(SyntaxNode node)
    {
        var start = node.SpanStart;
        var trivia = node.GetLeadingTrivia();
        for (var i = trivia.Count - 1; i >= 0; i--)
        {
            var kind = trivia[i].Kind();
            if (kind is SyntaxKind.SingleLineCommentTrivia
                or SyntaxKind.MultiLineCommentTrivia
                or SyntaxKind.SingleLineDocumentationCommentTrivia
                or SyntaxKind.MultiLineDocumentationCommentTrivia)
            {
                start = trivia[i].SpanStart;
            }
            else if (kind is not (SyntaxKind.WhitespaceTrivia or SyntaxKind.EndOfLineTrivia))
            {
                break;
            }
        }

        return start;
    }

    /// <summary>The end of a container's header: its opening brace, or the semicolon of a file-scoped namespace.</summary>
    private static int HeaderEnd(SyntaxNode node)
    {
        var token = node switch
        {
            NamespaceDeclarationSyntax namespaceDeclaration => namespaceDeclaration.OpenBraceToken,
            FileScopedNamespaceDeclarationSyntax fileScoped => fileScoped.SemicolonToken,
            BaseTypeDeclarationSyntax type => type.OpenBraceToken,
            _ => default,
        };

        return token.IsKind(SyntaxKind.None) || token.IsMissing ? node.Span.End : token.Span.End;
    }

    private static string ExplicitInterface(ExplicitInterfaceSpecifierSyntax? specifier) =>
        specifier is null ? "" : Compact(specifier.Name) + ".";

    private static string TypeParameters(TypeParameterListSyntax? list) =>
        list is null ? "" : "<" + string.Join(", ", list.Parameters.Select(parameter => parameter.Identifier.Text)) + ">";

    private static string Parameters(BaseParameterListSyntax? list) =>
        list is null ? "" : "(" + string.Join(", ", list.Parameters.Select(Parameter)) + ")";

    /// <summary>A parameter as it matters for overloads: ref-kind modifiers and the type.</summary>
    private static string Parameter(BaseParameterSyntax parameter)
    {
        var builder = new StringBuilder();
        foreach (var modifier in parameter.Modifiers)
        {
            if (modifier.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword or SyntaxKind.InKeyword or SyntaxKind.ThisKeyword or SyntaxKind.ParamsKeyword)
            {
                builder.Append(modifier.Text).Append(' ');
            }
        }

        builder.Append(parameter.Type is null ? "?" : Compact(parameter.Type));
        return builder.ToString();
    }

    private static TypeParameterListSyntax? MethodTypeParameters(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax method => method.TypeParameterList,
        DelegateDeclarationSyntax delegateDeclaration => delegateDeclaration.TypeParameterList,
        _ => null,
    };

    /// <summary>
    /// Replaces a generic method's own type parameters in its parameter list by their positions, so that renaming
    /// T to U keeps the identity.
    /// </summary>
    private static string GenericPositions(string key, TypeParameterListSyntax? list)
    {
        var paren = key.IndexOf('(');
        if (list is null || paren < 0)
        {
            return key;
        }

        var parameters = key.Substring(paren);
        for (var i = 0; i < list.Parameters.Count; i++)
        {
            var pattern = @"(?<![\w@])" + Regex.Escape(list.Parameters[i].Identifier.Text) + @"(?!\w)";
            parameters = Regex.Replace(parameters, pattern, "!!" + i);
        }

        return key.Substring(0, paren) + parameters;
    }

    /// <summary>Generic parameter names do not change an overload's identity; only their count does.</summary>
    private static string RemoveParameterNames(string signature)
    {
        var open = signature.IndexOf('<');
        var paren = signature.IndexOfAny(['(', '[']);
        if (open < 0 || (paren >= 0 && open > paren))
        {
            return signature;
        }

        var close = signature.IndexOf('>', open);
        var arity = signature.Substring(open, close - open).Count(c => c == ',') + 1;
        return signature.Substring(0, open) + "`" + arity + signature.Substring(close + 1);
    }

    /// <summary>The tokens of a node without trivia, separated only where needed between words.</summary>
    internal static string Compact(SyntaxNode node)
    {
        var builder = new StringBuilder();
        foreach (var token in node.DescendantTokens())
        {
            var text = token.Text;
            if (builder.Length > 0 && text.Length > 0 && IsWordChar(builder[builder.Length - 1]) && IsWordChar(text[0]))
            {
                builder.Append(' ');
            }

            builder.Append(text);
        }

        return builder.ToString();
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@';
}
