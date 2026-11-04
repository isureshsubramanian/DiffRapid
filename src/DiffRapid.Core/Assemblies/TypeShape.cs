using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection.Metadata;

namespace DiffRapid.Core.Assemblies;

internal enum TypeShapeKind
{
    Primitive,
    Named,
    Generic,
    Array,
    Pointer,
    ByRef,
    TypeParameter,
    FunctionPointer,
    Modified,
}

/// <summary>
/// A decoded type from a signature. It is kept as a tree so that nullable annotations, which are stored
/// as a pre-order list of flags, can be applied while the type is written.
/// </summary>
internal sealed class TypeShape
{
    private TypeShape(TypeShapeKind kind, string name, bool isValueType, TypeShape? element, IReadOnlyList<TypeShape> arguments, int rank)
    {
        Kind = kind;
        Name = name;
        IsValueType = isValueType;
        Element = element;
        Arguments = arguments;
        Rank = rank;
    }

    public TypeShapeKind Kind { get; }

    /// <summary>
    /// The keyword of a primitive, the full name of a named type (nested types joined by '.', generic
    /// arity kept as `N), the name of a type parameter, or the full name of a custom modifier.
    /// </summary>
    public string Name { get; }

    public bool IsValueType { get; }

    /// <summary>The element of an array, pointer or by-ref, the definition of a generic instance, or the modified type.</summary>
    public TypeShape? Element { get; }

    public IReadOnlyList<TypeShape> Arguments { get; }

    public int Rank { get; }

    public static TypeShape Primitive(string keyword, bool isValueType) =>
        new(TypeShapeKind.Primitive, keyword, isValueType, null, Array.Empty<TypeShape>(), 0);

    public static TypeShape Named(string fullName, bool isValueType) =>
        new(TypeShapeKind.Named, fullName, isValueType, null, Array.Empty<TypeShape>(), 0);

    public static TypeShape Generic(TypeShape definition, IReadOnlyList<TypeShape> arguments) =>
        new(TypeShapeKind.Generic, definition.Name, definition.IsValueType, definition, arguments, 0);

    public static TypeShape ArrayOf(TypeShape element, int rank) =>
        new(TypeShapeKind.Array, string.Empty, false, element, Array.Empty<TypeShape>(), rank);

    public static TypeShape PointerTo(TypeShape element) =>
        new(TypeShapeKind.Pointer, string.Empty, true, element, Array.Empty<TypeShape>(), 0);

    public static TypeShape ByRef(TypeShape element) =>
        new(TypeShapeKind.ByRef, string.Empty, false, element, Array.Empty<TypeShape>(), 0);

    public static TypeShape TypeParameter(string name) =>
        new(TypeShapeKind.TypeParameter, name, false, null, Array.Empty<TypeShape>(), 0);

    public static TypeShape FunctionPointer(string text) =>
        new(TypeShapeKind.FunctionPointer, text, true, null, Array.Empty<TypeShape>(), 0);

    public static TypeShape Modified(string modifier, TypeShape unmodified) =>
        new(TypeShapeKind.Modified, modifier, unmodified.IsValueType, unmodified, Array.Empty<TypeShape>(), 0);

    /// <summary>Removes custom modifiers from the outside of the type.</summary>
    public TypeShape Unmodified()
    {
        var shape = this;
        while (shape.Kind == TypeShapeKind.Modified)
        {
            shape = shape.Element!;
        }

        return shape;
    }

    /// <summary>Whether a custom modifier with the given full name wraps this type.</summary>
    public bool HasModifier(string modifier)
    {
        for (var shape = this; shape.Kind == TypeShapeKind.Modified; shape = shape.Element!)
        {
            if (shape.Name == modifier)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>The names of the type parameters in scope for a signature.</summary>
internal sealed class GenericScope
{
    public static readonly GenericScope Empty = new(Array.Empty<string>(), Array.Empty<string>());

    public GenericScope(IReadOnlyList<string> typeParameters, IReadOnlyList<string> methodParameters)
    {
        TypeParameters = typeParameters;
        MethodParameters = methodParameters;
    }

    public IReadOnlyList<string> TypeParameters { get; }

    public IReadOnlyList<string> MethodParameters { get; }

    public GenericScope WithMethodParameters(IReadOnlyList<string> methodParameters) => new(TypeParameters, methodParameters);
}

/// <summary>Builds <see cref="TypeShape"/> trees from signatures and custom attribute blobs.</summary>
internal sealed class TypeShapeProvider : ISignatureTypeProvider<TypeShape, GenericScope>, ICustomAttributeTypeProvider<TypeShape>
{
    private const byte ValueTypeKind = 0x11;
    private const byte ClassKind = 0x12;

    public static readonly TypeShapeProvider Instance = new();

    public TypeShape GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => TypeShape.Primitive("bool", true),
        PrimitiveTypeCode.Byte => TypeShape.Primitive("byte", true),
        PrimitiveTypeCode.Char => TypeShape.Primitive("char", true),
        PrimitiveTypeCode.Double => TypeShape.Primitive("double", true),
        PrimitiveTypeCode.Int16 => TypeShape.Primitive("short", true),
        PrimitiveTypeCode.Int32 => TypeShape.Primitive("int", true),
        PrimitiveTypeCode.Int64 => TypeShape.Primitive("long", true),
        PrimitiveTypeCode.IntPtr => TypeShape.Primitive("nint", true),
        PrimitiveTypeCode.Object => TypeShape.Primitive("object", false),
        PrimitiveTypeCode.SByte => TypeShape.Primitive("sbyte", true),
        PrimitiveTypeCode.Single => TypeShape.Primitive("float", true),
        PrimitiveTypeCode.String => TypeShape.Primitive("string", false),
        PrimitiveTypeCode.TypedReference => TypeShape.Primitive("System.TypedReference", true),
        PrimitiveTypeCode.UInt16 => TypeShape.Primitive("ushort", true),
        PrimitiveTypeCode.UInt32 => TypeShape.Primitive("uint", true),
        PrimitiveTypeCode.UInt64 => TypeShape.Primitive("ulong", true),
        PrimitiveTypeCode.UIntPtr => TypeShape.Primitive("nuint", true),
        PrimitiveTypeCode.Void => TypeShape.Primitive("void", true),
        _ => TypeShape.Primitive(typeCode.ToString(), true),
    };

    public TypeShape GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var isValueType = rawTypeKind == ValueTypeKind || (rawTypeKind != ClassKind && MetadataNames.IsValueType(reader, handle));
        return TypeShape.Named(MetadataNames.FullName(reader, handle), isValueType);
    }

    public TypeShape GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
        TypeShape.Named(MetadataNames.FullName(reader, handle), rawTypeKind == ValueTypeKind);

    public TypeShape GetTypeFromSpecification(MetadataReader reader, GenericScope genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public TypeShape GetSZArrayType(TypeShape elementType) => TypeShape.ArrayOf(elementType, 1);

    public TypeShape GetArrayType(TypeShape elementType, ArrayShape shape) => TypeShape.ArrayOf(elementType, shape.Rank);

    public TypeShape GetByReferenceType(TypeShape elementType) => TypeShape.ByRef(elementType);

    public TypeShape GetPointerType(TypeShape elementType) => TypeShape.PointerTo(elementType);

    public TypeShape GetPinnedType(TypeShape elementType) => elementType;

    public TypeShape GetGenericInstantiation(TypeShape genericType, ImmutableArray<TypeShape> typeArguments) =>
        TypeShape.Generic(genericType, typeArguments);

    public TypeShape GetGenericMethodParameter(GenericScope genericContext, int index) =>
        TypeShape.TypeParameter(index < genericContext.MethodParameters.Count ? genericContext.MethodParameters[index] : "!!" + index);

    public TypeShape GetGenericTypeParameter(GenericScope genericContext, int index) =>
        TypeShape.TypeParameter(index < genericContext.TypeParameters.Count ? genericContext.TypeParameters[index] : "!" + index);

    public TypeShape GetModifiedType(TypeShape modifier, TypeShape unmodifiedType, bool isRequired) =>
        TypeShape.Modified(modifier.Name, unmodifiedType);

    public TypeShape GetFunctionPointerType(MethodSignature<TypeShape> signature)
    {
        var types = signature.ParameterTypes.Append(signature.ReturnType).Select(TypeShapeWriter.WriteOblivious);
        return TypeShape.FunctionPointer("delegate*<" + string.Join(", ", types) + ">");
    }

    public TypeShape GetSystemType() => TypeShape.Named("System.Type", false);

    public bool IsSystemType(TypeShape type) => type.Kind == TypeShapeKind.Named && type.Name == "System.Type";

    public TypeShape GetTypeFromSerializedName(string name) => TypeShape.Named(name, false);

    // Only attributes with primitive, string and array arguments are decoded, so any enum is assumed to be int.
    public PrimitiveTypeCode GetUnderlyingEnumType(TypeShape type) => PrimitiveTypeCode.Int32;
}

/// <summary>Reads nullable flags in the order the compiler stored them.</summary>
internal sealed class NullableFlags
{
    private readonly IReadOnlyList<byte>? _flags;
    private readonly byte _fallback;
    private int _index;

    public NullableFlags(IReadOnlyList<byte>? flags, byte fallback)
    {
        _flags = flags;
        _fallback = fallback;
    }

    public static NullableFlags Oblivious => new(null, 0);

    /// <summary>Returns the next flag: 0 oblivious, 1 not annotated, 2 annotated.</summary>
    public byte Next()
    {
        if (_flags is null || _flags.Count == 0)
        {
            return _fallback;
        }

        // A single flag applies to every type in the signature.
        if (_flags.Count == 1)
        {
            return _flags[0];
        }

        return _index < _flags.Count ? _flags[_index++] : _fallback;
    }
}

/// <summary>Writes a <see cref="TypeShape"/> as C# source text.</summary>
internal static class TypeShapeWriter
{
    private const byte Annotated = 2;

    private static readonly Dictionary<string, string> Keywords = new(StringComparer.Ordinal)
    {
        ["System.Object"] = "object",
        ["System.String"] = "string",
        ["System.Boolean"] = "bool",
        ["System.Byte"] = "byte",
        ["System.SByte"] = "sbyte",
        ["System.Char"] = "char",
        ["System.Int16"] = "short",
        ["System.UInt16"] = "ushort",
        ["System.Int32"] = "int",
        ["System.UInt32"] = "uint",
        ["System.Int64"] = "long",
        ["System.UInt64"] = "ulong",
        ["System.Single"] = "float",
        ["System.Double"] = "double",
        ["System.Decimal"] = "decimal",
        ["System.IntPtr"] = "nint",
        ["System.UIntPtr"] = "nuint",
        ["System.Void"] = "void",
    };

    public static string WriteOblivious(TypeShape shape) => Write(shape, NullableFlags.Oblivious);

    /// <summary>Writes the type, consuming nullable flags in pre-order as the compiler stores them.</summary>
    public static string Write(TypeShape shape, NullableFlags flags)
    {
        switch (shape.Kind)
        {
            case TypeShapeKind.Modified:
            case TypeShapeKind.ByRef:
                return Write(shape.Element!, flags);
            case TypeShapeKind.Pointer:
                return Write(shape.Element!, flags) + "*";
            case TypeShapeKind.FunctionPointer:
                return shape.Name;
            case TypeShapeKind.TypeParameter:
                return shape.Name + Suffix(flags.Next());
            case TypeShapeKind.Array:
                return WriteArray(shape, flags);
            case TypeShapeKind.Generic:
                return WriteGeneric(shape, flags);
            default:
                var own = shape.IsValueType ? (byte)0 : flags.Next();
                return Keyword(shape.Name) + Suffix(own);
        }
    }

    /// <summary>
    /// Writes a full name, replacing each `N arity marker with the next N arguments in angle brackets.
    /// Markers without arguments are dropped.
    /// </summary>
    public static string WriteName(string fullName, IReadOnlyList<string> arguments)
    {
        var builder = new System.Text.StringBuilder(fullName.Length + 16);
        var next = 0;
        for (var i = 0; i < fullName.Length; i++)
        {
            if (fullName[i] != '`')
            {
                builder.Append(fullName[i]);
                continue;
            }

            var count = 0;
            while (i + 1 < fullName.Length && char.IsDigit(fullName[i + 1]))
            {
                count = (count * 10) + (fullName[++i] - '0');
            }

            if (next + count <= arguments.Count && count > 0)
            {
                builder.Append('<').Append(string.Join(", ", arguments.Skip(next).Take(count))).Append('>');
                next += count;
            }
        }

        return builder.ToString();
    }

    private static string WriteArray(TypeShape shape, NullableFlags flags)
    {
        var own = flags.Next();
        var element = Write(shape.Element!, flags);
        return element + "[" + new string(',', shape.Rank - 1) + "]" + Suffix(own);
    }

    private static string WriteGeneric(TypeShape shape, NullableFlags flags)
    {
        var definition = shape.Element!;
        if (definition.Name == "System.Nullable`1" && shape.Arguments.Count == 1)
        {
            return Write(shape.Arguments[0], flags) + "?";
        }

        var own = definition.IsValueType ? (byte)0 : flags.Next();
        var arguments = shape.Arguments.Select(argument => Write(argument, flags)).ToList();
        var isTuple = definition.IsValueType && definition.Name.StartsWith("System.ValueTuple`", StringComparison.Ordinal);
        if (isTuple && arguments.Count is > 1 and < 8)
        {
            return "(" + string.Join(", ", arguments) + ")";
        }

        return WriteName(definition.Name, arguments) + Suffix(own);
    }

    private static string Keyword(string fullName) =>
        Keywords.TryGetValue(fullName, out var keyword) ? keyword : WriteName(fullName, Array.Empty<string>());

    private static string Suffix(byte flag) => flag == Annotated ? "?" : string.Empty;
}
