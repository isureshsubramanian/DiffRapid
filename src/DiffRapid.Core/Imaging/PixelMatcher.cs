using System;

namespace DiffRapid.Core.Imaging;

/// <summary>Decides whether two BGRA32 pixels match under the comparison options.</summary>
internal sealed class PixelMatcher
{
    private readonly int _tolerance;
    private readonly long _squaredTolerance;
    private readonly bool _euclidean;
    private readonly bool _ignoreAlpha;

    public PixelMatcher(ImageCompareOptions options)
    {
        _tolerance = options.ColorTolerance;
        _squaredTolerance = (long)options.ColorTolerance * options.ColorTolerance;
        _euclidean = options.ToleranceMode == ColorToleranceMode.Euclidean;
        _ignoreAlpha = options.IgnoreAlpha;
    }

    /// <summary>Compares the pixel at <paramref name="leftIndex"/> with the one at <paramref name="rightIndex"/>.</summary>
    public bool Matches(byte[] left, int leftIndex, byte[] right, int rightIndex)
    {
        var alphaDelta = 0;
        if (!_ignoreAlpha)
        {
            var leftAlpha = left[leftIndex + 3];
            var rightAlpha = right[rightIndex + 3];
            if (leftAlpha == 0 && rightAlpha == 0)
            {
                // Invisible pixels match whatever colour bytes they carry.
                return true;
            }

            alphaDelta = leftAlpha - rightAlpha;
        }

        var blueDelta = left[leftIndex] - right[rightIndex];
        var greenDelta = left[leftIndex + 1] - right[rightIndex + 1];
        var redDelta = left[leftIndex + 2] - right[rightIndex + 2];
        if (_euclidean)
        {
            var squared = (long)blueDelta * blueDelta + (long)greenDelta * greenDelta + (long)redDelta * redDelta + (long)alphaDelta * alphaDelta;
            return squared <= _squaredTolerance;
        }

        return Math.Abs(blueDelta) <= _tolerance
            && Math.Abs(greenDelta) <= _tolerance
            && Math.Abs(redDelta) <= _tolerance
            && Math.Abs(alphaDelta) <= _tolerance;
    }
}
