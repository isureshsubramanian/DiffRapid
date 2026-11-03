using System;
using System.Windows;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace DiffRapid.VisualStudio.UI;

/// <summary>
/// A comparison shown as a document tab. It is both the view and the document data registered in the
/// running document table; saving it saves what the comparison edits (the compared files) through their
/// own documents.
/// </summary>
internal sealed class DiffDocumentPane : WindowPane, IVsPersistDocData
{
    private static readonly Guid EditorType = new(PackageIds.DiffWindowGuidString);

    public DiffDocumentPane(FrameworkElement view)
        : base(null)
    {
        Content = view;
    }

    private IComparisonView? View => Content as IComparisonView;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeContent();
        }

        base.Dispose(disposing);
    }

    private void DisposeContent()
    {
        View?.Dispose();
        Content = null;
    }

    int IVsPersistDocData.GetGuidEditorType(out Guid pClassID)
    {
        pClassID = EditorType;
        return VSConstants.S_OK;
    }

    // Dirty when a compared file has unsaved edits, so closing the tab offers to save them.
    int IVsPersistDocData.IsDocDataDirty(out int pfDirty)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        pfDirty = View is { IsDirty: true } ? 1 : 0;
        return VSConstants.S_OK;
    }

    int IVsPersistDocData.SetUntitledDocPath(string pszDocDataPath) => VSConstants.S_OK;

    int IVsPersistDocData.LoadDocData(string pszMkDocument) => VSConstants.S_OK;

    int IVsPersistDocData.SaveDocData(VSSAVEFLAGS dwSave, out string? pbstrMkDocumentNew, out int pfSaveCanceled)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        pbstrMkDocumentNew = null;
        pfSaveCanceled = 0;
        View?.Save();
        return VSConstants.S_OK;
    }

    int IVsPersistDocData.Close()
    {
        DisposeContent();
        return VSConstants.S_OK;
    }

    int IVsPersistDocData.OnRegisterDocData(uint docCookie, IVsHierarchy pHierNew, uint itemidNew) => VSConstants.S_OK;

    int IVsPersistDocData.RenameDocData(uint grfAttribs, IVsHierarchy pHierNew, uint itemidNew, string pszMkDocumentNew) => VSConstants.S_OK;

    int IVsPersistDocData.IsDocDataReloadable(out int pfReloadable)
    {
        pfReloadable = 0;
        return VSConstants.S_OK;
    }

    int IVsPersistDocData.ReloadDocData(uint grfFlags) => VSConstants.S_OK;
}
