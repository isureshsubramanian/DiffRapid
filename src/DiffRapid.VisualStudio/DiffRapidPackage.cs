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

    /// <summary>
    /// Opens a new comparison document tab for two files. Two CSV or TSV files open in the table view;
    /// everything else opens as text (see <see cref="OpenTextComparisonAsync"/>).
    /// </summary>
    public Task OpenComparisonAsync(string leftPath, string rightPath)
    {
        if (IsImageFile(leftPath) && IsImageFile(rightPath))
        {
            return OpenImageComparisonAsync(leftPath, rightPath);
        }

        if (IsTableFile(leftPath) && IsTableFile(rightPath))
        {
            return OpenDocumentAsync(
                "Loading the table comparison",
                () => Task.FromResult<FrameworkElement>(new TableDiffControl(this, leftPath, rightPath)),
                Path.GetFileName(leftPath) + " ↔ " + Path.GetFileName(rightPath),
                hostsEditors: false);
        }

        return OpenTextComparisonAsync(leftPath, rightPath);
    }

    private static bool IsTableFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) || extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Opens a new text comparison document tab for two files. Word, Excel and PowerPoint files are compared as
    /// their text, through temporary copies (editing those does not change the documents).
    /// </summary>
    public Task OpenTextComparisonAsync(string leftPath, string rightPath) =>
        OpenDocumentAsync(
            "Loading the comparison",
            async () =>
            {
                var left = await Task.Run(() => AsComparableText(leftPath));
                var right = await Task.Run(() => AsComparableText(rightPath));
                return await TextDiffControl.CreateAsync(this, left, right, (await DiffRapidSettings.ReadAsync(this)).Compare);
            },
            Path.GetFileName(leftPath) + " ↔ " + Path.GetFileName(rightPath),
            hostsEditors: true);

    /// <summary>Opens the picture comparison for two image files; decoding and comparing run in the background.</summary>
    private Task OpenImageComparisonAsync(string leftPath, string rightPath) =>
        OpenDocumentAsync(
            "Loading the image comparison",
            () => Task.FromResult<FrameworkElement>(new ImageDiffControl(this, leftPath, rightPath)),
            Path.GetFileName(leftPath) + " ↔ " + Path.GetFileName(rightPath),
            hostsEditors: false);

    /// <summary>True for the image formats the image comparison decodes (WebP only where Windows has the codec).</summary>
    private static bool IsImageFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".ico" or ".webp";

    private static string AsComparableText(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        if (AssemblyApiText(path) is { } api)
        {
            return EditorSelection.WriteTemporary(api, Path.GetFileName(path) + " (public API)", ".cs");
        }

        if (DiffRapid.Documents.DocumentUnpackers.Find(path) is null)
        {
            return path;
        }

        if (!DiffRapid.Documents.DocumentText.TryExtract(path, out var text))
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} could not be read as a document; it may be damaged or protected.");
        }

        return EditorSelection.WriteTemporary(text, Path.GetFileName(path) + " (text)", ".txt");
    }

    /// <summary>
    /// The public API of a .NET assembly or NuGet package as sorted C#-like declarations, so two builds compare by
    /// what changed for callers. Null for other files and for native binaries, which then compare as bytes.
    /// </summary>
    private static string? AssemblyApiText(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (extension is ".dll" or ".exe" or ".winmd")
            {
                return DiffRapid.Core.Assemblies.ApiReader.Read(path).ToText();
            }

            if (extension != ".nupkg")
            {
                return null;
            }

            var package = DiffRapid.Core.Assemblies.NuGetPackageReader.Read(path);
            var text = new System.Text.StringBuilder();
            foreach (var property in package.ToProperties())
            {
                text.Append("// ").Append(property.Key).Append(": ").AppendLine(property.Value);
            }

            foreach (var library in package.Libraries)
            {
                text.AppendLine().Append("// == ").AppendLine(library.Path).Append(library.Api.ToText());
            }

            return text.ToString();
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Opens a new folder comparison document tab; the scan runs in the background.</summary>
    public Task OpenFolderComparisonAsync(string leftPath, string rightPath) =>
        OpenDocumentAsync(
            "Loading the folder comparison",
            async () => new FolderDiffControl(this, leftPath, rightPath, (await DiffRapidSettings.ReadAsync(this)).Folder),
            FolderName(leftPath) + " ↔ " + FolderName(rightPath),
            hostsEditors: false);

    /// <summary>Opens a three-way comparison of two folders and the folder both started from.</summary>
    public Task OpenThreeWayFolderComparisonAsync(string basePath, string leftPath, string rightPath) =>
        OpenDocumentAsync(
            "Loading the folder comparison",
            async () => new ThreeWayFolderControl(this, basePath, leftPath, rightPath, (await DiffRapidSettings.ReadAsync(this)).Folder),
            FolderName(leftPath) + " ↔ " + FolderName(rightPath) + " (base " + FolderName(basePath) + ")",
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
