using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

namespace DiffRapid.VisualStudio.UI;

/// <summary>A comparison tab in the document well. Multi-instance; one per comparison.</summary>
[Guid(PackageIds.DiffWindowGuidString)]
public sealed class DiffToolWindow : ToolWindowPane
{
    public DiffToolWindow()
        : base(null)
    {
        Caption = "DiffRapid";
    }

    internal async Task LoadAsync(AsyncPackage package, string leftPath, string rightPath)
    {
        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        Caption = Path.GetFileName(leftPath) + " ↔ " + Path.GetFileName(rightPath);
        (Content as IDisposable)?.Dispose();
        Content = await TextDiffControl.CreateAsync(package, leftPath, rightPath);
    }

    protected override void OnClose()
    {
        (Content as IDisposable)?.Dispose();
        Content = null;
        base.OnClose();
    }
}
