using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DiffRapid.Core.Folders;

/// <summary>
/// File and folder rules from a .flt filter file. File rules match the file name; folder rules match the
/// folder's relative path written with backslashes and a leading backslash (\src\bin). A kind of entry
/// with no rules is always included, so a file-only filter still descends into every folder.
/// </summary>
public sealed class FolderFilter
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private readonly Regex[] _files;
    private readonly Regex[] _folders;
    private readonly Regex[] _fileExceptions;
    private readonly Regex[] _folderExceptions;

    private FolderFilter(string name, string description, bool defaultInclude, IReadOnlyDictionary<string, List<Regex>> rules)
    {
        Name = name;
        Description = description;
        DefaultInclude = defaultInclude;
        _files = rules["f"].ToArray();
        _folders = rules["d"].ToArray();
        _fileExceptions = rules["xf"].ToArray();
        _folderExceptions = rules["xd"].ToArray();
    }

    /// <summary>The display name, or an empty string when absent.</summary>
    public string Name { get; }

    /// <summary>The description, or an empty string when absent.</summary>
    public string Description { get; }

    /// <summary>
    /// True for "def: include" (rules exclude entries), false for "def: exclude" (rules select the entries kept).
    /// </summary>
    public bool DefaultInclude { get; }

    /// <summary>Parses filter text. An unknown keyword or invalid regex throws a FormatException naming the line.</summary>
    public static FolderFilter Parse(string text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        var name = string.Empty;
        var description = string.Empty;
        var defaultInclude = true;
        var rules = new Dictionary<string, List<Regex>>
        {
            ["f"] = new(),
            ["d"] = new(),
            ["xf"] = new(),
            ["xd"] = new(),
        };

        using var reader = new StringReader(text);
        var lineNumber = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith("##", StringComparison.Ordinal))
            {
                continue;
            }

            var comment = line.IndexOf(" ##", StringComparison.Ordinal);
            if (comment >= 0)
            {
                line = line.Substring(0, comment).TrimEnd();
            }

            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                throw new FormatException($"Filter file line {lineNumber}: expected a rule such as \"f: \\.obj$\".");
            }

            var keyword = line.Substring(0, colon).Trim().ToLowerInvariant();
            var value = line.Substring(colon + 1).Trim();
            switch (keyword)
            {
                case "name":
                    name = value;
                    break;
                case "desc":
                    description = value;
                    break;
                case "def":
                    if (value.Equals("include", StringComparison.OrdinalIgnoreCase))
                    {
                        defaultInclude = true;
                    }
                    else if (value.Equals("exclude", StringComparison.OrdinalIgnoreCase))
                    {
                        defaultInclude = false;
                    }
                    else
                    {
                        throw new FormatException($"Filter file line {lineNumber}: \"def:\" must be include or exclude.");
                    }

                    break;
                case "f":
                case "d":
                case "xf":
                case "xd":
                    rules[keyword].Add(CreateRegex(value, lineNumber));
                    break;
                default:
                    throw new FormatException($"Filter file line {lineNumber}: unknown filter keyword \"{keyword}\".");
            }
        }

        return new FolderFilter(name, description, defaultInclude, rules);
    }

    /// <summary>Whether a file with this name is compared.</summary>
    public bool IsFileIncluded(string name)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        return IsIncluded(name, _files, _fileExceptions);
    }

    /// <summary>Whether a folder is compared (and descended into). Accepts / or \ separators.</summary>
    public bool IsFolderIncluded(string relativePath)
    {
        if (relativePath is null)
        {
            throw new ArgumentNullException(nameof(relativePath));
        }

        return IsIncluded("\\" + relativePath.Replace('/', '\\').TrimStart('\\'), _folders, _folderExceptions);
    }

    private bool IsIncluded(string value, Regex[] rules, Regex[] exceptions)
    {
        if (rules.Length == 0)
        {
            return true;
        }

        var matched = Matches(rules, value) && !Matches(exceptions, value);
        return DefaultInclude ? !matched : matched;
    }

    // A pattern that runs past its time limit counts as not matching rather than failing the whole scan.
    private static bool Matches(Regex[] patterns, string value)
    {
        foreach (var pattern in patterns)
        {
            try
            {
                if (pattern.IsMatch(value))
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }

        return false;
    }

    private static Regex CreateRegex(string pattern, int lineNumber)
    {
        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"Filter file line {lineNumber}: invalid regular expression: {ex.Message}", ex);
        }
    }
}
