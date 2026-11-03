using System;
using System.ComponentModel;
using System.Drawing.Design;
using System.Linq;
using DiffRapid.Core.Algorithms;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Folders;
using Microsoft.VisualStudio.Shell;

namespace DiffRapid.VisualStudio.Options;

/// <summary>
/// Tools > Options > DiffRapid > General: the settings new comparisons start with. The toolbar of a
/// comparison still changes them for that comparison only.
/// </summary>
internal sealed class DiffRapidOptionsPage : DialogPage
{
    private const string FileCategory = "File comparison";
    private const string FolderCategory = "Folder comparison";
    private const char ListSeparator = ';';

    [Category(FileCategory)]
    [DisplayName("Whitespace")]
    [Description("How differences in whitespace are treated. IgnoreChange: runs of spaces compare equal and leading/trailing whitespace is ignored. IgnoreAll: all whitespace is removed before comparing.")]
    [DefaultValue(WhitespaceMode.Compare)]
    public WhitespaceMode Whitespace { get; set; } = WhitespaceMode.Compare;

    [Category(FileCategory)]
    [DisplayName("Ignore case")]
    [Description("Lines that differ only in letter case compare equal.")]
    [DefaultValue(false)]
    public bool IgnoreCase { get; set; }

    [Category(FileCategory)]
    [DisplayName("Ignore blank lines")]
    [Description("Inserted or deleted blank lines are shown as ignored rather than as differences.")]
    [DefaultValue(false)]
    public bool IgnoreBlankLines { get; set; }

    [Category(FileCategory)]
    [DisplayName("Ignore line endings")]
    [Description("CRLF, LF and CR line endings compare equal.")]
    [DefaultValue(false)]
    public bool IgnoreLineEndings { get; set; }

    [Category(FileCategory)]
    [DisplayName("Ignore numbers")]
    [Description("Sequences of digits compare equal whatever their value.")]
    [DefaultValue(false)]
    public bool IgnoreNumbers { get; set; }

    [Category(FileCategory)]
    [DisplayName("Ignore missing final line ending")]
    [Description("A last line without a line ending compares equal to one with a line ending.")]
    [DefaultValue(false)]
    public bool IgnoreMissingTrailingLineEnding { get; set; }

    [Category(FileCategory)]
    [DisplayName("Algorithm")]
    [Description("How lines are matched between the two files.")]
    [DefaultValue(DiffAlgorithmKind.Histogram)]
    public DiffAlgorithmKind Algorithm { get; set; } = DiffAlgorithmKind.Histogram;

    [Category(FileCategory)]
    [DisplayName("Detect moved blocks")]
    [Description("Blocks of lines that moved to another position are shown as moved.")]
    [DefaultValue(true)]
    public bool DetectMovedBlocks { get; set; } = true;

    [Category(FileCategory)]
    [DisplayName("Changes inside a line")]
    [Description("Whether changes inside a changed line are marked by word or by character.")]
    [DefaultValue(InlineGranularity.Word)]
    public InlineGranularity InlineGranularity { get; set; } = InlineGranularity.Word;

    [Category(FileCategory)]
    [DisplayName("Line filters")]
    [Description("Regular expressions, one per line. A difference in which every changed line matches one of them is shown as ignored (for example ^\\s*// to ignore comment-only changes).")]
    [Editor("System.Windows.Forms.Design.StringArrayEditor, System.Design", typeof(UITypeEditor))]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string[] LineFilters
    {
        // Not trimmed: spaces are part of a pattern.
        get => LineFiltersStorage.Split('\n').Where(p => p.Trim().Length > 0).ToArray();
        set => LineFiltersStorage = string.Join("\n", (value ?? Array.Empty<string>()).Where(p => p.Trim().Length > 0));
    }

    // Persisted form of LineFilters: Visual Studio stores option pages as strings.
    [Browsable(false)]
    public string LineFiltersStorage { get; set; } = string.Empty;

    [Category(FolderCategory)]
    [DisplayName("Compare by")]
    [Description("How files present on both sides are compared.")]
    [DefaultValue(FolderCompareMethod.FullContents)]
    public FolderCompareMethod Method { get; set; } = FolderCompareMethod.FullContents;

    [Category(FolderCategory)]
    [DisplayName("Include subfolders")]
    [Description("Compare the folders below the two roots as well.")]
    [DefaultValue(true)]
    public bool Recursive { get; set; } = true;

    [Category(FolderCategory)]
    [DisplayName("Skip common build folders")]
    [Description("Leave out .git, .vs, .idea, bin, obj, node_modules, packages and TestResults folders.")]
    [DefaultValue(true)]
    public bool SkipCommonFolders { get; set; } = true;

    [Category(FolderCategory)]
    [DisplayName("Exclude folders")]
    [Description("Folder masks to leave out, separated by semicolons (for example dist;*.cache;docs/generated).")]
    [DefaultValue("")]
    public string ExcludeFolders { get; set; } = string.Empty;

    [Category(FolderCategory)]
    [DisplayName("Exclude files")]
    [Description("File masks to leave out, separated by semicolons (for example *.min.js;*.designer.cs).")]
    [DefaultValue("")]
    public string ExcludeFiles { get; set; } = string.Empty;

    [Category(FolderCategory)]
    [DisplayName("Include files")]
    [Description("When set, only files matching these masks are compared, separated by semicolons (for example *.cs;*.sql).")]
    [DefaultValue("")]
    public string IncludeFiles { get; set; } = string.Empty;

    [Category(FolderCategory)]
    [DisplayName("Time tolerance (seconds)")]
    [Description("Modification times this close count as equal (FAT and zip files store times in 2-second steps).")]
    [DefaultValue(2.0)]
    public double TimeToleranceSeconds { get; set; } = 2;

    [Category(FolderCategory)]
    [DisplayName("Case-sensitive names")]
    [Description("Treat File.txt and file.txt as different entries.")]
    [DefaultValue(false)]
    public bool CaseSensitiveNames { get; set; }

    /// <summary>Text comparison settings for a new comparison.</summary>
    public CompareOptions ToCompareOptions() => CompareOptions.Default with
    {
        Whitespace = Whitespace,
        IgnoreCase = IgnoreCase,
        IgnoreBlankLines = IgnoreBlankLines,
        IgnoreLineEndings = IgnoreLineEndings,
        IgnoreNumbers = IgnoreNumbers,
        IgnoreMissingTrailingLineEnding = IgnoreMissingTrailingLineEnding,
        Algorithm = Algorithm,
        DetectMovedBlocks = DetectMovedBlocks,
        InlineGranularity = InlineGranularity,
        LineFilters = LineFilters.Select(p => new LineFilter(p)).ToList(),
    };

    /// <summary>Folder comparison settings for a new comparison; files are compared with the text settings.</summary>
    public FolderCompareOptions ToFolderOptions() => new()
    {
        Method = Method,
        Recursive = Recursive,
        DefaultExcludes = SkipCommonFolders,
        ExcludeFolders = Split(ExcludeFolders, ListSeparator),
        ExcludeFiles = Split(ExcludeFiles, ListSeparator),
        IncludeFiles = Split(IncludeFiles, ListSeparator),
        TimeTolerance = TimeSpan.FromSeconds(Math.Max(0, TimeToleranceSeconds)),
        CaseSensitive = CaseSensitiveNames,
        TextOptions = ToCompareOptions(),
    };

    private static string[] Split(string? text, char separator) =>
        (text ?? string.Empty).Split(separator).Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
}
