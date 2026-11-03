namespace DiffRapid.Core.Text;

/// <summary>A single line of text, without its terminator.</summary>
public readonly struct TextLine
{
    public TextLine(string content, LineEnding ending)
    {
        Content = content;
        Ending = ending;
    }

    public string Content { get; }

    public LineEnding Ending { get; }

    public string EndingText => Ending switch
    {
        LineEnding.CrLf => "\r\n",
        LineEnding.Lf => "\n",
        LineEnding.Cr => "\r",
        _ => string.Empty,
    };

    public override string ToString() => Content;
}
