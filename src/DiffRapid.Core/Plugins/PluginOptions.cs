using System;
using System.Text;

namespace DiffRapid.Core.Plugins;

/// <summary>The unpacker and prediffer pipelines to run on each file before it is compared.</summary>
public sealed record PluginOptions
{
    /// <summary>The timeout for external commands when none is set.</summary>
    public static readonly TimeSpan DefaultExternalCommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The <see cref="Unpacker"/> value that picks the first unpacker whose file pattern matches.</summary>
    public const string AutomaticUnpacker = "auto";

    /// <summary>
    /// The unpacker pipeline, such as <c>ZipEntries</c> or <c>PrettifyJson | SortLines</c>, or
    /// <see cref="AutomaticUnpacker"/>. Null reads the file as text.
    /// </summary>
    public string? Unpacker { get; init; }

    /// <summary>The prediffer pipeline applied to the unpacked text; null for none.</summary>
    public string? Prediffer { get; init; }

    /// <summary>How long one external command may run.</summary>
    public TimeSpan ExternalCommandTimeout { get; init; } = DefaultExternalCommandTimeout;

    /// <summary>Forces the encoding used to read the file; null detects it.</summary>
    public Encoding? Encoding { get; init; }

    /// <summary>The plugins to resolve names against; null uses <see cref="PluginRegistry.Default"/>.</summary>
    public PluginRegistry? Registry { get; init; }

    /// <summary>True when neither pipeline is set.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Unpacker) && string.IsNullOrWhiteSpace(Prediffer);
}
