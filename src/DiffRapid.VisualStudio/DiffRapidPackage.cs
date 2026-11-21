using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DiffRapid.Core.Plugins;
using DiffRapid.VisualStudio.Commands;
using DiffRapid.VisualStudio.Options;
using DiffRapid.VisualStudio.UI;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using static DiffRapid.VisualStudio.UI.UiText;
using Task = System.Threading.Tasks.Task;

namespace DiffRapid.VisualStudio;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageIds.PackageGuidString)]
[ProvideMenuResource("Menus.ctmenu", 3)]
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
        await LocalHistoryRecorder.StartAsync(this);

        // Temporary copies left by comparisons of earlier sessions (closed by a crash, or still locked then).
        _ = Task.Run(() => TemporaryFiles.Sweep(DisposalToken), DisposalToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && ThreadHelper.CheckAccess())
        {
            LocalHistoryRecorder.Stop();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Opens a new comparison document tab for two files. Images open in the image view, two CSV or TSV files or
    /// two Excel workbooks in the table view, and other binary files (except .NET assemblies, compared by their API) in the hex view;
    /// everything else opens as text (see <see cref="OpenTextComparisonAsync"/>).
    /// </summary>
    public async Task OpenComparisonAsync(string leftPath, string rightPath)
    {
        if (IsImageFile(leftPath) && IsImageFile(rightPath))
        {
            await OpenImageComparisonAsync(leftPath, rightPath);
            return;
        }

        if ((IsTableFile(leftPath) && IsTableFile(rightPath)) || (TableDiffControl.IsWorkbook(leftPath) && TableDiffControl.IsWorkbook(rightPath)))
        {
            await OpenDocumentAsync(
                "Loading the table comparison",
                () => Task.FromResult<FrameworkElement>(new TableDiffControl(this, leftPath, rightPath)),
                Path.GetFileName(leftPath) + " ↔ " + Path.GetFileName(rightPath),
                hostsEditors: false,
                files: new[] { leftPath, rightPath });
            return;
        }

        // An assembly's API is read once here, to tell it from a native binary, and reused by the text comparison.
        var (hex, leftApi, rightApi) = await Task.Run(() =>
        {
            var left = BinaryApiText(leftPath, out var leftHex);
            if (leftHex)
            {
                return (true, null, null);
            }

            var right = BinaryApiText(rightPath, out var rightHex);
            return (rightHex, left, right);
        });
        if (hex)
        {
            await OpenDocumentAsync(
                "Loading the hex comparison",
                () => Task.FromResult<FrameworkElement>(new HexDiffControl(this, leftPath, rightPath)),
                Path.GetFileName(leftPath) + " ↔ " + Path.GetFileName(rightPath),
                hostsEditors: false,
                files: new[] { leftPath, rightPath });
            return;
        }

        await OpenTextComparisonAsync(leftPath, rightPath, leftApi, rightApi);
    }

    private static bool IsTableFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) || extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase);
    }

    // For a binary file: the API text of a .NET assembly or package, or null with hex set when it has no text form
    // (not a document or an assembly). Null without hex for other files.
    private static string? BinaryApiText(string path, out bool hex)
    {
        var binary = IsBinaryFile(path);
        var api = binary ? AssemblyApiText(path) : null;
        hex = binary && api is null;
        return api;
    }

    /// <summary>
    /// Opens a new text comparison document tab for two files and returns its window. Word, Excel and PowerPoint
    /// files are compared as their text, through temporary copies shown read-only.
    /// </summary>
    public Task<IVsWindowFrame> OpenTextComparisonAsync(string leftPath, string rightPath) =>
        OpenTextComparisonAsync(leftPath, rightPath, null, null);

    /// <param name="leftApi">The left file's API text when already read; null reads it when the file is an assembly.</param>
    /// <param name="rightApi">The right file's API text; see <paramref name="leftApi"/>.</param>
    private Task<IVsWindowFrame> OpenTextComparisonAsync(string leftPath, string rightPath, string? leftApi, string? rightApi) =>
        OpenDocumentAsync(
            "Loading the comparison",
            async () =>
            {
                string? left = null, right = null;
                try
                {
                    left = await Task.Run(() => AsComparableText(leftPath, leftApi));
                    right = await Task.Run(() => AsComparableText(rightPath, rightApi));
                    var control = await TextDiffControl.CreateAsync(this, left, right, (await DiffRapidSettings.ReadAsync(this)).Compare);
                    control.LeftLabel = SourceLabel(leftPath, left);
                    control.RightLabel = SourceLabel(rightPath, right);
                    control.MarkExtracted(left: control.LeftLabel is not null, right: control.RightLabel is not null);
                    return control;
                }
                catch
                {
                    // The extracted copies belong to this view only; copies another tab holds are kept.
                    TemporaryFiles.DeleteUnused(new[] { left, right });
                    throw;
                }
            },
            Path.GetFileName(leftPath) + " ↔ " + Path.GetFileName(rightPath),
            hostsEditors: true,
            files: new[] { leftPath, rightPath });

    /// <summary>
    /// Runs an unpacker and prediffer pipeline on two files' content and opens the resulting texts side by side,
    /// read-only. The content is passed in so a text comparison can include its unsaved edits.
    /// </summary>
    public async Task<IVsWindowFrame> OpenPluginComparisonAsync(string leftPath, byte[] leftContent, string rightPath, byte[] rightContent, PluginOptions plugins)
    {
        // The pipelines run first, in the background, because the tab is named after the steps that actually ran.
        (string Path, string Steps) left = default, right = default;
        try
        {
            left = await Task.Run(() => PreparedCopy(leftPath, leftContent, plugins));
            right = await Task.Run(() => PreparedCopy(rightPath, rightContent, plugins));
        }
        catch (Exception ex)
        {
            // A copy made before the other side failed has no tab to remove it.
            TemporaryFiles.DeleteUnused(new[] { left.Path, right.Path });
            if (ex is InvalidDataException or PluginException or IOException or ArgumentException)
            {
                throw new StepFailedException("Running the plugins", ex);
            }

            throw;
        }

        var steps = left.Steps == right.Steps ? left.Steps : left.Steps + " / " + right.Steps;
        return await OpenDocumentAsync(
            "Loading the comparison",
            async () =>
            {
                var control = await TextDiffControl.CreateAsync(this, left.Path, right.Path, (await DiffRapidSettings.ReadAsync(this)).Compare);
                control.LeftLabel = SourceLabel(leftPath, left.Path);
                control.RightLabel = SourceLabel(rightPath, right.Path);
                control.MarkExtracted(left: true, right: true);
                return control;
            },
            Path.GetFileName(leftPath) + " ↔ " + Path.GetFileName(rightPath) + " (" + steps + ")",
            hostsEditors: true,
            files: new[] { left.Path, right.Path });
    }

    /// <summary>
    /// Writes what the pipelines make of a file to a temporary copy named after the steps that ran, such as
    /// "A.json (PrettifyJson sort + SortLines)", and returns its path and that name of the steps.
    /// </summary>
    private static (string Path, string Steps) PreparedCopy(string path, byte[] content, PluginOptions plugins)
    {
        PreparedFile prepared;
        try
        {
            prepared = PluginPipeline.Prepare(content, path, plugins);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }

        if (prepared.IsBinary)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is not text and no unpacker could read it. Choose an unpacker, such as 'auto'.");
        }

        // "auto" names the unpacker it picked, or nothing when the file was read as text. File names cannot hold "|".
        var unpacker = string.Equals(plugins.Unpacker?.Trim(), PluginOptions.AutomaticUnpacker, StringComparison.OrdinalIgnoreCase)
            ? prepared.UnpackerName
            : plugins.Unpacker;
        var steps = string.Join(" + ", new[] { unpacker, plugins.Prediffer }
            .Where(step => !string.IsNullOrWhiteSpace(step))
            .SelectMany(step => step!.Split('|'))
            .Select(step => step.Trim()));
        if (steps.Length == 0)
        {
            steps = "as text";
        }

        // Unpacked text is no longer in the file's own language; transformed text still is, so it keeps its colors.
        var extension = prepared.UnpackerName is null ? Path.GetExtension(path) : ".txt";
        var copy = EditorSelection.WriteTemporary(prepared.CompareText, Path.GetFileName(path) + " (" + steps + ")", extension);
        return (copy, steps);
    }

    /// <summary>
    /// For a temporary copy named after its source ("A.nupkg (public API).cs"), the source path with what the copy
    /// holds: "C:\pkg\A.nupkg (public API)". Null when the file is compared as it is.
    /// </summary>
    private static string? SourceLabel(string source, string comparable)
    {
        if (string.Equals(source, comparable, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var copyName = Path.GetFileNameWithoutExtension(comparable);
        var sourceName = Path.GetFileName(source);
        return copyName.StartsWith(sourceName, StringComparison.Ordinal) ? source + copyName.Substring(sourceName.Length) : source;
    }

    /// <summary>
    /// Whether a file should open in the hex comparison: a NUL byte in its first 8000 bytes. Word, Excel and
    /// PowerPoint files are compared as text (see <see cref="AsComparableText"/>), and so are files starting
    /// with a UTF-16 or UTF-32 byte order mark, whose text has NULs too. A file that cannot be read counts as
    /// text, so the text comparison reports the error.
    /// </summary>
    private static bool IsBinaryFile(string path)
    {
        if (DiffRapid.Documents.DocumentUnpackers.Find(path) is not null || !File.Exists(path))
        {
            return false;
        }

        var buffer = new byte[8000];
        int read;
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            read = stream.Read(buffer, 0, buffer.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return LooksBinary(buffer, read);
    }

    /// <summary>Opens the picture comparison for two image files; decoding and comparing run in the background.</summary>
    private Task OpenImageComparisonAsync(string leftPath, string rightPath) =>
        OpenDocumentAsync(
            "Loading the image comparison",
            () => Task.FromResult<FrameworkElement>(new ImageDiffControl(this, leftPath, rightPath)),
            Path.GetFileName(leftPath) + " ↔ " + Path.GetFileName(rightPath),
            hostsEditors: false,
            files: new[] { leftPath, rightPath });

    /// <summary>True for the image formats the image comparison decodes (WebP only where Windows has the codec).</summary>
    private static bool IsImageFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".ico" or ".webp";

    private static string AsComparableText(string path, string? knownApi)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        if ((knownApi ?? AssemblyApiText(path)) is { } api)
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

            // Plain lines, as in the API text, so ignoring comments never hides a version or dependency change;
            // and "\n" throughout, because mixed line endings make the editor normalize one side.
            var text = new System.Text.StringBuilder();
            foreach (var property in package.ToProperties())
            {
                text.Append(property.Key).Append(": ").Append(property.Value).Append('\n');
            }

            foreach (var library in package.Libraries)
            {
                text.Append("\n== ").Append(library.Path).Append('\n').Append(library.Api.ToText());
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
            FolderName(leftPath) + " ↔ " + FolderName(rightPath) + " · 3-way",
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

    /// <param name="hostsEditors">
    /// True when the view is made of text editors: the window then uses the DiffRapid key binding scope,
    /// which inherits the text editor's bindings so arrows, Enter, Backspace and the rest reach them.
    /// </param>
    /// <param name="files">The compared files; those that are temporary copies are removed when the last tab showing them closes.</param>
    private async Task<IVsWindowFrame> OpenDocumentAsync(string loadStep, Func<Task<FrameworkElement>> createView, string caption, bool hostsEditors, IReadOnlyList<string>? files = null)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

        var shell = await GetServiceAsync(typeof(SVsUIShell)) as IVsUIShell
            ?? throw new InvalidOperationException("The Visual Studio shell service is unavailable.");

        // The hold on the temporary copies among the files is taken before the view loads, so closing another tab on
        // the same copies meanwhile cannot delete them; the pane releases it when it closes. When the view fails to
        // load, the copies written for it are removed now rather than by the next day's sweep.
        files ??= Array.Empty<string>();
        TemporaryFiles.Acquire(files);
        FrameworkElement view;
        try
        {
            view = await createView();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not StepFailedException)
        {
            TemporaryFiles.Release(files);
            throw new StepFailedException(loadStep, ex);
        }
        catch
        {
            TemporaryFiles.Release(files);
            throw;
        }

        var document = new DiffDocumentPane(view, files);
        try
        {
            var frame = Diagnostics.Step("Creating the comparison window", () => CreateDocumentWindow(shell, document, caption, hostsEditors));
            Diagnostics.Step("Showing the comparison window", () => ErrorHandler.ThrowOnFailure(frame.Show()));
            return frame;
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
