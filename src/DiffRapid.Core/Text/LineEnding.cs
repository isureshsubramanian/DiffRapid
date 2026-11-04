namespace DiffRapid.Core.Text;

/// <summary>The terminator that ended a line in the original text.</summary>
public enum LineEnding
{
    /// <summary>Last line of a file without a trailing line break.</summary>
    None,
    CrLf,
    Lf,
    Cr,
}
