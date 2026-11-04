using System;
using System.Collections.Generic;
using DiffRapid.Core.Text;

namespace DiffRapid.Core.Comparison;

/// <summary>Whether a difference matters to the reader.</summary>
public enum Importance
{
    /// <summary>A real difference.</summary>
    Important,

    /// <summary>A difference that only touches text the rules say does not matter.</summary>
    Unimportant,
}

/// <summary>The rules that made a difference unimportant. None means the difference is important.</summary>
[Flags]
public enum UnimportantReasons
{
    /// <summary>No rule applies; the difference is important.</summary>
    None = 0,

    /// <summary>Already ignored by the compare options (blank-line option or line filters).</summary>
    Filtered = 1,

    /// <summary>Only blank lines were added or removed.</summary>
    BlankLines = 2,

    /// <summary>Only whitespace, line breaks or line endings changed.</summary>
    Whitespace = 4,

    /// <summary>Only the case of letters changed.</summary>
    Case = 8,

    /// <summary>Only comments changed.</summary>
    Comment = 16,

    /// <summary>Only text matching an unimportant-text pattern changed.</summary>
    Pattern = 32,

    /// <summary>A rule supplied by another layer, such as semantic analysis, explained the difference.</summary>
    Custom = 64,
}

/// <summary>A pair of strings that open and close a block comment.</summary>
public sealed record BlockCommentDelimiters(string Open, string Close);

/// <summary>How comments are written in a file's language.</summary>
public sealed record CommentSyntax(IReadOnlyList<string> LineComments, IReadOnlyList<BlockCommentDelimiters> BlockComments)
{
    /// <summary>C, C++, C#, Java, JavaScript: // and /* */.</summary>
    public static readonly CommentSyntax CStyle = new(new[] { "//" }, new[] { new BlockCommentDelimiters("/*", "*/") });

    /// <summary>Shell, Python, PowerShell, YAML and .ini-like files: #.</summary>
    public static readonly CommentSyntax Hash = new(new[] { "#" }, Array.Empty<BlockCommentDelimiters>());

    /// <summary>SQL: -- and /* */.</summary>
    public static readonly CommentSyntax Sql = new(new[] { "--" }, new[] { new BlockCommentDelimiters("/*", "*/") });

    /// <summary>XML, HTML, XAML and MSBuild: &lt;!-- --&gt;. Quotes are not treated as strings.</summary>
    public static readonly CommentSyntax Xml = new(Array.Empty<string>(), new[] { new BlockCommentDelimiters("<!--", "-->") })
    {
        QuotedStrings = false,
    };

    /// <summary>
    /// When true, comment markers inside "double" or 'single' quoted text on one line (with backslash escapes)
    /// do not start a comment.
    /// </summary>
    public bool QuotedStrings { get; init; } = true;
}

/// <summary>What a custom importance rule is asked about: one hunk of a comparison.</summary>
public sealed record ImportanceContext(TextDiffResult Result, TextDocument Left, TextDocument Right, int HunkIndex, DiffHunk Hunk);

/// <summary>
/// A hook for another layer (for example the semantic layer) to explain why a hunk does not matter.
/// </summary>
public interface IImportanceRule
{
    /// <summary>Returns a short reason when the hunk is unimportant, or null when this rule has nothing to say.</summary>
    string? ExplainUnimportant(ImportanceContext context);
}

/// <summary>
/// Rules that mark differences as unimportant, in the manner of "unimportant text" in other compare tools.
/// Every rule is off by default. Immutable; use <c>with</c> to derive variants.
/// </summary>
public sealed record ImportanceRules
{
    /// <summary>Rules that make nothing unimportant except hunks the compare options already ignore.</summary>
    public static readonly ImportanceRules None = new();

    /// <summary>Regular expressions for text that does not matter; matches are removed before comparing.</summary>
    public IReadOnlyList<string> UnimportantPatterns { get; init; } = Array.Empty<string>();

    /// <summary>Changes to whitespace, line breaks and line endings are unimportant.</summary>
    public bool WhitespaceIsUnimportant { get; init; }

    /// <summary>Changes to the case of letters are unimportant.</summary>
    public bool CaseIsUnimportant { get; init; }

    /// <summary>Added or removed blank lines are unimportant.</summary>
    public bool BlankLinesAreUnimportant { get; init; }

    /// <summary>When set, changes inside comments written in this syntax are unimportant.</summary>
    public CommentSyntax? Comments { get; init; }

    /// <summary>Extra rules asked, in order, about hunks the built-in rules consider important.</summary>
    public IReadOnlyList<IImportanceRule> CustomRules { get; init; } = Array.Empty<IImportanceRule>();
}
