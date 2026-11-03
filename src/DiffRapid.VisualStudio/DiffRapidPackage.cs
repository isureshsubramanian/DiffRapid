using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DiffRapid.VisualStudio.Commands;
using DiffRapid.VisualStudio.UI;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace DiffRapid.VisualStudio;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageIds.PackageGuidString)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideAutoLoad(VSConstants.UICONTEXT.FolderOpened_string, PackageAutoLoadFlags.BackgroundLoad)]
public sealed class DiffRapidPackage : AsyncPackage
{
    private static int _comparisonCount;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        await CompareCommands.InitializeAsync(this);
    }

    /// <summary>Opens a new comparison document tab for two files.</summary>
    public Task OpenComparisonAsync(string leftPath, string rightPath) =>
        OpenDocumentAsync(
            "Loading the comparison",
            async () => await TextDiffControl.CreateAsync(this, leftPath, rightPath),
            Path.GetFileName(leftPath) + " ↔ " + Path.GetFileName(rightPath));

    /// <summary>Opens a new folder comparison document tab; the scan runs in the background.</summary>
    public Task OpenFolderComparisonAsync(string leftPath, string rightPath) =>
        OpenDocumentAsync(
            "Loading the folder comparison",
            () => Task.FromResult<FrameworkElement>(new FolderDiffControl(this, leftPath, rightPath)),
            FolderName(leftPath) + " ↔ " + FolderName(rightPath));

    private static string FolderName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private async Task OpenDocumentAsync(string loadStep, Func<Task<FrameworkElement>> createView, string caption)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

        var shell = await GetServiceAsync(typeof(SVsUIShell)) as IVsUIShell
            ?? throw new InvalidOperationException("The Visual Studio shell service is unavailable.");
        var solution = await GetServiceAsync(typeof(SVsSolution)) as IVsHierarchy
            ?? throw new InvalidOperationException("The Visual Studio solution service is unavailable.");

        FrameworkElement view;
        try
        {
            view = await createView();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not StepFailedException)
        {
            throw new StepFailedException(loadStep, ex);
        }

        var document = new DiffDocumentPane(view);
        try
        {
            var frame = Diagnostics.Step("Creating the comparison window", () => CreateDocumentWindow(shell, solution, document, caption));
            Diagnostics.Step("Showing the comparison window", () => ErrorHandler.ThrowOnFailure(frame.Show()));
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private IVsWindowFrame CreateDocumentWindow(IVsUIShell shell, IVsHierarchy solution, DiffDocumentPane document, string caption)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // A unique moniker per comparison (it is also the name in the "save changes?" prompt). Saving the
        // tab saves the compared files; it never appears in the MRU. The running document table needs an
        // owning hierarchy; the solution node works even when no solution is open.
        var number = Interlocked.Increment(ref _comparisonCount);
        var moniker = "DiffRapid: " + caption + (number > 1 ? $" ({number})" : string.Empty);
        const uint flags = (uint)(_VSRDTFLAGS.RDT_DontAddToMRU | _VSRDTFLAGS.RDT_DontSaveAs);

        var unknown = Marshal.GetIUnknownForObject(document);
        try
        {
            var editorType = new Guid(PackageIds.DiffWindowGuidString);
            var commandUi = Guid.Empty;
            ErrorHandler.ThrowOnFailure(shell.CreateDocumentWindow(
                flags,
                moniker,
                (IVsUIHierarchy)solution,
                (uint)VSConstants.VSITEMID.Root,
                unknown,
                unknown,
                ref editorType,
                null,
                ref commandUi,
                null,
                caption,
                string.Empty,
                null,
                out var frame));
            return frame;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
