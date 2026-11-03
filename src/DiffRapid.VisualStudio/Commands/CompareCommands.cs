using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using DiffRapid.VisualStudio.UI;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace DiffRapid.VisualStudio.Commands;

internal static class CompareCommands
{
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
