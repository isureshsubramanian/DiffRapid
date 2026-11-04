using System;
using System.IO;
using System.Threading;

namespace DiffRapid.Core.Structured;

/// <summary>Chooses a structured format for a file and reads it with the matching reader.</summary>
public static class StructuredReader
{
    /// <summary>
    /// Picks the format from the file extension (.csproj and other project files, .props and .targets as
    /// MSBuild; .resx; .json; .yml and .yaml; other XML extensions), otherwise from the first character of the
    /// text: '&lt;' is XML, '{' or '[' is JSON, anything else YAML.
    /// </summary>
    /// <param name="fileName">The file name or path, or null.</param>
    /// <param name="text">The file's text.</param>
    public static StructuredFormat DetectFormat(string? fileName, string text)
    {
        var extension = fileName is null ? string.Empty : Path.GetExtension(fileName).ToLowerInvariant();
        switch (extension)
        {
            case ".csproj":
            case ".vbproj":
            case ".fsproj":
            case ".vcxproj":
            case ".sqlproj":
            case ".proj":
            case ".props":
            case ".targets":
                return StructuredFormat.MSBuild;
            case ".resx":
            case ".resw":
                return StructuredFormat.Resx;
            case ".json":
                return StructuredFormat.Json;
            case ".yml":
            case ".yaml":
                return StructuredFormat.Yaml;
            case ".xml":
            case ".config":
            case ".nuspec":
            case ".xaml":
            case ".manifest":
            case ".vsixmanifest":
                return StructuredFormat.Xml;
        }

        var first = FirstSignificantCharacter(text ?? string.Empty);
        return first switch
        {
            '<' => StructuredFormat.Xml,
            '{' or '[' => StructuredFormat.Json,
            _ => StructuredFormat.Yaml,
        };
    }

    /// <summary>Reads text in the given format.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="format">The format; not <see cref="StructuredFormat.Auto"/>.</param>
    /// <param name="arrayKey">For JSON and YAML, the property that identifies objects inside arrays, or null.</param>
    /// <param name="cancellationToken">Stops reading a large file.</param>
    /// <exception cref="StructuredFormatException">The text is malformed.</exception>
    public static StructuredNode Read(string text, StructuredFormat format, string? arrayKey = null, CancellationToken cancellationToken = default) =>
        format switch
        {
            StructuredFormat.Xml or StructuredFormat.MSBuild or StructuredFormat.Resx => XmlTreeReader.Read(text, format, cancellationToken),
            StructuredFormat.Json => JsonTreeReader.Read(text, arrayKey, cancellationToken),
            StructuredFormat.Yaml => YamlTreeReader.Read(text, arrayKey, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Choose a format, or call DetectFormat first."),
        };

    private static char FirstSignificantCharacter(string text)
    {
        foreach (var character in text)
        {
            if (!char.IsWhiteSpace(character) && character != '\uFEFF')
            {
                return character;
            }
        }

        return '\0';
    }
}
