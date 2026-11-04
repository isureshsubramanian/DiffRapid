using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace DiffRapid.Core.Structured;

/// <summary>
/// Reads XML into a structured tree. Elements are keyed by name plus an identity attribute where one exists
/// (Include, Update or Remove for MSBuild items, name for .resx data, otherwise id, key or name); attributes
/// are keyed by "@" and their name. Comments, processing instructions and the XML declaration are left out.
/// </summary>
public static class XmlTreeReader
{
    private const string SchemaNamespace = "http://www.w3.org/2001/XMLSchema";
    private static readonly string[] GenericIdentities = ["Include", "Update", "Remove", "name", "id", "key"];
    private static readonly string[] MSBuildIdentities = ["Include", "Update", "Remove", "Name", "Project"];
    private static readonly Regex XmlPositionSuffix = new(@"\s*Line \d+, position \d+\.\s*$", RegexOptions.CultureInvariant);

    /// <summary>Reads XML text with the given profile.</summary>
    /// <param name="text">The XML text.</param>
    /// <param name="profile"><see cref="StructuredFormat.Xml"/>, <see cref="StructuredFormat.MSBuild"/> or <see cref="StructuredFormat.Resx"/>.</param>
    /// <param name="cancellationToken">Stops reading a large file.</param>
    /// <exception cref="StructuredFormatException">The text is not well-formed XML.</exception>
    public static StructuredNode Read(string text, StructuredFormat profile = StructuredFormat.Xml, CancellationToken cancellationToken = default)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        if (profile is not (StructuredFormat.Xml or StructuredFormat.MSBuild or StructuredFormat.Resx))
        {
            throw new ArgumentOutOfRangeException(nameof(profile), profile, "Use Xml, MSBuild or Resx.");
        }

        try
        {
            return new Builder(profile, cancellationToken).Build(text).Freeze(null, cancellationToken);
        }
        catch (XmlException ex)
        {
            var reason = XmlPositionSuffix.Replace(ex.Message, string.Empty);
            throw new StructuredFormatException(reason, Math.Max(1, ex.LineNumber), ex.LinePosition);
        }
    }

    private sealed class Frame(NodeBuilder node)
    {
        public NodeBuilder Node { get; } = node;

        public StringBuilder Text { get; } = new();

        public int TextLine { get; set; }

        public bool HasElements { get; set; }
    }

    private sealed class Builder(StructuredFormat profile, CancellationToken cancellationToken)
    {
        private readonly Stack<Frame> _stack = new();

        private bool IsMSBuild => profile == StructuredFormat.MSBuild;

        public NodeBuilder Build(string text)
        {
            var document = new NodeBuilder(StructuredNodeKind.Document, string.Empty, 1);
            _stack.Push(new Frame(document));
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            var lineInfo = (IXmlLineInfo)reader;
            reader.Read();
            while (!reader.EOF)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsSkipped(reader))
                {
                    reader.Skip();
                    continue;
                }

                ReadNode(reader, lineInfo);
                reader.Read();
            }

            document.EndLine = Math.Max(1, lineInfo.LineNumber);
            return document;
        }

        private bool IsSkipped(XmlReader reader) =>
            profile == StructuredFormat.Resx
            && reader.NodeType == XmlNodeType.Element
            && reader.LocalName == "schema"
            && reader.NamespaceURI == SchemaNamespace;

        private void ReadNode(XmlReader reader, IXmlLineInfo lineInfo)
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    StartElement(reader, lineInfo);
                    break;
                case XmlNodeType.EndElement:
                    EndElement(_stack.Pop(), lineInfo.LineNumber);
                    break;
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace:
                    AppendText(reader.Value, lineInfo.LineNumber);
                    break;
            }
        }

        private void StartElement(XmlReader reader, IXmlLineInfo lineInfo)
        {
            if (_stack.Count > NodeBuilder.MaxDepth)
            {
                throw new StructuredFormatException("the XML is nested too deeply", lineInfo.LineNumber, lineInfo.LinePosition);
            }

            var parent = _stack.Peek();
            FlushText(parent);
            parent.HasElements = true;
            var element = new NodeBuilder(StructuredNodeKind.Element, reader.Name, lineInfo.LineNumber);
            var isEmpty = reader.IsEmptyElement;
            ReadAttributes(reader, lineInfo, element);
            var frame = new Frame(element);
            if (isEmpty)
            {
                element.Value = string.Empty;
                EndElement(frame, element.Children.Select(attribute => attribute.EndLine).DefaultIfEmpty(element.StartLine).Max(), parent);
                return;
            }

            _stack.Push(frame);
        }

        private static void ReadAttributes(XmlReader reader, IXmlLineInfo lineInfo, NodeBuilder element)
        {
            while (reader.MoveToNextAttribute())
            {
                element.Add(Attribute(reader.Name, reader.Value, lineInfo.LineNumber));
            }

            reader.MoveToElement();
        }

        private static NodeBuilder Attribute(string name, string value, int line) =>
            new(StructuredNodeKind.Attribute, name, line) { Value = value, BaseKey = "@" + name };

        private void AppendText(string value, int line)
        {
            var frame = _stack.Peek();
            if (frame.Text.Length == 0 || string.IsNullOrWhiteSpace(frame.Text.ToString()))
            {
                frame.TextLine = line;
            }

            frame.Text.Append(value);
        }

        private static void FlushText(Frame frame)
        {
            var text = frame.Text.ToString();
            frame.Text.Clear();
            if (frame.Node.Kind == StructuredNodeKind.Document || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            frame.Node.Add(new NodeBuilder(StructuredNodeKind.Text, string.Empty, frame.TextLine)
            {
                Value = text,
                BaseKey = "#text",
                EndLine = frame.TextLine + text.Count(character => character == '\n'),
            });
        }

        private void EndElement(Frame frame, int endLine) => EndElement(frame, endLine, _stack.Peek());

        private void EndElement(Frame frame, int endLine, Frame parent)
        {
            var element = frame.Node;
            element.EndLine = endLine;
            // The value is the element's own text; with child elements, text goes into Text children instead.
            if (frame.HasElements)
            {
                FlushText(frame);
                element.Value = string.Empty;
            }
            else if (element.Value is null)
            {
                element.Value = frame.Text.ToString();
            }

            if (IsMSBuild && element.Name is "ItemGroup" or "PropertyGroup")
            {
                LiftGroup(element, parent.Node);
                return;
            }

            AssignKey(element);
            parent.Node.Add(element);
        }

        /// <summary>Moves the items or properties of an MSBuild group into its parent, carrying the group's condition.</summary>
        private void LiftGroup(NodeBuilder group, NodeBuilder parent)
        {
            var groupCondition = group.Children.FirstOrDefault(child => child.Kind == StructuredNodeKind.Attribute && child.Name == "Condition");
            foreach (var member in group.Children.Where(child => child.Kind == StructuredNodeKind.Element))
            {
                if (group.Name == "ItemGroup")
                {
                    MetadataToAttributes(member);
                }

                if (groupCondition is not null)
                {
                    AddCondition(member, groupCondition);
                }

                AssignKey(member);
                parent.Add(member);
            }
        }

        /// <summary>Turns item metadata written as child elements into attributes, so both spellings compare equal.</summary>
        private static void MetadataToAttributes(NodeBuilder item)
        {
            for (var i = 0; i < item.Children.Count; i++)
            {
                var child = item.Children[i];
                // Metadata with its own attributes (such as a Condition) keeps its element form.
                var isMetadata = child.Kind == StructuredNodeKind.Element && child.Value is not null && child.Children.Count == 0;
                if (!isMetadata)
                {
                    continue;
                }

                var attribute = Attribute(child.Name, child.Value!, child.StartLine);
                attribute.EndLine = child.EndLine;
                item.Children[i] = attribute;
            }
        }

        private static void AddCondition(NodeBuilder member, NodeBuilder groupCondition)
        {
            var own = member.Children.FindIndex(child => child.Kind == StructuredNodeKind.Attribute && child.Name == "Condition");
            if (own < 0)
            {
                member.Children.Insert(0, Attribute("Condition", groupCondition.Value!, groupCondition.StartLine));
                return;
            }

            var combined = Attribute("Condition", "(" + groupCondition.Value + ") and (" + member.Children[own].Value + ")", member.Children[own].StartLine);
            member.Children[own] = combined;
        }

        private void AssignKey(NodeBuilder element)
        {
            var identities = IsMSBuild ? MSBuildIdentities : GenericIdentities;
            var key = new StringBuilder(element.Name);
            foreach (var identity in identities)
            {
                var attribute = FindAttribute(element, identity);
                if (attribute is not null)
                {
                    element.IdentityAttribute = attribute.Name;
                    key.Append('[').Append(attribute.Name).Append('=').Append(attribute.Value).Append(']');
                    break;
                }
            }

            var condition = IsMSBuild ? FindAttribute(element, "Condition") : null;
            if (condition is not null)
            {
                key.Append("[Condition=").Append(condition.Value).Append(']');
            }

            element.BaseKey = key.ToString();
        }

        private static NodeBuilder? FindAttribute(NodeBuilder element, string name) =>
            element.Children.FirstOrDefault(child => child.Kind == StructuredNodeKind.Attribute
                && string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
