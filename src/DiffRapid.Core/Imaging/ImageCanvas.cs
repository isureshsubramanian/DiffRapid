using System;

namespace DiffRapid.Core.Imaging;

/// <summary>Where the two images sit on the canvas that holds both.</summary>
internal readonly struct ImageCanvas
{
    private ImageCanvas(int width, int height, int leftX, int leftY, int rightX, int rightY, PixelImage left, PixelImage right)
    {
        Width = width;
        Height = height;
        LeftX = leftX;
        LeftY = leftY;
        RightX = rightX;
        RightY = rightY;
        LeftWidth = left.Width;
        LeftHeight = left.Height;
        RightWidth = right.Width;
        RightHeight = right.Height;
    }

    public int Width { get; }

    public int Height { get; }

    public int LeftX { get; }

    public int LeftY { get; }

    public int RightX { get; }

    public int RightY { get; }

    public int LeftWidth { get; }

    public int LeftHeight { get; }

    public int RightWidth { get; }

    public int RightHeight { get; }

    /// <summary>Places the left image at the origin and the right image shifted by the offset.</summary>
    public static ImageCanvas Create(PixelImage left, PixelImage right, int offsetX, int offsetY)
    {
        var minX = Math.Min(0, offsetX);
        var minY = Math.Min(0, offsetY);
        var maxX = Math.Max(left.Width, offsetX + right.Width);
        var maxY = Math.Max(left.Height, offsetY + right.Height);
        var width = maxX - minX;
        var height = maxY - minY;
        if ((long)width * height > int.MaxValue)
        {
            throw new ArgumentException("The images are too large to compare in one canvas.");
        }

        return new ImageCanvas(width, height, -minX, -minY, offsetX - minX, offsetY - minY, left, right);
    }

    /// <summary>True when canvas column/row (x, y) falls inside the left image.</summary>
    public bool InLeft(int x, int y) => x >= LeftX && x < LeftX + LeftWidth && y >= LeftY && y < LeftY + LeftHeight;

    /// <summary>True when canvas column/row (x, y) falls inside the right image.</summary>
    public bool InRight(int x, int y) => x >= RightX && x < RightX + RightWidth && y >= RightY && y < RightY + RightHeight;
}
