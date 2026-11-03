using System.ComponentModel.Composition;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace DiffRapid.VisualStudio.Editor;

/// <summary>
/// Diff colors. They appear under Tools &gt; Options &gt; Fonts and Colors (Text Editor) so users can
/// change them. Pick the hue here; DiffRapid paints it as a light tint so syntax colours stay readable.
/// </summary>
internal static class DiffFormatNames
{
    public const string ChangedLine = "DiffRapid.ChangedLine";
    public const string InsertedLine = "DiffRapid.InsertedLine";
    public const string RemovedLine = "DiffRapid.RemovedLine";
    public const string MovedLine = "DiffRapid.MovedLine";
    public const string IgnoredLine = "DiffRapid.IgnoredLine";
    public const string ChangedText = "DiffRapid.ChangedText";
    public const string Filler = "DiffRapid.Filler";
}

[Export(typeof(EditorFormatDefinition))]
[Name(DiffFormatNames.ChangedLine)]
[UserVisible(true)]
internal sealed class ChangedLineFormat : EditorFormatDefinition
{
    public ChangedLineFormat()
    {
        DisplayName = "DiffRapid - Changed line";
        BackgroundColor = Color.FromRgb(0xD9, 0x9A, 0x1E);
        ForegroundCustomizable = false;
    }
}

[Export(typeof(EditorFormatDefinition))]
[Name(DiffFormatNames.InsertedLine)]
[UserVisible(true)]
internal sealed class InsertedLineFormat : EditorFormatDefinition
{
    public InsertedLineFormat()
    {
        DisplayName = "DiffRapid - Line only in the right file";
        BackgroundColor = Color.FromRgb(0x2D, 0xA4, 0x4E);
        ForegroundCustomizable = false;
    }
}

[Export(typeof(EditorFormatDefinition))]
[Name(DiffFormatNames.MovedLine)]
[UserVisible(true)]
internal sealed class MovedLineFormat : EditorFormatDefinition
{
    public MovedLineFormat()
    {
        DisplayName = "DiffRapid - Moved line";
        BackgroundColor = Color.FromRgb(0x3B, 0x82, 0xF6);
        ForegroundCustomizable = false;
    }
}

[Export(typeof(EditorFormatDefinition))]
[Name(DiffFormatNames.IgnoredLine)]
[UserVisible(true)]
internal sealed class IgnoredLineFormat : EditorFormatDefinition
{
    public IgnoredLineFormat()
    {
        DisplayName = "DiffRapid - Ignored difference";
        BackgroundColor = Color.FromRgb(0x80, 0x80, 0x80);
        ForegroundCustomizable = false;
    }
}

[Export(typeof(EditorFormatDefinition))]
[Name(DiffFormatNames.ChangedText)]
[UserVisible(true)]
internal sealed class ChangedTextFormat : EditorFormatDefinition
{
    public ChangedTextFormat()
    {
        DisplayName = "DiffRapid - Changed text within a line";
        BackgroundColor = Color.FromRgb(0xD9, 0x8A, 0x1E);
        ForegroundCustomizable = false;
    }
}

[Export(typeof(EditorFormatDefinition))]
[Name(DiffFormatNames.Filler)]
[UserVisible(true)]
internal sealed class FillerFormat : EditorFormatDefinition
{
    public FillerFormat()
    {
        DisplayName = "DiffRapid - Filler (no matching lines)";
        BackgroundColor = Color.FromRgb(0x80, 0x80, 0x80);
        ForegroundCustomizable = false;
    }
}

[Export(typeof(EditorFormatDefinition))]
[Name(DiffFormatNames.RemovedLine)]
[UserVisible(true)]
internal sealed class RemovedLineFormat : EditorFormatDefinition
{
    public RemovedLineFormat()
    {
        DisplayName = "DiffRapid - Line only in the left file";
        BackgroundColor = Color.FromRgb(0xD7, 0x3A, 0x49);
        ForegroundCustomizable = false;
    }
}
