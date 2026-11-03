using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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
    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        await CompareCommands.InitializeAsync(this);
    }

    /// <summary>Opens a new comparison document tab for two files.</summary>
    public async Task OpenComparisonAsync(string leftPath, string rightPath)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

        var shell = await GetServiceAsync(typeof(SVsUIShell)) as IVsUIShell
            ?? throw new InvalidOperationException("The Visual Studio shell service is unavailable.");
        var solution = await GetServiceAsync(typeof(SVsSolution)) as IVsHierarchy
            ?? throw new InvalidOperationException("The Visual Studio solution service is unavailable.");

        var document = new DiffDocumentPane();
        try
        {
            await document.LoadAsync(this, leftPath, rightPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not StepFailedException)
        {
            document.Dispose();
            throw new StepFailedException("Loading the comparison", ex);
        }

        var caption = Path.GetFileName(leftPath) + " ↔ " + Path.GetFileName(rightPath);
        var frame = Diagnostics.Step("Creating the comparison window", () => CreateDocumentWindow(shell, solution, document, caption));
        Diagnostics.Step("Showing the comparison window", () => ErrorHandler.ThrowOnFailure(frame.Show()));
    }

    private IVsWindowFrame CreateDocumentWindow(IVsUIShell shell, IVsHierarchy solution, DiffDocumentPane document, string caption)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // A unique moniker: each comparison is its own document, never saved, never in the MRU. The running
        // document table needs an owning hierarchy; the solution node works even when no solution is open.
        var moniker = "DiffRapid:" + Guid.NewGuid().ToString("N") + ":" + caption;
        const uint flags = (uint)(_VSRDTFLAGS.RDT_DontAddToMRU | _VSRDTFLAGS.RDT_CantSave | _VSRDTFLAGS.RDT_DontSaveAs);

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
