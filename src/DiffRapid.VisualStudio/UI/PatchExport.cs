using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace DiffRapid.VisualStudio.UI;

/// <summary>Asks where to save a patch and writes it as UTF-8 with LF line breaks, as git and patch expect.</summary>
internal static class PatchExport
{
    /// <summary>Returns a message for the footer: where the patch went, why it failed, or null when cancelled.</summary>
    public static string? Save(string suggestedName, Action<TextWriter> write)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Create patch",
            FileName = string.Concat(suggestedName.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c)),
            Filter = "Patch file (*.patch)|*.patch|Diff file (*.diff)|*.diff|All files (*.*)|*.*",
            DefaultExt = ".patch",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        try
        {
            using (var writer = new StreamWriter(dialog.FileName, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n" })
            {
                write(writer);
            }

            return $"Saved the patch to {dialog.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not save the patch: {ex.Message}";
        }
    }
}
