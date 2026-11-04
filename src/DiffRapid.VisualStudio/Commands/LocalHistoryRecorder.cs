using System;
using System.IO;
using System.Threading.Tasks;
using DiffRapid.Core.History;
using DiffRapid.VisualStudio.Options;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace DiffRapid.VisualStudio.Commands;

/// <summary>
/// Keeps the local history: listens to document saves and stores each saved version in
/// %LOCALAPPDATA%\DiffRapid\History. The first time a file is saved, the version on disk before the save is kept
/// too, so there is always something to go back to.
/// </summary>
internal sealed class LocalHistoryRecorder : IVsRunningDocTableEvents3
{
    private readonly DiffRapidPackage _package;
    private readonly RunningDocumentTable _documents;
    private readonly LocalHistoryStore _store;
    private bool _enabled;

    private LocalHistoryRecorder(DiffRapidPackage package, LocalHistoryStore store, bool enabled)
    {
        _package = package;
        _store = store;
        _enabled = enabled;
        _documents = new RunningDocumentTable(package);
    }

    /// <summary>Where the history is kept; the CLI's `history` command uses the same folder by default.</summary>
    public static string StoreFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiffRapid", "History");

    /// <summary>The history store, once <see cref="StartAsync"/> has run.</summary>
    public static LocalHistoryStore? Store { get; private set; }

    /// <summary>Opens the store, removes versions past the settings' limits in the background, and starts listening to saves.</summary>
    public static async Task StartAsync(DiffRapidPackage package)
    {
        var settings = await DiffRapidSettings.ReadAsync(package);
        var store = new LocalHistoryStore(StoreFolder, settings.History);
        Store = store;
        await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
        var recorder = new LocalHistoryRecorder(package, store, settings.KeepHistory);
        recorder._documents.Advise(recorder);

        _ = Task.Run(() => Guard("Pruning the local history", () => store.Prune(package.DisposalToken)));
    }

    public int OnBeforeSave(uint docCookie)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var path = FilePath(docCookie);
        if (path is null)
        {
            return VSConstants.S_OK;
        }

        // Only the first save of a file reads the old version here, on the UI thread, before it is overwritten;
        // later saves already have their previous version in the history.
        Guard("Keeping the version before the save", () =>
        {
            if (_store.List(path).Count == 0)
            {
                _store.Snapshot(path, "Before save");
            }
        });
        return VSConstants.S_OK;
    }

    public int OnAfterSave(uint docCookie)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var path = FilePath(docCookie);
        if (path is not null)
        {
            _ = Task.Run(() => Guard("Keeping the saved version", () => _store.Snapshot(path, "Saved")));
        }

        // Settings may have changed since the last save; read them again for the next one.
        _package.JoinableTaskFactory.RunAsync(async () => _enabled = (await DiffRapidSettings.ReadAsync(_package)).KeepHistory).FileAndForget("DiffRapid/LocalHistorySettings");
        return VSConstants.S_OK;
    }

    public int OnAfterAttributeChangeEx(uint docCookie, uint grfAttribs, IVsHierarchy pHierOld, uint itemidOld, string pszMkDocumentOld, IVsHierarchy pHierNew, uint itemidNew, string pszMkDocumentNew)
    {
        // A renamed file takes its history with it.
        if ((grfAttribs & (uint)__VSRDTATTRIB.RDTA_MkDocument) != 0 && File.Exists(pszMkDocumentNew) && !string.IsNullOrEmpty(pszMkDocumentOld))
        {
            _ = Task.Run(() => Guard("Moving the local history of a renamed file", () => _store.Rename(pszMkDocumentOld, pszMkDocumentNew)));
        }

        return VSConstants.S_OK;
    }

    public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;

    public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;

    public int OnAfterAttributeChange(uint docCookie, uint grfAttribs) => VSConstants.S_OK;

    public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame) => VSConstants.S_OK;

    public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) => VSConstants.S_OK;

    /// <summary>The path of a saved document that is a file on disk, outside the temporary folder; null otherwise.</summary>
    private string? FilePath(uint docCookie)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!_enabled)
        {
            return null;
        }

        var moniker = _documents.GetDocumentInfo(docCookie).Moniker;
        if (string.IsNullOrEmpty(moniker) || !Path.IsPathRooted(moniker) || !File.Exists(moniker))
        {
            return null;
        }

        // Comparison copies (earlier versions, extracted text) live in the temporary folder and are not worth keeping.
        var temporary = Path.GetFullPath(Path.GetTempPath());
        return Path.GetFullPath(moniker).StartsWith(temporary, StringComparison.OrdinalIgnoreCase) ? null : moniker;
    }

    // The history must never get in the way of saving: failures are only logged.
    private static void Guard(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or OperationCanceledException)
        {
            Diagnostics.LogWarning($"{step} failed: {ex.Message}");
        }
    }
}
