using System;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace DiffRapid.VisualStudio.UI;

/// <summary>Asks where to save a patch and writes it as UTF-8 with LF line breaks, as git and patch expect.</summary>
internal static class PatchExport
{
    /// <summary>Shows the save dialog (UI thread); returns the chosen path, or null when cancelled.</summary>
    public static string? AskPath(string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Create patch",
            FileName = UiText.SafeFileName(suggestedName),
            Filter = "Patch file (*.patch)|*.patch|Diff file (*.diff)|*.diff|All files (*.*)|*.*",
            DefaultExt = ".patch",
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>
    /// Writes the patch to <paramref name="path"/> through a temporary file, so a failure leaves no half-written patch;
    /// safe to call from a background thread. Returns the footer message: where the patch went or why it failed.
    /// </summary>
    public static string Write(string path, Action<TextWriter> write)
    {
        try
        {
            UiText.WriteText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), writer =>
            {
                writer.NewLine = "\n";
                write(writer);
            });
            return $"Saved the patch to {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not save the patch: {ex.Message}";
        }
    }
}
