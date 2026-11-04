using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace DiffRapid.Ai;

/// <summary>Parses one unambiguous fenced proposal without applying it.</summary>
public static class ResolutionParser
{
    /// <summary>Extracts replacement lines, preserving whitespace, and one following line of reasoning.</summary>
    public static AiResolution Parse(string reply, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reply is null)
        {
            throw new ArgumentNullException(nameof(reply));
        }

        using var reader = new StringReader(reply);
        var lines = new List<string>();
        var reasoning = new List<string>();
        var opened = false;
        var closed = false;
        var fenceCharacter = '\0';
        var fenceLength = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fence = ReadFence(line, cancellationToken);
            if (!opened && fence.Length >= 3)
            {
                opened = true;
                fenceCharacter = fence.Character;
                fenceLength = fence.Length;
            }
            else if (opened && !closed)
            {
                if (fence.Character == fenceCharacter && fence.Length >= fenceLength && fence.Tail.Length == 0)
                {
                    closed = true;
                }
                else
                {
                    lines.Add(line);
                }
            }
            else if (closed)
            {
                if (fence.Length >= 3)
                {
                    throw new FormatException("The AI reply contains several fenced blocks; the proposal is ambiguous.");
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    reasoning.Add(line.Trim());
                }
            }
        }

        if (!opened || !closed)
        {
            throw new FormatException("The AI reply must contain one complete fenced block of resolved lines.");
        }

        if (reasoning.Count != 1)
        {
            throw new FormatException("The AI proposal must be followed by one line of reasoning.");
        }

        return new AiResolution(lines.AsReadOnly(), reasoning[0]);
    }

    private static (char Character, int Length, string Tail) ReadFence(string line, CancellationToken cancellationToken)
    {
        var position = 0;
        while (position < line.Length && position < 3 && line[position] == ' ')
        {
            position++;
        }

        if (position >= line.Length || (line[position] != '`' && line[position] != '~'))
        {
            return default;
        }

        var character = line[position];
        var start = position;
        while (position < line.Length && line[position] == character)
        {
            if ((position & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            position++;
        }

        return (character, position - start, line.Substring(position).Trim());
    }
}
