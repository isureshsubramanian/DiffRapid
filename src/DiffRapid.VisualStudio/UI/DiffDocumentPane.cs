using System;
using System.ComponentModel.Design;
using System.Windows;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Constants = Microsoft.VisualStudio.OLE.Interop.Constants;

namespace DiffRapid.VisualStudio.UI;

/// <summary>
/// A comparison shown as a document tab. It is both the view and the document data registered in the
/// running document table; saving it saves what the comparison edits (the compared files) through their
/// own documents.
/// </summary>
internal sealed class DiffDocumentPane : WindowPane, IVsPersistDocData, IOleCommandTarget
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

    // Content that hosts editors (the file comparison) gets the window's commands first, so typing,
    // undo and clipboard reach the focused editor; the rest go to the pane's own command service.
    int IOleCommandTarget.QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Content is IOleCommandTarget content)
        {
            var hr = content.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
            if (!IsUnhandled(hr))
            {
                return hr;
            }
        }

        return GetService(typeof(IMenuCommandService)) is IOleCommandTarget commands
            ? commands.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText)
            : (int)Constants.OLECMDERR_E_NOTSUPPORTED;
    }

    int IOleCommandTarget.Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Content is IOleCommandTarget content)
        {
            var hr = content.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
            if (!IsUnhandled(hr))
            {
                return hr;
            }
        }

        return GetService(typeof(IMenuCommandService)) is IOleCommandTarget commands
            ? commands.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut)
            : (int)Constants.OLECMDERR_E_NOTSUPPORTED;
    }

    private static bool IsUnhandled(int hr) =>
        hr == (int)Constants.OLECMDERR_E_NOTSUPPORTED || hr == (int)Constants.OLECMDERR_E_UNKNOWNGROUP;

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
