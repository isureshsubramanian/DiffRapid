using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using DiffRapid.Core.VersionControl;
using DiffRapid.VisualStudio.UI;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace DiffRapid.VisualStudio.Commands;

internal static class CompareCommands
{
    // The text chosen with "Select for Compare", waiting for the other side.
    private static EditorSelection? _selectedForCompare;

    public static async Task InitializeAsync(DiffRapidPackage package)
    {
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService
            ?? throw new InvalidOperationException("Menu command service is unavailable.");

        var compareSelected = new OleMenuCommand(
            (_, _) => Run(package, () => CompareSelectedAsync(package)),
            new CommandID(PackageIds.CommandSet, PackageIds.CompareSelected));
        compareSelected.BeforeQueryStatus += (_, _) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            compareSelected.Visible = SolutionSelection.GetSelectedFilePaths().Count == 2;
        };
        commandService.AddCommand(compareSelected);

        commandService.AddCommand(new OleMenuCommand(
            (_, _) => Run(package, () => CompareFilesAsync(package)),
            new CommandID(PackageIds.CommandSet, PackageIds.CompareFiles)));

        commandService.AddCommand(new OleMenuCommand(
            (_, _) => Run(package, () => CompareFoldersAsync(package)),
            new CommandID(PackageIds.CommandSet, PackageIds.CompareFolders)));

        commandService.AddCommand(new OleMenuCommand(
            (_, _) => Run(package, () => MergeFilesAsync(package)),
            new CommandID(PackageIds.CommandSet, PackageIds.MergeFiles)));

        var compareWithClipboard = new OleMenuCommand(
            (_, _) => Run(package, () => CompareWithClipboardAsync(package)),
            new CommandID(PackageIds.CommandSet, PackageIds.CompareWithClipboard));
        compareWithClipboard.BeforeQueryStatus += (_, _) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var selection = EditorSelection.FromActiveEditor();
            compareWithClipboard.Visible = selection is not null;
            compareWithClipboard.Text = selection is { IsWholeDocument: false }
                ? "Compare Selection with Clipboard (DiffRapid)"
                : "Compare with Clipboard (DiffRapid)";
        };
        commandService.AddCommand(compareWithClipboard);

        var selectForCompare = new OleMenuCommand(
            (_, _) => Run(package, () => SelectForCompareAsync(package)),
            new CommandID(PackageIds.CommandSet, PackageIds.SelectForCompare));
        selectForCompare.BeforeQueryStatus += (_, _) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var selection = EditorSelection.FromActiveEditor();
            selectForCompare.Visible = selection is not null;
            selectForCompare.Text = selection is { IsWholeDocument: false }
                ? "Select for Compare (DiffRapid)"
                : "Select Document for Compare (DiffRapid)";
        };
        commandService.AddCommand(selectForCompare);

        var compareWithSelected = new OleMenuCommand(
            (_, _) => Run(package, () => CompareWithSelectedAsync(package)),
            new CommandID(PackageIds.CommandSet, PackageIds.CompareWithSelected));
        compareWithSelected.BeforeQueryStatus += (_, _) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            compareWithSelected.Visible = _selectedForCompare is not null && EditorSelection.HasActiveEditor();
            compareWithSelected.Text = _selectedForCompare is { } selected
                ? $"Compare with {selected.DisplayName} (DiffRapid)"
                : "Compare with Selected (DiffRapid)";
        };
        commandService.AddCommand(compareWithSelected);

        var compareWithVersion = new OleMenuCommand(
            (_, _) => Run(package, () => CompareWithVersionAsync(package, SolutionSelection.GetSelectedFilePaths()[0])),
            new CommandID(PackageIds.CommandSet, PackageIds.CompareWithVersion));
        compareWithVersion.BeforeQueryStatus += (_, _) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            compareWithVersion.Visible = SolutionSelection.GetSelectedFilePaths().Count == 1;
        };
        commandService.AddCommand(compareWithVersion);

        var compareDocumentWithVersion = new OleMenuCommand(
            (_, _) => Run(package, () => CompareDocumentWithVersionAsync(package)),
            new CommandID(PackageIds.CommandSet, PackageIds.CompareDocumentWithVersion));
        compareDocumentWithVersion.BeforeQueryStatus += (_, _) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            compareDocumentWithVersion.Visible = EditorSelection.HasActiveEditor();
        };
        commandService.AddCommand(compareDocumentWithVersion);

        var compareWithSaved = new OleMenuCommand(
            (_, _) => Run(package, () => CompareWithSavedAsync(package)),
            new CommandID(PackageIds.CommandSet, PackageIds.CompareWithSaved));
        compareWithSaved.BeforeQueryStatus += (_, _) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            compareWithSaved.Visible = EditorSelection.ActiveDocumentWithUnsavedChanges() is not null;
        };
        commandService.AddCommand(compareWithSaved);

        commandService.AddCommand(new OleMenuCommand(
            (_, _) => Run(package, () => ApplyPatchAsync(package, null)),
            new CommandID(PackageIds.CommandSet, PackageIds.ApplyPatch)));

        var applySelectedPatch = new OleMenuCommand(
            (_, _) => Run(package, () => ApplyPatchAsync(package, SolutionSelection.GetSelectedFilePaths()[0])),
            new CommandID(PackageIds.CommandSet, PackageIds.ApplySelectedPatch));
        applySelectedPatch.BeforeQueryStatus += (_, _) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var paths = SolutionSelection.GetSelectedFilePaths();
            applySelectedPatch.Visible = paths.Count == 1 && IsPatchFile(paths[0]);
        };
        commandService.AddCommand(applySelectedPatch);
    }

    /// <summary>
    /// Compares the file as saved on disk (left, a temporary copy with its own bytes) with the open document and its
    /// unsaved edits (right, still editable).
    /// </summary>
    private static async Task CompareWithSavedAsync(DiffRapidPackage package)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var path = EditorSelection.ActiveDocumentWithUnsavedChanges();
        if (path is null)
        {
            ShowMessage(package, "The document has no unsaved changes.");
            return;
        }

        var saved = EditorSelection.WriteTemporary(System.IO.File.ReadAllBytes(path), System.IO.Path.GetFileNameWithoutExtension(path) + " (saved)", System.IO.Path.GetExtension(path));
        await package.OpenComparisonAsync(saved, path);
    }

    private static bool IsPatchFile(string path) =>
        path.EndsWith(".patch", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".diff", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Asks for a patch and where to apply it, then opens the preview. A patch chosen in Solution Explorer
    /// starts out applied to its repository root (patch paths are usually repository-relative), else its folder.
    /// </summary>
    private static async Task ApplyPatchAsync(DiffRapidPackage package, string? patchPath)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        string? target = null;
        if (patchPath is not null)
        {
            target = await Task.Run(() => DiffRapid.Core.Git.GitRepository.Find(patchPath)?.Root ?? System.IO.Path.GetDirectoryName(patchPath));
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        }

        var dialog = new SelectFilesDialog(ComparisonTarget.ApplyPatch, patchPath, target);
        if (dialog.ShowModal() != true)
        {
            return;
        }

        await package.OpenApplyPatchAsync(dialog.LeftPath, dialog.RightPath);
    }

    private static async Task CompareSelectedAsync(DiffRapidPackage package)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var paths = SolutionSelection.GetSelectedFilePaths();
        if (paths.Count != 2)
        {
            ShowMessage(package, "Select exactly two files in Solution Explorer to compare.");
            return;
        }

        await package.OpenComparisonAsync(paths[0], paths[1]);
    }

    private static async Task CompareFilesAsync(DiffRapidPackage package)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();

        // Pre-fill from the Solution Explorer selection so a selected file lands on the left.
        var selected = SolutionSelection.GetSelectedFilePaths();
        var dialog = new SelectFilesDialog(
            ComparisonTarget.Files,
            selected.Count > 0 ? selected[0] : null,
            selected.Count > 1 ? selected[1] : null);
        if (dialog.ShowModal() != true)
        {
            return;
        }

        await package.OpenComparisonAsync(dialog.LeftPath, dialog.RightPath);
    }

    private static async Task CompareFoldersAsync(DiffRapidPackage package)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dialog = new SelectFilesDialog(ComparisonTarget.Folders);
        if (dialog.ShowModal() != true)
        {
            return;
        }

        if (dialog.BasePath.Length > 0)
        {
            await package.OpenThreeWayFolderComparisonAsync(dialog.BasePath, dialog.LeftPath, dialog.RightPath);
            return;
        }

        await package.OpenFolderComparisonAsync(dialog.LeftPath, dialog.RightPath);
    }

    private static async Task MergeFilesAsync(DiffRapidPackage package)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dialog = new SelectFilesDialog(ComparisonTarget.Merge);
        if (dialog.ShowModal() != true)
        {
            return;
        }

        await package.OpenMergeAsync(dialog.LeftPath, dialog.BasePath, dialog.RightPath, dialog.ResultPath);
    }

    /// <summary>Compares the clipboard (left) with the editor's selection, or its document when nothing is selected.</summary>
    private static async Task CompareWithClipboardAsync(DiffRapidPackage package)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var selection = EditorSelection.FromActiveEditor();
        if (selection is null)
        {
            ShowMessage(package, "Open a document in the editor to compare it with the clipboard.");
            return;
        }

        var clipboard = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : string.Empty;
        if (clipboard.Length == 0)
        {
            ShowMessage(package, "The clipboard has no text to compare.");
            return;
        }

        var left = EditorSelection.WriteTemporary(clipboard, "Clipboard", selection.Extension);
        await package.OpenComparisonAsync(left, selection.ToComparisonFile());
    }

    private static async Task SelectForCompareAsync(DiffRapidPackage package)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        _selectedForCompare = EditorSelection.FromActiveEditor();
        if (_selectedForCompare is not null
            && await package.GetServiceAsync(typeof(Microsoft.VisualStudio.Shell.Interop.SVsStatusbar)) is Microsoft.VisualStudio.Shell.Interop.IVsStatusbar statusBar)
        {
            statusBar.SetText($"DiffRapid: {_selectedForCompare.DisplayName} selected. Right-click other text and choose Compare with {_selectedForCompare.DisplayName}.");
        }
    }

    /// <summary>Compares the text chosen with Select for Compare (left) with the current selection or document.</summary>
    private static async Task CompareWithSelectedAsync(DiffRapidPackage package)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var selected = _selectedForCompare;
        var current = EditorSelection.FromActiveEditor();
        if (selected is null || current is null)
        {
            ShowMessage(package, "First right-click some text and choose Select for Compare, then right-click the other text.");
            return;
        }

        _selectedForCompare = null;
        await package.OpenComparisonAsync(selected.ToComparisonFile(), current.ToComparisonFile());
    }

    private static async Task CompareDocumentWithVersionAsync(DiffRapidPackage package)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var path = EditorSelection.FromActiveEditor()?.DocumentPath;
        if (path is null || !System.IO.File.Exists(path))
        {
            ShowMessage(package, "Save the document to a file first: only files have earlier versions.");
            return;
        }

        await CompareWithVersionAsync(package, path);
    }

    /// <summary>
    /// Lets the user pick a version of the file from git or TFVC (Azure DevOps), and compares it (left) with the
    /// working copy (right). The version is written to a temporary file with its own bytes and encoding.
    /// </summary>
    private static async Task CompareWithVersionAsync(DiffRapidPackage package, string path)
    {
        var cancellation = package.DisposalToken;

        // Finding a TFVC workspace runs tf.exe, so the lookup and the history stay off the UI thread.
        var versionControl = await Task.Run(() => VersionControl.Find(path), cancellation);
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation);
        if (versionControl is null)
        {
            ShowMessage(package, $"{System.IO.Path.GetFileName(path)} is not in a git repository or a TFVC (Azure DevOps) workspace.");
            return;
        }

        var provider = versionControl.Kind == "tfvc" ? "TFVC" : "git";
        var history = await Task.Run(() => versionControl.GetHistoryAsync(path, 200, cancellation), cancellation);
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation);
        if (history.Count == 0)
        {
            ShowMessage(package, $"{System.IO.Path.GetFileName(path)} has no history in {provider} yet.");
            return;
        }

        var dialog = new VersionPickerDialog(System.IO.Path.GetFileName(path), provider, history);
        if (dialog.ShowModal() != true || dialog.SelectedVersion is not { } version)
        {
            return;
        }

        var content = await Task.Run(() => versionControl.ReadFileAsync(version.Id, version.PathAtVersion, cancellation), cancellation);
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellation);
        if (content is null)
        {
            ShowMessage(package, $"{System.IO.Path.GetFileName(path)} does not exist in version {version.ShortId}.");
            return;
        }

        var name = System.IO.Path.GetFileNameWithoutExtension(path) + "@" + version.ShortId;
        var earlier = EditorSelection.WriteTemporary(content, name, System.IO.Path.GetExtension(path));
        await package.OpenComparisonAsync(earlier, path);
    }

    private static void Run(DiffRapidPackage package, Func<Task> action)
    {
        package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await action();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                Diagnostics.LogError(ex);
                ShowMessage(package, ex.Message + "\n\nDetails are in the Output window (DiffRapid pane) and in " + Diagnostics.LogFilePath);
            }
        }).FileAndForget("DiffRapid/Command");
    }

    private static void ShowMessage(DiffRapidPackage package, string message)
    {
        VsShellUtilities.ShowMessageBox(
            package,
            message,
            "DiffRapid",
            Microsoft.VisualStudio.Shell.Interop.OLEMSGICON.OLEMSGICON_INFO,
            Microsoft.VisualStudio.Shell.Interop.OLEMSGBUTTON.OLEMSGBUTTON_OK,
            Microsoft.VisualStudio.Shell.Interop.OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }
}
