using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DiffRapid.Core.Assemblies;

namespace DiffRapid.Core.Tests;

/// <summary>Crafted assemblies whose metadata chains are circular must be rejected, not recursed forever.</summary>
public sealed class AssemblyLimitsTests
{
    [Theory]
    [InlineData(TypeAttributes.NestedPublic)]
    [InlineData(TypeAttributes.NestedAssembly)]
    public void CircularDeclaringTypesAreRejected(TypeAttributes visibility)
    {
        using var image = new MemoryStream(Build((metadata, objectType) =>
        {
            var first = AddType(metadata, visibility, "First", objectType);
            var second = AddType(metadata, visibility, "Second", objectType);
            metadata.AddNestedType(first, second);
            metadata.AddNestedType(second, first);
        }));

        var error = Assert.Throws<InvalidDataException>(() => ApiReader.Read(image, TestContext.Current.CancellationToken));
        Assert.Contains("circular", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CircularTypeReferenceScopesAreRejected()
    {
        using var image = new MemoryStream(Build((metadata, objectType) =>
        {
            // Rows 2 and 3 name each other as their resolution scope (row 1 is System.Object).
            var looped = metadata.AddTypeReference(MetadataTokens.TypeReferenceHandle(3), default, metadata.GetOrAddString("Inner"));
            metadata.AddTypeReference(looped, default, metadata.GetOrAddString("Outer"));
            AddType(metadata, TypeAttributes.Public, "Derived", looped);
        }));

        var error = Assert.Throws<InvalidDataException>(() => ApiReader.Read(image, TestContext.Current.CancellationToken));
        Assert.Contains("circular", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepButFiniteNestingIsRejectedPastTheLimit()
    {
        Assert.Throws<InvalidDataException>(() => MetadataNames.CheckNestingDepth(MetadataNames.MaxNestingDepth + 1));
        MetadataNames.CheckNestingDepth(MetadataNames.MaxNestingDepth);
    }

    private static TypeDefinitionHandle AddType(MetadataBuilder metadata, TypeAttributes visibility, string name, EntityHandle baseType) =>
        metadata.AddTypeDefinition(
            visibility | TypeAttributes.Class,
            metadata.GetOrAddString("Crafted"),
            metadata.GetOrAddString(name),
            baseType,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));

    private static byte[] Build(Action<MetadataBuilder, EntityHandle> addTypes)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString("Crafted.dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString("Crafted"), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.Sha1);
        var corlib = metadata.AddAssemblyReference(metadata.GetOrAddString("mscorlib"), new Version(4, 0, 0, 0), default, default, 0, default);
        var objectType = metadata.AddTypeReference(corlib, metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));
        metadata.AddTypeDefinition(
            default,
            default,
            metadata.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));
        addTypes(metadata, objectType);

        var builder = new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(metadata), new BlobBuilder());
        var output = new BlobBuilder();
        builder.Serialize(output);
        return output.ToArray();
    }
}
