namespace DiffRapid.Core.Comparison;

public enum DiffKind
{
    /// <summary>Lines exist on both sides but differ.</summary>
    Changed,

    /// <summary>Lines exist only on the right side.</summary>
    Added,

    /// <summary>Lines exist only on the left side.</summary>
    Removed,
}

/// <summary>How much a difference matters, after ignore rules and filters are applied.</summary>
public enum DiffSignificance
{
    /// <summary>A real difference; counted, navigated to and merged.</summary>
    Important,

    /// <summary>Hidden by an ignore rule (for example blank lines); not counted or merged.</summary>
    Ignored,
}

/// <summary>A contiguous block of differing lines. Ranges are zero-based, end-exclusive.</summary>
public sealed record DiffHunk(int LeftStart, int LeftCount, int RightStart, int RightCount)
{
    public int LeftEnd => LeftStart + LeftCount;

    public int RightEnd => RightStart + RightCount;

    public DiffKind Kind => LeftCount == 0 ? DiffKind.Added : RightCount == 0 ? DiffKind.Removed : DiffKind.Changed;

    public DiffSignificance Significance { get; init; } = DiffSignificance.Important;
}

/// <summary>A block of lines removed in one place and inserted unchanged in another.</summary>
public sealed record MovedBlock(int LeftStart, int RightStart, int LineCount);
