using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DiffRapid.VisualStudio.Commands;
using DiffRapid.VisualStudio.UI;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace DiffRapid.VisualStudio;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageIds.PackageGuidString)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideAutoLoad(VSConstants.UICONTEXT.FolderOpened_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideToolWindow(typeof(DiffToolWindow), Style = VsDockStyle.MDI, MultiInstances = true, Transient = true)]
public sealed class DiffRapidPackage : AsyncPackage
{
    private int _nextWindowId;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        await CompareCommands.InitializeAsync(this);
    }

    /// <summary>Opens a new comparison tab for two files.</summary>
    public async Task OpenComparisonAsync(string leftPath, string rightPath)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

        var id = Interlocked.Increment(ref _nextWindowId);
        var window = (DiffToolWindow)await FindToolWindowAsync(typeof(DiffToolWindow), id, create: true, DisposalToken);
        if (window?.Frame is not Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame frame)
        {
            throw new NotSupportedException("Could not create the DiffRapid comparison window.");
        }

        await window.LoadAsync(this, leftPath, rightPath);
        ErrorHandler.ThrowOnFailure(frame.Show());
    }
}
