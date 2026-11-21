using System.IO;
using System.Text;

namespace DiffRapid.Documents.Internal;

/// <summary>Collects extracted text one line at a time, with '\n' line endings.</summary>
internal sealed class TextLines
{
    private readonly StringBuilder _builder = new();
    private long _length;

    /// <summary>Appends one line; embedded '\n' characters start further lines.</summary>
    /// <exception cref="InvalidDataException">The text would pass <see cref="ExtractLimits.MaxTextLength"/>.</exception>
    public void Add(string line)
    {
        ExtractLimits.AddText(ref _length, line.Length + 1L);
        _builder.Append(line).Append('\n');
    }

    /// <summary>Returns the collected text.</summary>
    public override string ToString() => _builder.ToString();
}
