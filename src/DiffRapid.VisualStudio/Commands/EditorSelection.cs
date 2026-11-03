using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;

namespace DiffRapid.VisualStudio.Commands;

/// <summary>
/// What the active code editor offers for a comparison: its selection, or the whole document when nothing
/// is selected. Text that is not a whole saved file is written to a temporary file, since comparisons open files.
/// </summary>
internal sealed class EditorSelection
{
    private EditorSelection(string text, string? documentPath, string displayName, bool isWholeDocument)
    {
        Text = text;
        DocumentPath = documentPath;
        DisplayName = displayName;
        IsWholeDocument = isWholeDocument;
    }

    public string Text { get; }

    /// <summary>Path of the document the text comes from, when it has one.</summary>
    public string? DocumentPath { get; }

    /// <summary>Short description for file names and messages, for example "Program.cs lines 10-24".</summary>
    public string DisplayName { get; }

    public bool IsWholeDocument { get; }

    /// <summary>The selection of the active code editor, or its whole text when nothing is selected; null without an editor.</summary>
    public static EditorSelection? FromActiveEditor()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var view = ActiveView();
        if (view is null)
        {
            return null;
        }

        view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document);
        var path = document?.FilePath;
        var name = path is null ? "Untitled" : Path.GetFileName(path);
        var snapshot = view.TextSnapshot;
        if (view.Selection.IsEmpty)
        {
            return new EditorSelection(snapshot.GetText(), path, name, isWholeDocument: true);
        }

        var spans = view.Selection.SelectedSpans;
        var text = string.Join(Environment.NewLine, spans.Select(span => span.GetText()));
        var first = snapshot.GetLineNumberFromPosition(spans[0].Start) + 1;
        var last = snapshot.GetLineNumberFromPosition(spans[spans.Count - 1].End) + 1;
        var lines = first == last ? $"line {first}" : $"lines {first}-{last}";
        return new EditorSelection(text, path, $"{name} {lines}", isWholeDocument: false);
    }

    public static bool HasActiveEditor()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return ActiveView() is not null;
    }

    /// <summary>
    /// A file to compare: the document itself when the whole saved document was taken (so it can be edited
    /// in the comparison), otherwise a temporary copy of the text.
    /// </summary>
    public string ToComparisonFile()
    {
        if (IsWholeDocument && DocumentPath is not null && File.Exists(DocumentPath))
        {
            return DocumentPath;
        }

        return WriteTemporary(Text, DisplayName, Extension);
    }

    /// <summary>Extension of the source document, so the comparison gets the same syntax colours.</summary>
    public string Extension => Path.GetExtension(DocumentPath ?? string.Empty);

    /// <summary>Writes text to a new temporary folder under a readable name and returns the path.</summary>
    public static string WriteTemporary(string text, string name, string extension)
    {
        var folder = Path.Combine(Path.GetTempPath(), "DiffRapid", Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(folder);
        var safeName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = Path.Combine(folder, safeName + (safeName.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? string.Empty : extension));
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static IWpfTextView? ActiveView()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Package.GetGlobalService(typeof(SVsTextManager)) is not IVsTextManager textManager
            || textManager.GetActiveView(1, null, out var vsView) != 0
            || vsView is null)
        {
            return null;
        }

        var componentModel = (IComponentModel?)Package.GetGlobalService(typeof(SComponentModel));
        return componentModel?.GetService<IVsEditorAdaptersFactoryService>().GetWpfTextView(vsView);
    }
}
