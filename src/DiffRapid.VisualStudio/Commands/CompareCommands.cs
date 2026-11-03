using System;
using System.ComponentModel.Design;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.Win32;
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
        var left = PickFile("Select the left file");
        if (left is null)
        {
            return;
        }

        var right = PickFile("Select the right file", Path.GetDirectoryName(left));
        if (right is null)
        {
            return;
        }

        await package.OpenComparisonAsync(left, right);
    }

    private static string? PickFile(string title, string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            CheckFileExists = true,
            Filter = "All files (*.*)|*.*",
            InitialDirectory = initialDirectory ?? string.Empty,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
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
                ShowMessage(package, ex.Message);
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
