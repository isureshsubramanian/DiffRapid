using System;

namespace DiffRapid.Core.Algorithms;

public static class DiffAlgorithms
{
    public static IDiffAlgorithm Create(DiffAlgorithmKind kind) => kind switch
    {
        DiffAlgorithmKind.Default => new MyersDiff(),
        DiffAlgorithmKind.Minimal => new MyersDiff(),
        DiffAlgorithmKind.Patience => new PatienceDiff(),
        DiffAlgorithmKind.Histogram => new HistogramDiff(),
        DiffAlgorithmKind.None => new NoneDiff(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
