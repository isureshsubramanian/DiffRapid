using System.Text;

namespace DiffRapid.Documents.Internal;

/// <summary>Collects extracted text one line at a time, with '\n' line endings.</summary>
internal sealed class TextLines
{
    private readonly StringBuilder _builder = new();

    /// <summary>Appends one line; embedded '\n' characters start further lines.</summary>
    public void Add(string line)
    {
        _builder.Append(line).Append('\n');
    }

    /// <summary>Returns the collected text.</summary>
    public override string ToString() => _builder.ToString();
}
