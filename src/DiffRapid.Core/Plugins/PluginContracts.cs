using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace DiffRapid.Core.Plugins;

/// <summary>What every unpacker and prediffer describes about itself.</summary>
public interface IPlugin
{
    /// <summary>The name used in a pipeline, matched without regard to case.</summary>
    string Name { get; }

    /// <summary>One line saying what the plugin does.</summary>
    string Description { get; }

    /// <summary>The arguments the plugin accepts, for help text; empty when it takes none.</summary>
    string ArgumentsHelp { get; }
}

/// <summary>Turns the bytes of a file into comparable text, or into a set of named entries.</summary>
public interface IUnpacker : IPlugin
{
    /// <summary>Returns whether this unpacker is chosen automatically for the file name.</summary>
    bool Matches(string fileName);

    /// <summary>Reads the file content and returns its text form.</summary>
    UnpackResult Unpack(Stream input, PluginContext context);
}

/// <summary>Transforms text before it is compared, while the original text is what the user sees.</summary>
public interface IPrediffer : IPlugin
{
    /// <summary>Returns the transformed text.</summary>
    string Transform(string text, PluginContext context);
}

/// <summary>What one pipeline step runs with: its arguments, the file and the limits.</summary>
public sealed class PluginContext
{
    public PluginContext(string fileName, IReadOnlyList<string> arguments, TimeSpan externalCommandTimeout, CancellationToken cancellationToken)
    {
        FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
        ExternalCommandTimeout = externalCommandTimeout;
        CancellationToken = cancellationToken;
    }

    /// <summary>The name (or path) of the file being prepared; used for extensions and messages.</summary>
    public string FileName { get; }

    /// <summary>The step's arguments as written in the pipeline.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>How long an external command may run before it is stopped.</summary>
    public TimeSpan ExternalCommandTimeout { get; }

    public CancellationToken CancellationToken { get; }

    /// <summary>Creates a context with only arguments, for direct calls and tests.</summary>
    public static PluginContext ForArguments(params string[] arguments) =>
        new(string.Empty, arguments, PluginOptions.DefaultExternalCommandTimeout, CancellationToken.None);
}

/// <summary>The text an unpacker produced, plus the entries when the file is a container.</summary>
public sealed class UnpackResult
{
    public UnpackResult(string text, IReadOnlyList<UnpackedEntry>? entries = null)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Entries = entries ?? Array.Empty<UnpackedEntry>();
    }

    /// <summary>The comparable text of the whole file.</summary>
    public string Text { get; }

    /// <summary>The named entries of a container file; empty for a single document.</summary>
    public IReadOnlyList<UnpackedEntry> Entries { get; }
}

/// <summary>One entry of a container file, such as a file inside a zip archive.</summary>
/// <param name="Name">The entry path inside the container, with forward slashes.</param>
/// <param name="Length">The uncompressed size in bytes.</param>
/// <param name="Text">The decoded text, or null for a folder, a binary entry or one too large to read.</param>
/// <param name="IsBinary">True when the entry content is not text.</param>
public sealed record UnpackedEntry(string Name, long Length, string? Text, bool IsBinary);

/// <summary>A plugin could not do its work: bad input, a failing external command, or a timeout.</summary>
public sealed class PluginException : Exception
{
    public PluginException(string message)
        : base(message)
    {
    }

    public PluginException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
