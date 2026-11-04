using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiffRapid.Ai;
using DiffRapid.Core.Comparison;
using DiffRapid.Core.Output;
using DiffRapid.Core.Text;

namespace DiffRapid.Cli;

internal static partial class Program
{
    private static int RunExplain(string[] rawArgs)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            return RunExplainAsync(rawArgs, Console.Out, cancellationToken: cancellation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return Fail("AI request cancelled.");
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    internal static async Task<int> RunExplainAsync(string[] rawArgs, TextWriter output,
        HttpMessageHandler? handler = null, CancellationToken cancellationToken = default)
    {
        var arguments = ParseAiArguments(rawArgs);
        var endpoint = CreateAiEndpoint(arguments.Options);
        var conflicts = arguments.Options.ContainsKey("conflicts");
        if (arguments.Files.Count != (conflicts ? 1 : 2))
        {
            throw new UsageException("explain needs two files, or --conflicts and one file with conflict markers.");
        }

        var builder = new DiffPromptBuilder(endpoint);
        using var client = new ChatClient(endpoint, handler);
        var redact = !arguments.Options.ContainsKey("no-redact");
        if (conflicts)
        {
            var document = await ReadAiDocumentAsync(arguments.Files[0], cancellationToken).ConfigureAwait(false);
            var proposals = ReadAiConflicts(document, cancellationToken);
            output.WriteLine("Proposals only; review before applying. The input file is unchanged.");
            for (var index = 0; index < proposals.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var conflict = proposals[index];
                var prompt = builder.ProposeResolution(conflict.Base, conflict.Left, conflict.Right, conflict.Context, cancellationToken);
                var reply = await client.CompleteAsync(prompt, redact, cancellationToken).ConfigureAwait(false);
                var resolution = ResolutionParser.Parse(reply, cancellationToken);
                output.WriteLine($"Conflict {index + 1} (line {conflict.LineNumber}):");
                WriteAiResolution(output, resolution, endpoint.ApiKey, cancellationToken);
            }

            if (proposals.Count == 0)
            {
                output.WriteLine("No conflict markers found.");
            }
        }
        else
        {
            var left = await ReadAiDocumentAsync(arguments.Files[0], cancellationToken).ConfigureAwait(false);
            var right = await ReadAiDocumentAsync(arguments.Files[1], cancellationToken).ConfigureAwait(false);
            var comparison = TextComparer.Compare(left, right, cancellationToken: cancellationToken);
            var diff = UnifiedDiffWriter.Write(comparison, Path.GetFileName(arguments.Files[0]), Path.GetFileName(arguments.Files[1]));
            var prompt = builder.ExplainDiff(diff, Path.GetFileName(arguments.Files[0]), Path.GetFileName(arguments.Files[1]),
                cancellationToken: cancellationToken);
            var reply = await client.CompleteAsync(prompt, redact, cancellationToken).ConfigureAwait(false);
            output.WriteLine(HideAiCredential(reply, endpoint.ApiKey));
        }

        return ExitSame;
    }

    private static (List<string> Files, Dictionary<string, string?> Options) ParseAiArguments(string[] rawArgs)
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        var positionalOnly = false;
        for (var index = 1; index < rawArgs.Length; index++)
        {
            var argument = rawArgs[index];
            if (argument == "--")
            {
                positionalOnly = true;
                continue;
            }

            if (positionalOnly || !argument.StartsWith("--", StringComparison.Ordinal))
            {
                files.Add(argument);
                continue;
            }

            var separator = argument.IndexOf('=');
            var name = (separator < 0 ? argument.Substring(2) : argument.Substring(2, separator - 2)).ToLowerInvariant();
            if (name == "api-key")
            {
                throw new UsageException("Read an API key from an environment variable using --api-key-env <VAR>.");
            }

            if (name is "conflicts" or "no-redact")
            {
                if (separator >= 0)
                {
                    throw new UsageException($"--{name} is a switch and does not take a value.");
                }

                options[name] = null;
                continue;
            }

            if (name is not ("endpoint" or "model" or "api-key-env" or "azure-deployment" or "api-version"))
            {
                throw new UsageException("Unknown explain option. Run 'diffrapid --help' for the supported options.");
            }

            var value = separator < 0 ? null : argument.Substring(separator + 1);
            if (value is null && index + 1 < rawArgs.Length && !rawArgs[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = rawArgs[++index];
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new UsageException($"--{name} needs a value.");
            }

            options[name] = value;
        }

        return (files, options);
    }

    private static AiEndpoint CreateAiEndpoint(Dictionary<string, string?> options)
    {
        if (!options.TryGetValue("endpoint", out var address) || string.IsNullOrWhiteSpace(address))
        {
            throw new UsageException("No AI endpoint configured. Supply --endpoint <URL>; nothing has been sent.");
        }

        options.TryGetValue("model", out var model);
        options.TryGetValue("azure-deployment", out var deployment);
        options.TryGetValue("api-version", out var apiVersion);
        if (string.IsNullOrWhiteSpace(model) && string.IsNullOrWhiteSpace(deployment))
        {
            throw new UsageException("explain requires --model <name> (or --azure-deployment <name> for Azure OpenAI).");
        }

        if (apiVersion is not null && deployment is null)
        {
            throw new UsageException("--api-version requires --azure-deployment.");
        }

        string? key = null;
        if (options.TryGetValue("api-key-env", out var variable))
        {
            key = Environment.GetEnvironmentVariable(variable!);
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new UsageException("The environment variable named by --api-key-env is unset or empty.");
            }
        }

        return new AiEndpoint
        {
            BaseUrl = address!,
            Model = model ?? deployment!,
            ApiKey = key,
            Kind = deployment is null ? AiEndpointKind.OpenAi : AiEndpointKind.AzureOpenAi,
            Deployment = deployment,
            ApiVersion = apiVersion,
        };
    }

    private static async Task<TextDocument> ReadAiDocumentAsync(string path, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (text.IndexOf('\0') >= 0)
        {
            throw new UsageException("explain requires text files; the input contains binary data.");
        }

        var document = TextDocument.Parse(text);
        cancellationToken.ThrowIfCancellationRequested();
        return document;
    }

    private static List<AiConflictInput> ReadAiConflicts(TextDocument document, CancellationToken cancellationToken)
    {
        var conflicts = new List<AiConflictInput>();
        var left = new StringBuilder();
        var right = new StringBuilder();
        StringBuilder? original = null;
        var section = '\0';
        var start = 0;
        for (var index = 0; index < document.LineCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = document[index];
            var marker = AiConflictMarker(line.Content);
            switch (marker)
            {
                case '<' when section == '\0':
                    start = index;
                    left.Clear();
                    right.Clear();
                    original = null;
                    section = '<';
                    break;
                case '|' when section == '<':
                    original = new StringBuilder();
                    section = '|';
                    break;
                case '=' when section is '<' or '|':
                    section = '=';
                    break;
                case '>' when section == '=':
                    conflicts.Add(new AiConflictInput(original?.ToString(), left.ToString(), right.ToString(),
                        AiConflictContext(document, start, index, cancellationToken), start + 1));
                    section = '\0';
                    break;
                case '\0':
                    var target = section switch { '<' => left, '=' => right, '|' => original, _ => null };
                    target?.Append(line.Content).Append(line.EndingText);
                    break;
                default:
                    throw new FormatException($"Unexpected conflict marker at line {index + 1}.");
            }
        }

        if (section != '\0')
        {
            throw new FormatException($"Unterminated conflict starting at line {start + 1}.");
        }

        return conflicts;
    }

    private static string AiConflictContext(TextDocument document, int start, int end, CancellationToken cancellationToken)
    {
        var first = start;
        while (first > 0 && first > start - 3 && AiConflictMarker(document[first - 1].Content) == '\0')
        {
            first--;
        }

        var last = end;
        while (last + 1 < document.LineCount && last < end + 3 && AiConflictMarker(document[last + 1].Content) == '\0')
        {
            last++;
        }

        var context = new StringBuilder();
        for (var index = first; index <= last; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Append(document[index].Content).Append(document[index].EndingText);
        }

        return context.ToString();
    }

    private static char AiConflictMarker(string line)
    {
        if (line.Length < 7 || line[0] is not ('<' or '|' or '=' or '>'))
        {
            return '\0';
        }

        for (var index = 1; index < 7; index++)
        {
            if (line[index] != line[0])
            {
                return '\0';
            }
        }

        return line.Length == 7 || line[7] is ' ' or '\t' ? line[0] : '\0';
    }

    private static void WriteAiResolution(TextWriter output, AiResolution resolution, string? key, CancellationToken cancellationToken)
    {
        // Choose a fence longer than any backtick run in the proposed lines.
        var fenceLength = 3;
        foreach (var line in resolution.Lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var run = 0;
            foreach (var character in line)
            {
                run = character == '`' ? run + 1 : 0;
                fenceLength = Math.Max(fenceLength, run + 1);
            }
        }

        var fence = new string('`', fenceLength);
        output.WriteLine(fence);
        foreach (var line in resolution.Lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.WriteLine(HideAiCredential(line, key));
        }

        output.WriteLine(fence);
        output.WriteLine(HideAiCredential(resolution.Reasoning, key));
    }

    private static string HideAiCredential(string text, string? key) => string.IsNullOrEmpty(key) ? text : text.Replace(key, "[REDACTED]");

    private sealed record AiConflictInput(string? Base, string Left, string Right, string Context, int LineNumber);
}
