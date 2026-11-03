using System;
using System.Collections.Generic;
using System.Text;

namespace DiffRapid.Core.Merge;

/// <summary>The source or ordering selected for a merge chunk.</summary>
public enum MergeResolution
{
    /// <summary>Not decided yet; written with conflict markers.</summary>
    Unresolved,

    /// <summary>The left lines.</summary>
    Left,

    /// <summary>The right lines.</summary>
    Right,

    /// <summary>The left lines followed by the right lines.</summary>
    LeftThenRight,

    /// <summary>The right lines followed by the left lines.</summary>
    RightThenLeft,

    /// <summary>The base lines (neither change).</summary>
    Base,

    /// <summary>Text supplied with <see cref="MergeSession.SetCustomText"/>.</summary>
    Custom,
}

/// <summary>Editable resolution choices over a three-way merge result.</summary>
public sealed class MergeSession
{
    private readonly MergeResult _result;
    private readonly string _newline;
    private readonly MergeResolution[] _resolutions;
    private readonly string[]?[] _custom;

    /// <summary>Creates a session with automatic choices for non-conflicting chunks.</summary>
    public MergeSession(MergeResult result)
    {
        _result = result ?? throw new ArgumentNullException(nameof(result));
        _newline = result.DetectNewline();
        _resolutions = new MergeResolution[result.Chunks.Count];
        _custom = new string[]?[result.Chunks.Count];
        for (var i = 0; i < _resolutions.Length; i++)
        {
            _resolutions[i] = result.Chunks[i].Kind switch
            {
                MergeChunkKind.Unchanged => MergeResolution.Base,
                MergeChunkKind.LeftOnly => MergeResolution.Left,
                MergeChunkKind.RightOnly => MergeResolution.Right,
                MergeChunkKind.BothSame => MergeResolution.Left,
                _ => MergeResolution.Unresolved,
            };
        }
    }

    /// <summary>Raised after resolution choices or custom text change.</summary>
    public event EventHandler? Changed;

    /// <summary>The merge this session resolves.</summary>
    public MergeResult Result => _result;

    /// <summary>The number of chunks still awaiting resolution.</summary>
    public int UnresolvedCount
    {
        get
        {
            var count = 0;
            foreach (var resolution in _resolutions)
            {
                if (resolution == MergeResolution.Unresolved)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Gets a chunk's current choice.</summary>
    public MergeResolution Resolution(int chunk)
    {
        ValidateChunk(chunk);
        return _resolutions[chunk];
    }

    /// <summary>Changes a chunk's choice. Custom requires previously supplied text.</summary>
    public void Resolve(int chunk, MergeResolution resolution)
    {
        ValidateChunk(chunk);
        ValidateResolution(resolution);
        if (resolution == MergeResolution.Custom && _custom[chunk] is null)
        {
            throw new InvalidOperationException("Supply custom text before selecting Custom.");
        }

        if (_resolutions[chunk] == resolution)
        {
            return;
        }

        _resolutions[chunk] = resolution;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Copies custom line contents, terminated using the merge's detected newline, and selects Custom.</summary>
    public void SetCustomText(int chunk, IReadOnlyList<string> lines)
    {
        ValidateChunk(chunk);
        if (lines is null)
        {
            throw new ArgumentNullException(nameof(lines));
        }

        var copy = new string[lines.Count];
        for (var i = 0; i < copy.Length; i++)
        {
            copy[i] = lines[i] ?? throw new ArgumentException("Lines cannot contain null.", nameof(lines));
        }

        _custom[chunk] = copy;
        _resolutions[chunk] = MergeResolution.Custom;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies a choice to all conflict chunks, including previously resolved conflicts.</summary>
    public void ResolveAllConflicts(MergeResolution resolution)
    {
        ValidateResolution(resolution);
        for (var i = 0; i < _resolutions.Length; i++)
        {
            if (_result.Chunks[i].Kind == MergeChunkKind.Conflict && resolution == MergeResolution.Custom && _custom[i] is null)
            {
                throw new InvalidOperationException("Supply custom text for every conflict first.");
            }
        }

        var changed = false;
        for (var i = 0; i < _resolutions.Length; i++)
        {
            if (_result.Chunks[i].Kind != MergeChunkKind.Conflict || _resolutions[i] == resolution)
            {
                continue;
            }

            _resolutions[i] = resolution;
            changed = true;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Finds the next conflict strictly after fromChunk; -1 starts at the beginning.</summary>
    public int NextConflict(int fromChunk)
    {
        ValidatePosition(fromChunk);
        for (var i = fromChunk + 1; i < _resolutions.Length; i++)
        {
            if (_result.Chunks[i].Kind == MergeChunkKind.Conflict)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Finds the previous conflict strictly before fromChunk; the chunk count starts at the end.</summary>
    public int PreviousConflict(int fromChunk)
    {
        ValidatePosition(fromChunk);
        for (var i = fromChunk - 1; i >= 0; i--)
        {
            if (_result.Chunks[i].Kind == MergeChunkKind.Conflict)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Renders choices, retaining source terminators and marking unresolved chunks.</summary>
    public string ToText(ConflictLabels? labels = null, bool includeBase = false)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < _resolutions.Length; i++)
        {
            AppendChunk(sb, i, labels, includeBase);
        }

        return sb.ToString();
    }

    /// <summary>Gets the zero-based output line range of one chunk for the supplied rendering options.</summary>
    public (int Start, int Count) OutputLineRange(int chunk, ConflictLabels? labels = null, bool includeBase = false)
    {
        ValidateChunk(chunk);
        return OutputLineRanges(labels, includeBase)[chunk];
    }

    /// <summary>
    /// Gets the zero-based output line range of every chunk in one pass, so a view can map output lines back
    /// to chunks. A chunk that renders no text has Count 0 and starts where the next chunk starts.
    /// </summary>
    public IReadOnlyList<(int Start, int Count)> OutputLineRanges(ConflictLabels? labels = null, bool includeBase = false)
    {
        var sb = new StringBuilder();
        var offsets = new int[_resolutions.Length + 1];
        for (var i = 0; i < _resolutions.Length; i++)
        {
            offsets[i] = sb.Length;
            AppendChunk(sb, i, labels, includeBase);
        }

        offsets[_resolutions.Length] = sb.Length;

        // Line breaks before each chunk boundary; CRLF counts once, even when a boundary splits it.
        var breaks = new int[offsets.Length];
        int position = 0, count = 0;
        var afterCarriageReturn = false;
        for (var b = 0; b < offsets.Length; b++)
        {
            for (; position < offsets[b]; position++)
            {
                var c = sb[position];
                if (c == '\r' || (c == '\n' && !afterCarriageReturn))
                {
                    count++;
                }

                afterCarriageReturn = c == '\r';
            }

            breaks[b] = count;
        }

        var ranges = new (int Start, int Count)[_resolutions.Length];
        for (var i = 0; i < ranges.Length; i++)
        {
            var end = offsets[i + 1];
            var openLine = end > 0 && sb[end - 1] != '\r' && sb[end - 1] != '\n' ? 1 : 0;
            ranges[i] = (breaks[i], end == offsets[i] ? 0 : breaks[i + 1] + openLine - breaks[i]);
        }

        return ranges;
    }

    private void AppendChunk(StringBuilder sb, int index, ConflictLabels? labels, bool includeBase)
    {
        var c = _result.Chunks[index];
        var newline = _newline;
        switch (_resolutions[index])
        {
            case MergeResolution.Unresolved:
                _result.AppendConflict(sb, c, labels ?? new ConflictLabels(), includeBase, newline);
                break;
            case MergeResolution.Left:
                MergeResult.Append(sb, _result.Left, c.LeftStart, c.LeftEnd, newline);
                break;
            case MergeResolution.Right:
                MergeResult.Append(sb, _result.Right, c.RightStart, c.RightEnd, newline);
                break;
            case MergeResolution.LeftThenRight:
                MergeResult.Append(sb, _result.Left, c.LeftStart, c.LeftEnd, newline);
                MergeResult.Append(sb, _result.Right, c.RightStart, c.RightEnd, newline);
                break;
            case MergeResolution.RightThenLeft:
                MergeResult.Append(sb, _result.Right, c.RightStart, c.RightEnd, newline);
                MergeResult.Append(sb, _result.Left, c.LeftStart, c.LeftEnd, newline);
                break;
            case MergeResolution.Base:
                MergeResult.Append(sb, _result.Base, c.BaseStart, c.BaseEnd, newline);
                break;
            case MergeResolution.Custom:
                foreach (var line in _custom[index]!)
                {
                    sb.Append(line).Append(newline);
                }

                break;
        }
    }

    private void ValidateChunk(int chunk)
    {
        if (chunk < 0 || chunk >= _resolutions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(chunk));
        }
    }

    private void ValidatePosition(int fromChunk)
    {
        if (fromChunk < -1 || fromChunk > _resolutions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(fromChunk));
        }
    }

    private static void ValidateResolution(MergeResolution resolution)
    {
        if (resolution < MergeResolution.Unresolved || resolution > MergeResolution.Custom)
        {
            throw new ArgumentOutOfRangeException(nameof(resolution));
        }
    }
}
