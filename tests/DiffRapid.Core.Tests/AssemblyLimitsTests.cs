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

    [Fact]
    public void DeeplyNestedFieldSignatureIsRejectedNotOverflowed()
    {
        // List<List<...<int>>> nested 3,000 deep overflowed the recursive signature decoder.
        using var image = new MemoryStream(Build((metadata, objectType) => AddFieldType(metadata, objectType, NestedGeneric(metadata, 3000, fieldHeader: true))));

        var error = Assert.Throws<InvalidDataException>(() => ApiReader.Read(image, TestContext.Current.CancellationToken));
        Assert.Contains("levels deep", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x0F)]
    [InlineData(0x10)]
    [InlineData(0x1D)]
    public void DeeplyWrappedFieldSignatureIsRejected(byte wrapper)
    {
        var signature = new BlobBuilder();
        signature.WriteByte(0x06);
        for (var level = 0; level < 3000; level++)
        {
            signature.WriteByte(wrapper);
        }

        signature.WriteByte(0x08);
        using var image = new MemoryStream(Build((metadata, objectType) => AddFieldType(metadata, objectType, signature)));

        Assert.Throws<InvalidDataException>(() => ApiReader.Read(image, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DeeplyNestedBaseTypeSpecificationIsRejected()
    {
        using var image = new MemoryStream(Build((metadata, objectType) =>
        {
            var spec = NestedGeneric(metadata, 3000, fieldHeader: false);
            AddType(metadata, TypeAttributes.Public, "Derived", metadata.AddTypeSpecification(metadata.GetOrAddBlob(spec)));
        }));

        Assert.Throws<InvalidDataException>(() => ApiReader.Read(image, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TruncatedSignatureIsRejected()
    {
        var signature = new BlobBuilder();
        signature.WriteByte(0x06);
        signature.WriteByte(0x15);
        using var image = new MemoryStream(Build((metadata, objectType) => AddFieldType(metadata, objectType, signature)));

        Assert.Throws<InvalidDataException>(() => ApiReader.Read(image, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ModifierNamingItsOwnTypeSpecificationIsNotFollowed()
    {
        using var image = new MemoryStream(Build((metadata, objectType) =>
        {
            // Type specification 1 is "modopt(type specification 1) int": a decoder that followed it would never stop.
            var spec = new BlobBuilder();
            spec.WriteByte(0x20);
            spec.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(MetadataTokens.TypeSpecificationHandle(1)));
            spec.WriteByte(0x08);
            AddType(metadata, TypeAttributes.Public, "Derived", metadata.AddTypeSpecification(metadata.GetOrAddBlob(spec)));
        }));

        Assert.NotEmpty(ApiReader.Read(image, TestContext.Current.CancellationToken).Entries);
    }

    [Fact]
    public void NestingWithinTheLimitStillReads()
    {
        using var image = new MemoryStream(Build((metadata, objectType) => AddFieldType(metadata, objectType, NestedGeneric(metadata, 20, fieldHeader: true))));

        var surface = ApiReader.Read(image, TestContext.Current.CancellationToken);
        Assert.Contains(surface.Entries, entry => entry.Declaration.Contains("List<System.Collections.Generic.List<", StringComparison.Ordinal));
    }

    [Fact]
    public void RealAssembliesStillRead()
    {
        // The framework's core library and this test assembly hold every signature form compilers emit.
        using var core = File.OpenRead(typeof(object).Assembly.Location);
        Assert.NotEmpty(ApiReader.Read(core, TestContext.Current.CancellationToken).Entries);
        using var tests = File.OpenRead(typeof(AssemblyLimitsTests).Assembly.Location);
        Assert.NotEmpty(ApiReader.Read(tests, TestContext.Current.CancellationToken).Entries);
    }

    [Fact]
    public void DeeplyNestedAttributeValueIsNotDecoded()
    {
        using var image = new MemoryStream(Build((metadata, objectType) =>
        {
            // A NullableAttribute look-alike whose constructor takes object, given object[] { object[] { ... } } 100,000 deep.
            var constructorSignature = new BlobBuilder();
            constructorSignature.WriteBytes(new byte[] { 0x20, 0x01, 0x01, 0x1C });
            var constructor = metadata.AddMethodDefinition(
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                MethodImplAttributes.IL,
                metadata.GetOrAddString(".ctor"),
                metadata.GetOrAddBlob(constructorSignature),
                -1,
                MetadataTokens.ParameterHandle(1));
            var fieldSignature = new BlobBuilder();
            fieldSignature.WriteBytes(new byte[] { 0x06, 0x08 });
            AddFieldType(metadata, objectType, fieldSignature);
            metadata.AddTypeDefinition(
                TypeAttributes.Public | TypeAttributes.Class,
                metadata.GetOrAddString("System.Runtime.CompilerServices"),
                metadata.GetOrAddString("NullableAttribute"),
                objectType,
                MetadataTokens.FieldDefinitionHandle(2),
                constructor);
            var value = new BlobBuilder();
            value.WriteUInt16(1);
            for (var level = 0; level < 100_000; level++)
            {
                value.WriteBytes(new byte[] { 0x1D, 0x51 });
                value.WriteInt32(1);
            }

            value.WriteByte(0x08);
            value.WriteInt32(0);
            value.WriteUInt16(0);
            metadata.AddCustomAttribute(MetadataTokens.FieldDefinitionHandle(1), constructor, metadata.GetOrAddBlob(value));
        }));

        Assert.Contains(ApiReader.Read(image, TestContext.Current.CancellationToken).Entries, entry => entry.MemberName == "F");
    }

    // A field signature (or, without the header, a type specification) of List`1 instantiations nested around int.
    private static BlobBuilder NestedGeneric(MetadataBuilder metadata, int depth, bool fieldHeader)
    {
        var list = metadata.AddTypeReference(
            MetadataTokens.AssemblyReferenceHandle(1), metadata.GetOrAddString("System.Collections.Generic"), metadata.GetOrAddString("List`1"));
        var signature = new BlobBuilder();
        if (fieldHeader)
        {
            signature.WriteByte(0x06);
        }

        for (var level = 0; level < depth; level++)
        {
            signature.WriteByte(0x15);
            signature.WriteByte(0x12);
            signature.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(list));
            signature.WriteCompressedInteger(1);
        }

        signature.WriteByte(0x08);
        return signature;
    }

    private static void AddFieldType(MetadataBuilder metadata, EntityHandle objectType, BlobBuilder signature)
    {
        var field = metadata.AddFieldDefinition(FieldAttributes.Public, metadata.GetOrAddString("F"), metadata.GetOrAddBlob(signature));
        metadata.AddTypeDefinition(
            TypeAttributes.Public | TypeAttributes.Class,
            metadata.GetOrAddString("Crafted"),
            metadata.GetOrAddString("Holder"),
            objectType,
            field,
            MetadataTokens.MethodDefinitionHandle(1));
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
