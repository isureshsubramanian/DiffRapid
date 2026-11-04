using System;

namespace DiffRapid.VisualStudio.UI;

/// <summary>Content of a comparison document tab (files or folders).</summary>
internal interface IComparisonView : IDisposable
{
    /// <summary>True when something the comparison edits has unsaved changes.</summary>
    bool IsDirty { get; }

    /// <summary>Saves pending changes; does nothing when there are none.</summary>
    void Save();
}
