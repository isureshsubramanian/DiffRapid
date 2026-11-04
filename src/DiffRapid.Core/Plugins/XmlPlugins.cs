using System.IO;
using System.Text;
using System.Xml;

namespace DiffRapid.Core.Plugins;

/// <summary>
/// PrettifyXml: re-indents a document with each top-level node on its own line. The XML declaration is
/// kept as written; a DOCTYPE is dropped, and DTDs are never processed or fetched.
/// </summary>
internal static class XmlPlugins
{
    private const char ByteOrderMark = (char)0xFEFF;

    public static string Prettify(string text, PluginContext context)
    {
        var arguments = StepArguments.Parse("PrettifyXml", context.Arguments, keys: ["indent"]);
        arguments.ExpectAtMost(0);
        var indent = arguments.PositiveInt("indent", 2);
        var output = new StringBuilder(text.Length + text.Length / 4);
        try
        {
            using var reader = XmlReader.Create(new StringReader(text.TrimStart(ByteOrderMark)), ReaderSettings());
            reader.Read();
            if (reader.NodeType == XmlNodeType.XmlDeclaration)
            {
                output.Append("<?xml ").Append(reader.Value).Append("?>\n");
                reader.Read();
            }

            WriteNodes(reader, output, indent);
        }
        catch (XmlException ex)
        {
            throw new PluginException($"PrettifyXml: the text is not well-formed XML ({ex.Message}).", ex);
        }

        return output.ToString();
    }

    private static XmlReaderSettings ReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        IgnoreWhitespace = true,
        ConformanceLevel = ConformanceLevel.Document,
    };

    private static void WriteNodes(XmlReader reader, StringBuilder output, int indent)
    {
        var writerSettings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = new string(' ', indent),
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
            OmitXmlDeclaration = true,
            ConformanceLevel = ConformanceLevel.Fragment,
        };

        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.DocumentType)
            {
                reader.Read();
                continue;
            }

            // One writer per top-level node puts each node on its own line; WriteNode moves the reader past it.
            using (var writer = XmlWriter.Create(output, writerSettings))
            {
                writer.WriteNode(reader, defattr: false);
            }

            output.Append('\n');
        }
    }
}
