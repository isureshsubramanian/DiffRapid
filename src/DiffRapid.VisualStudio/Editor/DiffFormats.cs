using System.ComponentModel.Composition;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace DiffRapid.VisualStudio.Editor;

/// <summary>
/// Diff colors. They appear under Tools &gt; Options &gt; Fonts and Colors (Text Editor) so users can
/// change them. Defaults are translucent so they read well on light and dark themes.
/// </summary>
internal static class DiffFormatNames
{
    public const string ChangedLine = "DiffRapid.ChangedLine";
    public const string InsertedLine = "DiffRapid.InsertedLine";
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
        BackgroundColor = Color.FromArgb(0x38, 0xE0, 0xA8, 0x00);
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
        DisplayName = "DiffRapid - Line only on this side";
        BackgroundColor = Color.FromArgb(0x38, 0x2E, 0xA0, 0x43);
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
        BackgroundColor = Color.FromArgb(0x38, 0x3B, 0x82, 0xF6);
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
        BackgroundColor = Color.FromArgb(0x20, 0x80, 0x80, 0x80);
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
        BackgroundColor = Color.FromArgb(0x70, 0xE0, 0x80, 0x00);
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
        BackgroundColor = Color.FromArgb(0x24, 0x80, 0x80, 0x80);
        ForegroundCustomizable = false;
    }
}
