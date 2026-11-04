using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DiffRapid.VisualStudio.Commands;
using DiffRapid.VisualStudio.Options;
using DiffRapid.VisualStudio.UI;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace DiffRapid.VisualStudio;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageIds.PackageGuidString)]
[ProvideMenuResource("Menus.ctmenu", 2)]
[ProvideSettingsManifest(PackageRelativeManifestFile = @"UnifiedSettings\registration.json")]
[ProvideKeyBindingTable(PackageIds.DiffWindowGuidString, PackageIds.KeyBindingScopeNameResource, AllowNavKeyBinding = true)]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideAutoLoad(VSConstants.UICONTEXT.FolderOpened_string, PackageAutoLoadFlags.BackgroundLoad)]

// Also without a solution (a file opened on its own), so the editor menu can decide which DiffRapid items to show.
[ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
public sealed class DiffRapidPackage : AsyncPackage
{
    private static int _comparisonCount;
    private readonly ComparisonHierarchy _hierarchy = new();

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        await CompareCommands.InitializeAsync(this);
    }

    /// <summary>Opens a new comparison document tab for two files.</summary>
    public Task OpenComparisonAsync(string leftPath, string rightPath) =>
        OpenDocumentAsync(
            "Loading the comparison",
            async () => await TextDiffControl.CreateAsync(this, leftPath, rightPath, (await DiffRapidSettings.ReadAsync(this)).Compare),
            Path.GetFileName(leftPath) + " ↔ " + Path.GetFileName(rightPath),
            hostsEditors: true);

    /// <summary>Opens a new folder comparison document tab; the scan runs in the background.</summary>
    public Task OpenFolderComparisonAsync(string leftPath, string rightPath) =>
        OpenDocumentAsync(
            "Loading the folder comparison",
            async () => new FolderDiffControl(this, leftPath, rightPath, (await DiffRapidSettings.ReadAsync(this)).Folder),
            FolderName(leftPath) + " ↔ " + FolderName(rightPath),
            hostsEditors: false);

    /// <summary>Opens a three-way merge of two changed versions of a file and their common base.</summary>
    public Task OpenMergeAsync(string leftPath, string basePath, string rightPath, string resultPath) =>
        OpenDocumentAsync(
            "Loading the merge",
            async () => await MergeControl.CreateAsync(this, leftPath, basePath, rightPath, resultPath, (await DiffRapidSettings.ReadAsync(this)).Compare),
            Path.GetFileName(resultPath) + " (merge)",
            hostsEditors: true);

    /// <summary>Opens a preview of a patch applied under a folder (or to one file); nothing is written until the user applies it.</summary>
    public Task OpenApplyPatchAsync(string patchPath, string targetPath) =>
        OpenDocumentAsync(
            "Loading the patch",
            () => Task.FromResult<FrameworkElement>(new ApplyPatchControl(this, patchPath, targetPath)),
            Path.GetFileName(patchPath) + " → " + FolderName(targetPath),
            hostsEditors: false);

    private static string FolderName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    /// <param name="hostsEditors">
    /// True when the view is made of text editors: the window then uses the DiffRapid key binding scope,
    /// which inherits the text editor's bindings so arrows, Enter, Backspace and the rest reach them.
    /// </param>
    private async Task OpenDocumentAsync(string loadStep, Func<Task<FrameworkElement>> createView, string caption, bool hostsEditors)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

        var shell = await GetServiceAsync(typeof(SVsUIShell)) as IVsUIShell
            ?? throw new InvalidOperationException("The Visual Studio shell service is unavailable.");

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
            var frame = Diagnostics.Step("Creating the comparison window", () => CreateDocumentWindow(shell, document, caption, hostsEditors));
            Diagnostics.Step("Showing the comparison window", () => ErrorHandler.ThrowOnFailure(frame.Show()));
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private IVsWindowFrame CreateDocumentWindow(IVsUIShell shell, DiffDocumentPane document, string caption, bool hostsEditors)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // A unique moniker per comparison (it is also the name in the "save changes?" prompt). Saving the
        // tab saves the compared files; it never appears in the MRU. Each tab is its own item of the
        // DiffRapid hierarchy, which sends saves back to the tab.
        var number = Interlocked.Increment(ref _comparisonCount);
        var moniker = "DiffRapid: " + caption + (number > 1 ? $" ({number})" : string.Empty);
        const uint flags = (uint)(_VSRDTFLAGS.RDT_DontAddToMRU | _VSRDTFLAGS.RDT_DontSaveAs);

        var unknown = Marshal.GetIUnknownForObject(document);
        try
        {
            var editorType = new Guid(PackageIds.DiffWindowGuidString);
            var commandUi = hostsEditors ? editorType : Guid.Empty;
            ErrorHandler.ThrowOnFailure(shell.CreateDocumentWindow(
                flags,
                moniker,
                _hierarchy,
                (uint)number,
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

            if (hostsEditors)
            {
                var textEditor = VSConstants.GUID_TextEditorFactory;
                ErrorHandler.ThrowOnFailure(frame.SetGuidProperty((int)__VSFPROPID.VSFPROPID_InheritKeyBindings, ref textEditor));
            }

            return frame;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
