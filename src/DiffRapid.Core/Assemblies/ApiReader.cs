using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Threading;

namespace DiffRapid.Core.Assemblies;

/// <summary>
/// Reads the public API of a .NET assembly from its metadata, without loading it into the process.
/// Public and protected types and members are included; attributes are not.
/// </summary>
public static class ApiReader
{
    /// <summary>Reads the API of an assembly file.</summary>
    /// <exception cref="InvalidDataException">The file is not a .NET assembly.</exception>
    public static ApiSurface Read(string path, CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(path);
        return Read(stream, cancellationToken);
    }

    /// <summary>Reads the API of an assembly image; the stream is read to the end and left open.</summary>
    /// <exception cref="InvalidDataException">The image is not a .NET assembly.</exception>
    public static ApiSurface Read(Stream stream, CancellationToken cancellationToken = default)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        try
        {
            using var image = new PEReader(stream, PEStreamOptions.LeaveOpen | PEStreamOptions.PrefetchEntireImage);
            if (!image.HasMetadata)
            {
                throw new InvalidDataException("The file is not a .NET assembly (it has no metadata).");
            }

            var reader = image.GetMetadataReader();
            if (!reader.IsAssembly)
            {
                throw new InvalidDataException("The file is a .NET module, not an assembly.");
            }

            var info = ReadInfo(image, reader);
            var entries = new ApiSurfaceBuilder(reader).Build(cancellationToken);
            return new ApiSurface(info, entries);
        }
        catch (BadImageFormatException exception)
        {
            throw new InvalidDataException("The file is not a valid .NET assembly: " + exception.Message, exception);
        }
    }

    private static AssemblyInfo ReadInfo(PEReader image, MetadataReader reader)
    {
        var assembly = reader.GetAssemblyDefinition();
        var attributes = assembly.GetCustomAttributes();
        var publicKey = reader.GetBlobBytes(assembly.PublicKey);
        return new AssemblyInfo(
            reader.GetString(assembly.Name),
            assembly.Version.ToString(),
            MetadataNames.AttributeString(reader, attributes, "System.Reflection.AssemblyFileVersionAttribute"),
            MetadataNames.AttributeString(reader, attributes, "System.Reflection.AssemblyInformationalVersionAttribute"),
            MetadataNames.AttributeString(reader, attributes, "System.Runtime.Versioning.TargetFrameworkAttribute"),
            publicKey.Length == 0 ? null : Hex(TokenOf(publicKey)),
            Architecture(image.PEHeaders),
            ReadReferences(reader));
    }

    private static IReadOnlyList<string> ReadReferences(MetadataReader reader)
    {
        var references = new List<string>();
        foreach (var handle in reader.AssemblyReferences)
        {
            var reference = reader.GetAssemblyReference(handle);
            var name = reader.GetString(reference.Name) + ", Version=" + reference.Version;
            var culture = reader.GetString(reference.Culture);
            if (culture.Length > 0)
            {
                name += ", Culture=" + culture;
            }

            var key = reader.GetBlobBytes(reference.PublicKeyOrToken);
            if (key.Length > 0)
            {
                var isFullKey = (reference.Flags & AssemblyFlags.PublicKey) != 0;
                name += ", PublicKeyToken=" + Hex(isFullKey ? TokenOf(key) : key);
            }

            references.Add(name);
        }

        references.Sort(StringComparer.OrdinalIgnoreCase);
        return references.AsReadOnly();
    }

    private static string Architecture(PEHeaders headers)
    {
        var machine = headers.CoffHeader.Machine;
        var flags = headers.CorHeader?.Flags ?? 0;
        if (machine != Machine.I386 || (flags & CorFlags.ILOnly) == 0)
        {
            return machine.ToString();
        }

        if ((flags & CorFlags.Requires32Bit) != 0)
        {
            return (flags & CorFlags.Prefers32Bit) != 0 ? "AnyCPU (32-bit preferred)" : "x86";
        }

        return "AnyCPU";
    }

    // The token is the last eight bytes of the SHA-1 hash of the public key, in reverse order.
    private static byte[] TokenOf(byte[] publicKey)
    {
        using var sha1 = SHA1.Create();
        return Enumerable.Reverse(sha1.ComputeHash(publicKey)).Take(8).ToArray();
    }

    private static string Hex(byte[] bytes) => string.Concat(bytes.Select(value => value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
}
