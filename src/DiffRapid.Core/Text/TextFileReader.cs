using System;
using System.IO;
using System.Text;

namespace DiffRapid.Core.Text;

/// <summary>Result of decoding a file: the text plus what was detected about it.</summary>
public sealed class DecodedText
{
    public DecodedText(string text, Encoding encoding, bool hasByteOrderMark, bool isBinary)
    {
        Text = text;
        Encoding = encoding;
        HasByteOrderMark = hasByteOrderMark;
        IsBinary = isBinary;
    }

    public string Text { get; }

    public Encoding Encoding { get; }

    public bool HasByteOrderMark { get; }

    /// <summary>True when the content looks binary (contains NUL bytes outside a UTF-16/32 encoding).</summary>
    public bool IsBinary { get; }
}

/// <summary>
/// Decodes bytes using the chain: byte order mark, then strict UTF-8, then a fallback code page.
/// </summary>
public static class TextFileReader
{
    private const int BinaryProbeLength = 8000;

    public static DecodedText ReadFile(string path, Encoding? fallback = null)
    {
        return Decode(File.ReadAllBytes(path), fallback);
    }

    public static DecodedText Decode(byte[] bytes, Encoding? fallback = null)
    {
        if (bytes is null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }

        var bom = DetectByteOrderMark(bytes, out var bomLength);
        if (bom is not null)
        {
            var text = bom.GetString(bytes, bomLength, bytes.Length - bomLength);
            return new DecodedText(text, bom, hasByteOrderMark: true, isBinary: false);
        }

        var isBinary = LooksBinary(bytes);
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            var text = strictUtf8.GetString(bytes);
            return new DecodedText(text, new UTF8Encoding(false), hasByteOrderMark: false, isBinary);
        }
        catch (DecoderFallbackException)
        {
            var encoding = fallback ?? Encoding.GetEncoding(28591); // ISO-8859-1 maps every byte.
            return new DecodedText(encoding.GetString(bytes), encoding, hasByteOrderMark: false, isBinary);
        }
    }

    private static Encoding? DetectByteOrderMark(byte[] b, out int length)
    {
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0 && b[3] == 0)
        {
            length = 4;
            return new UTF32Encoding(bigEndian: false, byteOrderMark: true);
        }

        if (b.Length >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 0xFE && b[3] == 0xFF)
        {
            length = 4;
            return new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        }

        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
        {
            length = 3;
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        }

        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
        {
            length = 2;
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        }

        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
        {
            length = 2;
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
        }

        length = 0;
        return null;
    }

    private static bool LooksBinary(byte[] bytes)
    {
        var n = Math.Min(bytes.Length, BinaryProbeLength);
        for (var i = 0; i < n; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }

        return false;
    }
}
