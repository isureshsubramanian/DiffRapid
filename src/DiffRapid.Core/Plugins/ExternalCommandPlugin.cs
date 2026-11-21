using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using DiffRapid.Core.Text;
using DiffRapid.Core.VersionControl;

namespace DiffRapid.Core.Plugins;

/// <summary>
/// A user command used as an unpacker and a prediffer. The program is started directly, never through a
/// shell. The input goes to its standard input, or, when an argument contains <see cref="FilePlaceholder"/>,
/// into a temporary file whose path replaces the placeholder. Standard output is the result; a non-zero
/// exit code or running past the timeout is an error.
/// </summary>
public sealed class ExternalCommandPlugin : IUnpacker, IPrediffer
{
    /// <summary>The argument text replaced by the path of a temporary file holding the input.</summary>
    public const string FilePlaceholder = "{file}";

    private readonly Regex? _filePattern;

    /// <param name="name">The name used in pipelines.</param>
    /// <param name="program">The executable path or a name found on PATH.</param>
    /// <param name="arguments">Arguments placed before any arguments written in the pipeline step.</param>
    /// <param name="filePatterns">Wildcards such as <c>*.pdf;*.rtf</c> for automatic selection; null for none.</param>
    /// <param name="description">The help text; a default naming the program is used when null.</param>
    public ExternalCommandPlugin(string name, string program, IReadOnlyList<string>? arguments = null,
        string? filePatterns = null, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A plugin needs a name.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(program))
        {
            throw new ArgumentException("An external command needs a program.", nameof(program));
        }

        Name = name;
        Program = program;
        Arguments = arguments ?? Array.Empty<string>();
        FilePatterns = filePatterns;
        Description = description ?? "Runs " + program + ".";
        _filePattern = filePatterns is null ? null : WildcardRegex(filePatterns);
    }

    public string Name { get; }

    public string Description { get; }

    public string ArgumentsHelp => "[extra arguments]";

    /// <summary>The program to start.</summary>
    public string Program { get; }

    /// <summary>The fixed arguments, which may contain <see cref="FilePlaceholder"/>.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>The wildcards used by <see cref="Matches"/>, separated by semicolons.</summary>
    public string? FilePatterns { get; }

    public bool Matches(string fileName) => _filePattern is not null && _filePattern.IsMatch(Path.GetFileName(fileName));

    public UnpackResult Unpack(Stream input, PluginContext context)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        return new UnpackResult(ExternalCommand.Run(Program, Arguments.Concat(context.Arguments).ToList(), buffer.ToArray(), context));
    }

    public string Transform(string text, PluginContext context)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        var input = new UTF8Encoding(false).GetBytes(text);
        return ExternalCommand.Run(Program, Arguments.Concat(context.Arguments).ToList(), input, context);
    }

    private static Regex WildcardRegex(string patterns)
    {
        var alternatives = patterns
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(pattern => Regex.Escape(pattern.Trim()).Replace(@"\*", ".*").Replace(@"\?", "."));
        return new Regex("^(?:" + string.Join("|", alternatives) + ")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

/// <summary>The built-in <c>Exec</c> step: <c>Exec &lt;program&gt; [arguments]</c>, with no file pattern.</summary>
internal sealed class ExecPlugin : IUnpacker, IPrediffer
{
    public string Name => "Exec";

    public string Description =>
        "Runs a program (no shell) with the text on standard input, or in a temp file named by {file}; uses its output.";

    public string ArgumentsHelp => "<program> [arguments, {file} for a temp file]";

    public bool Matches(string fileName) => false;

    public UnpackResult Unpack(Stream input, PluginContext context)
    {
        var (program, arguments) = Split(context);
        return new ExternalCommandPlugin(Name, program, arguments).Unpack(input, WithoutArguments(context));
    }

    public string Transform(string text, PluginContext context)
    {
        var (program, arguments) = Split(context);
        return new ExternalCommandPlugin(Name, program, arguments).Transform(text, WithoutArguments(context));
    }

    private static (string Program, IReadOnlyList<string> Arguments) Split(PluginContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw new ArgumentException("Exec needs a program to run.");
        }

        return (context.Arguments[0], context.Arguments.Skip(1).ToList());
    }

    private static PluginContext WithoutArguments(PluginContext context) =>
        new(context.FileName, Array.Empty<string>(), context.ExternalCommandTimeout, context.CancellationToken);
}

/// <summary>
/// Starts a program, feeds it input and collects its output within a time limit, through the shared
/// <see cref="ProcessRunner"/>: output past <see cref="ProcessRunner.DefaultOutputLimit"/> stops the program, and a
/// stopped program has exited before its temporary file is deleted.
/// </summary>
internal static class ExternalCommand
{
    // Estimate: a just-stopped program (or a grandchild that outlived it) can hold the temporary file open for a
    // moment on Windows; a few short retries cover that without delaying the result noticeably.
    private const int DeleteAttempts = 5;
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(100);

    public static string Run(string program, IReadOnlyList<string> arguments, byte[] input, PluginContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var usesFile = arguments.Any(argument => argument.Contains(ExternalCommandPlugin.FilePlaceholder));
        string? tempFile = null;
        try
        {
            if (usesFile)
            {
                tempFile = CreateTempFile(input, context.FileName);
            }

            var finalArguments = arguments
                .Select(argument => tempFile is null ? argument : argument.Replace(ExternalCommandPlugin.FilePlaceholder, tempFile))
                .ToList();
            return Execute(program, finalArguments, usesFile ? null : input, context);
        }
        finally
        {
            DeleteQuietly(tempFile);
        }
    }

    private static string Execute(string program, IReadOnlyList<string> arguments, byte[]? standardInput, PluginContext context)
    {
        ProcessResult result;
        try
        {
            result = ProcessRunner.RunAsync(program, null, arguments, standardInput, ProcessRunner.DefaultOutputLimit,
                context.ExternalCommandTimeout, context.CancellationToken).GetAwaiter().GetResult();
        }
        catch (TimeoutException ex)
        {
            throw new PluginException(ex.Message, ex);
        }
        catch (IOException ex)
        {
            throw new PluginException(ex.Message, ex);
        }

        if (result.ExitCode != 0)
        {
            var error = result.Error.Trim();
            var detail = error.Length == 0 ? string.Empty : ": " + (error.Length > 500 ? error.Substring(0, 500) + "..." : error);
            throw new PluginException($"{program} exited with code {result.ExitCode}{detail}");
        }

        return TextFileReader.Decode(result.Output).Text;
    }

    private static string CreateTempFile(byte[] input, string fileName)
    {
        var extension = string.IsNullOrEmpty(fileName) ? ".txt" : Path.GetExtension(fileName);
        var path = Path.Combine(Path.GetTempPath(), "diffrapid-" + Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, input);
        return path;
    }

    private static void DeleteQuietly(string? path)
    {
        if (path is null)
        {
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= DeleteAttempts)
                {
                    return;
                }

                Thread.Sleep(DeleteRetryDelay);
            }
        }
    }
}
