using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Constants = Microsoft.VisualStudio.OLE.Interop.Constants;
using OleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

namespace DiffRapid.VisualStudio.UI;

/// <summary>
/// Owner of the comparison tabs in the running document table. Visual Studio saves a document by asking
/// its owner to save the item (Save, Save All, the "save changes?" prompt on close); this owner hands that
/// to the comparison itself. Owning the tabs with the solution node made those saves save the solution.
/// </summary>
internal sealed class ComparisonHierarchy : IVsUIHierarchy, IVsPersistHierarchyItem
{
    private const string Name = "DiffRapid";

    private OleServiceProvider? _site;

    public int SaveItem(VSSAVEFLAGS dwSave, string pszSilentSaveAsName, uint itemid, IntPtr punkDocData, out int pfCanceled)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        pfCanceled = 0;
        if (punkDocData == IntPtr.Zero || Marshal.GetObjectForIUnknown(punkDocData) is not IVsPersistDocData document)
        {
            return VSConstants.E_INVALIDARG;
        }

        return document.SaveDocData(dwSave, out _, out pfCanceled);
    }

    public int IsItemDirty(uint itemid, IntPtr punkDocData, out int pfDirty)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        pfDirty = 0;
        if (punkDocData == IntPtr.Zero || Marshal.GetObjectForIUnknown(punkDocData) is not IVsPersistDocData document)
        {
            return VSConstants.E_INVALIDARG;
        }

        return document.IsDocDataDirty(out pfDirty);
    }

    public int GetProperty(uint itemid, int propid, out object? pvar)
    {
        switch ((__VSHPROPID)propid)
        {
            case __VSHPROPID.VSHPROPID_Name:
            case __VSHPROPID.VSHPROPID_Caption:
                pvar = Name;
                return VSConstants.S_OK;
            case __VSHPROPID.VSHPROPID_Parent:
                pvar = unchecked((int)VSConstants.VSITEMID.Nil);
                return VSConstants.S_OK;
            default:
                pvar = null;
                return VSConstants.DISP_E_MEMBERNOTFOUND;
        }
    }

    public int SetProperty(uint itemid, int propid, object var) => VSConstants.DISP_E_MEMBERNOTFOUND;

    public int GetGuidProperty(uint itemid, int propid, out Guid pguid)
    {
        pguid = Guid.Empty;
        return VSConstants.DISP_E_MEMBERNOTFOUND;
    }

    public int SetGuidProperty(uint itemid, int propid, ref Guid rguid) => VSConstants.DISP_E_MEMBERNOTFOUND;

    public int GetCanonicalName(uint itemid, out string? pbstrName)
    {
        pbstrName = null;
        return VSConstants.E_NOTIMPL;
    }

    public int ParseCanonicalName(string pszName, out uint pitemid)
    {
        pitemid = (uint)VSConstants.VSITEMID.Nil;
        return VSConstants.E_NOTIMPL;
    }

    public int GetNestedHierarchy(uint itemid, ref Guid iidHierarchyNested, out IntPtr ppHierarchyNested, out uint pitemidNested)
    {
        ppHierarchyNested = IntPtr.Zero;
        pitemidNested = (uint)VSConstants.VSITEMID.Nil;
        return VSConstants.E_FAIL;
    }

    public int SetSite(OleServiceProvider psp)
    {
        _site = psp;
        return VSConstants.S_OK;
    }

    public int GetSite(out OleServiceProvider? ppSP)
    {
        ppSP = _site;
        return VSConstants.S_OK;
    }

    public int QueryClose(out int pfCanClose)
    {
        pfCanClose = 1;
        return VSConstants.S_OK;
    }

    public int Close() => VSConstants.S_OK;

    // The tabs' items never change, so there are no hierarchy events to raise.
    public int AdviseHierarchyEvents(IVsHierarchyEvents pEventSink, out uint pdwCookie)
    {
        pdwCookie = 1;
        return VSConstants.S_OK;
    }

    public int UnadviseHierarchyEvents(uint dwCookie) => VSConstants.S_OK;

    public int QueryStatusCommand(uint itemid, ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText) =>
        (int)Constants.OLECMDERR_E_NOTSUPPORTED;

    public int ExecCommand(uint itemid, ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut) =>
        (int)Constants.OLECMDERR_E_NOTSUPPORTED;

    public int Unused0() => VSConstants.E_NOTIMPL;

    public int Unused1() => VSConstants.E_NOTIMPL;

    public int Unused2() => VSConstants.E_NOTIMPL;

    public int Unused3() => VSConstants.E_NOTIMPL;

    public int Unused4() => VSConstants.E_NOTIMPL;
}
