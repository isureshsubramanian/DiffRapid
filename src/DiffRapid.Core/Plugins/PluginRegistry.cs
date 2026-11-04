using System;
using System.Collections.Generic;
using System.Linq;

namespace DiffRapid.Core.Plugins;

/// <summary>The unpackers and prediffers a pipeline can name, with the built-ins registered first.</summary>
public sealed class PluginRegistry
{
    private readonly List<IUnpacker> _unpackers = [];
    private readonly List<IPrediffer> _prediffers = [];

    /// <summary>A shared registry holding only the built-in plugins. Do not register into it.</summary>
    public static PluginRegistry Default { get; } = CreateDefault();

    /// <summary>The registered unpackers in registration order.</summary>
    public IReadOnlyList<IUnpacker> Unpackers => _unpackers;

    /// <summary>The registered prediffers in registration order.</summary>
    public IReadOnlyList<IPrediffer> Prediffers => _prediffers;

    /// <summary>Creates a new registry holding the built-in plugins.</summary>
    public static PluginRegistry CreateDefault()
    {
        var registry = new PluginRegistry();
        foreach (var plugin in BuiltInPlugins.Create())
        {
            registry.Register(plugin);
        }

        return registry;
    }

    /// <summary>
    /// Adds an unpacker, a prediffer, or a plugin that is both. A plugin with the same name replaces the
    /// earlier one of that kind.
    /// </summary>
    public void Register(IPlugin plugin)
    {
        if (plugin is null)
        {
            throw new ArgumentNullException(nameof(plugin));
        }

        if (plugin is not IUnpacker && plugin is not IPrediffer)
        {
            throw new ArgumentException("A plugin must be an unpacker, a prediffer, or both.", nameof(plugin));
        }

        if (plugin is IUnpacker unpacker)
        {
            Replace(_unpackers, unpacker);
        }

        if (plugin is IPrediffer prediffer)
        {
            Replace(_prediffers, prediffer);
        }
    }

    /// <summary>Finds an unpacker by name, ignoring case.</summary>
    public IUnpacker? FindUnpacker(string name) => _unpackers.FirstOrDefault(plugin => IsNamed(plugin, name));

    /// <summary>Finds a prediffer by name, ignoring case.</summary>
    public IPrediffer? FindPrediffer(string name) => _prediffers.FirstOrDefault(plugin => IsNamed(plugin, name));

    /// <summary>Returns the first unpacker whose file pattern matches the file name, or null.</summary>
    public IUnpacker? SelectUnpacker(string fileName) => _unpackers.FirstOrDefault(plugin => plugin.Matches(fileName));

    private static bool IsNamed(IPlugin plugin, string name) => string.Equals(plugin.Name, name, StringComparison.OrdinalIgnoreCase);

    private static void Replace<T>(List<T> plugins, T plugin)
        where T : IPlugin
    {
        var index = plugins.FindIndex(existing => IsNamed(existing, plugin.Name));
        if (index >= 0)
        {
            plugins[index] = plugin;
        }
        else
        {
            plugins.Add(plugin);
        }
    }
}
