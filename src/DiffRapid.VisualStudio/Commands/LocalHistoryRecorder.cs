using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
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
    // Latin-1 maps each byte to the char of the same value and back, with no preamble, so a file's bytes pass
    // through LocalHistoryStore.SnapshotText unchanged.
    private static readonly Encoding ByteExact = Encoding.GetEncoding(28591);

    private static LocalHistoryRecorder? _instance;

    private readonly DiffRapidPackage _package;
    private readonly RunningDocumentTable _documents;
    private readonly LocalHistoryStore _store;
    private readonly long _maxFileSize;

    // Files known to have at least one version in the history, so their later saves skip reading the old version.
    private readonly ConcurrentDictionary<string, bool> _hasHistory = new(StringComparer.OrdinalIgnoreCase);
    private bool _enabled;
    private uint _cookie;

    // Saves are recorded in the background, one at a time and in the order of the saves, so a file's
    // "Before save" version is stored before its "Saved" one. Only the UI thread adds to the queue.
    private Task _queue = Task.CompletedTask;

    private LocalHistoryRecorder(DiffRapidPackage package, LocalHistoryStore store, bool enabled, long maxFileSize)
    {
        _package = package;
        _store = store;
        _enabled = enabled;
        _maxFileSize = maxFileSize;
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
        var recorder = new LocalHistoryRecorder(package, store, settings.KeepHistory, settings.History.MaxFileSize);
        recorder._cookie = recorder._documents.Advise(recorder);
        _instance = recorder;

        _ = Task.Run(() => Guard("Pruning the local history", () => store.Prune(package.DisposalToken)));
    }

    /// <summary>Stops listening to saves; called when the package is disposed.</summary>
    public static void Stop()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var recorder = _instance;
        _instance = null;
        if (recorder is not null && recorder._cookie != 0)
        {
            recorder._documents.Unadvise(recorder._cookie);
            recorder._cookie = 0;
        }
    }

    public int OnBeforeSave(uint docCookie)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var path = FilePath(docCookie);
        // HasHistory only checks that the file's index exists: no lock, so it is safe on the UI thread.
        if (path is null || _hasHistory.ContainsKey(path) || _store.HasHistory(path))
        {
            // The previous version is already in the history.
            return VSConstants.S_OK;
        }

        // The old version has to be read before the save overwrites it, so this read stays on the UI thread. It is
        // bounded by the history's size limit (larger files are not kept anyway). The store, whose lock can wait for
        // another Visual Studio instance, is used in the background.
        byte[]? before = null;
        Guard("Reading the version before the save", () =>
        {
            if (new FileInfo(path).Length <= _maxFileSize)
            {
                before = File.ReadAllBytes(path);
            }
        });
        if (before is not { } content)
        {
            return VSConstants.S_OK;
        }

        Enqueue("Keeping the version before the save", () =>
        {
            if (_store.List(path, _package.DisposalToken).Count == 0)
            {
                _store.SnapshotText(path, ByteExact.GetString(content), ByteExact, "Before save", _package.DisposalToken);
            }

            _hasHistory[path] = true;
        });
        return VSConstants.S_OK;
    }

    public int OnAfterSave(uint docCookie)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var path = FilePath(docCookie);
        if (path is not null)
        {
            Enqueue("Keeping the saved version", () => _store.Snapshot(path, "Saved", cancellationToken: _package.DisposalToken));
        }

        // Settings may have changed since the last save; read them again for the next one.
        _package.JoinableTaskFactory.RunAsync(async () => _enabled = (await DiffRapidSettings.ReadAsync(_package)).KeepHistory).FileAndForget("DiffRapid/LocalHistorySettings");
        return VSConstants.S_OK;
    }

    public int OnAfterAttributeChangeEx(uint docCookie, uint grfAttribs, IVsHierarchy pHierOld, uint itemidOld, string pszMkDocumentOld, IVsHierarchy pHierNew, uint itemidNew, string pszMkDocumentNew)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // A renamed file takes its history with it.
        if ((grfAttribs & (uint)__VSRDTATTRIB.RDTA_MkDocument) != 0 && File.Exists(pszMkDocumentNew) && !string.IsNullOrEmpty(pszMkDocumentOld))
        {
            Enqueue("Moving the local history of a renamed file", () => _store.Rename(pszMkDocumentOld, pszMkDocumentNew, _package.DisposalToken));
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

    /// <summary>Runs a step of the history after the ones queued before it, on a background thread.</summary>
    private void Enqueue(string step, Action action)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _queue = _queue.ContinueWith(_ => Guard(step, action), _package.DisposalToken, TaskContinuationOptions.None, TaskScheduler.Default);
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
