using System.Collections.Generic;

namespace DiffRapid.Ai;

/// <summary>Instructions and source material for a single chat request.</summary>
public sealed record AiPrompt(string SystemMessage, string UserMessage);

/// <summary>A file comparison supplied to a folder explanation in caller-selected priority order.</summary>
public sealed record AiFileDiff(string LeftFile, string RightFile, string UnifiedDiff, string? Language = null);

/// <summary>Proposed replacement lines and their explanation; callers must review before applying.</summary>
public sealed record AiResolution(IReadOnlyList<string> Lines, string Reasoning);
