using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;

namespace DiffRapid.Core.Assemblies;

/// <summary>Name, attribute and constant lookups over a metadata reader.</summary>
internal static class MetadataNames
{
    private const string CompilerServices = "System.Runtime.CompilerServices.";

    public const string NullableAttribute = CompilerServices + "NullableAttribute";
    public const string NullableContextAttribute = CompilerServices + "NullableContextAttribute";
    public const string ExtensionAttribute = CompilerServices + "ExtensionAttribute";
    public const string IsReadOnlyAttribute = CompilerServices + "IsReadOnlyAttribute";
    public const string IsExternalInit = CompilerServices + "IsExternalInit";
    public const string ParamArrayAttribute = "System.ParamArrayAttribute";

    /// <summary>The namespace-qualified name of a type definition; nested types are joined with '.'.</summary>
    public static string FullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        var name = reader.GetString(definition.Name);
        var declaring = definition.GetDeclaringType();
        if (!declaring.IsNil)
        {
            return FullName(reader, declaring) + "." + name;
        }

        return Qualify(reader.GetString(definition.Namespace), name);
    }

    /// <summary>The namespace-qualified name of a type reference; nested types are joined with '.'.</summary>
    public static string FullName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var reference = reader.GetTypeReference(handle);
        var name = reader.GetString(reference.Name);
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return FullName(reader, (TypeReferenceHandle)reference.ResolutionScope) + "." + name;
        }

        return Qualify(reader.GetString(reference.Namespace), name);
    }

    /// <summary>The full name of a type definition or reference, or null for a type specification.</summary>
    public static string? FullName(MetadataReader reader, EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeDefinition => FullName(reader, (TypeDefinitionHandle)handle),
        HandleKind.TypeReference => FullName(reader, (TypeReferenceHandle)handle),
        _ => null,
    };

    /// <summary>Whether a type definition derives from System.ValueType or System.Enum.</summary>
    public static bool IsValueType(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        var baseName = definition.BaseType.IsNil ? null : FullName(reader, definition.BaseType);
        var ownName = FullName(reader, handle);
        return baseName == "System.Enum" || (baseName == "System.ValueType" && ownName != "System.Enum");
    }

    /// <summary>Finds the first custom attribute of the given type.</summary>
    public static CustomAttribute? FindAttribute(MetadataReader reader, CustomAttributeHandleCollection attributes, string fullName)
    {
        foreach (var handle in attributes)
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (AttributeTypeName(reader, attribute) == fullName)
            {
                return attribute;
            }
        }

        return null;
    }

    /// <summary>Whether an attribute of the given type is present.</summary>
    public static bool HasAttribute(MetadataReader reader, CustomAttributeHandleCollection attributes, string fullName) =>
        FindAttribute(reader, attributes, fullName) is not null;

    /// <summary>Reads the first constructor argument of an attribute as a string.</summary>
    public static string? AttributeString(MetadataReader reader, CustomAttributeHandleCollection attributes, string fullName)
    {
        var attribute = FindAttribute(reader, attributes, fullName);
        if (attribute is null)
        {
            return null;
        }

        var value = attribute.Value.DecodeValue(TypeShapeProvider.Instance);
        return value.FixedArguments.Length > 0 ? value.FixedArguments[0].Value as string : null;
    }

    /// <summary>Reads the flags of a NullableAttribute (one byte or a byte array), or null when absent.</summary>
    public static IReadOnlyList<byte>? NullableFlags(MetadataReader reader, CustomAttributeHandleCollection attributes)
    {
        var attribute = FindAttribute(reader, attributes, NullableAttribute);
        if (attribute is null)
        {
            return null;
        }

        var value = attribute.Value.DecodeValue(TypeShapeProvider.Instance);
        if (value.FixedArguments.Length != 1)
        {
            return null;
        }

        return value.FixedArguments[0].Value switch
        {
            byte single => new[] { single },
            ImmutableArray<CustomAttributeTypedArgument<TypeShape>> many => many.Select(item => item.Value is byte flag ? flag : (byte)0).ToArray(),
            _ => null,
        };
    }

    /// <summary>Reads the default of a NullableContextAttribute, or null when absent.</summary>
    public static byte? NullableContext(MetadataReader reader, CustomAttributeHandleCollection attributes)
    {
        var attribute = FindAttribute(reader, attributes, NullableContextAttribute);
        if (attribute is null)
        {
            return null;
        }

        var value = attribute.Value.DecodeValue(TypeShapeProvider.Instance);
        return value.FixedArguments.Length == 1 && value.FixedArguments[0].Value is byte flag ? flag : null;
    }

    /// <summary>Writes a constant (a field value or parameter default) as a C# literal.</summary>
    public static string ConstantText(MetadataReader reader, ConstantHandle handle)
    {
        var constant = reader.GetConstant(handle);
        var blob = reader.GetBlobReader(constant.Value);
        var value = constant.TypeCode == ConstantTypeCode.NullReference ? null : blob.ReadConstant(constant.TypeCode);
        return Literal(value);
    }

    /// <summary>Writes a value as a C# literal.</summary>
    public static string Literal(object? value) => value switch
    {
        null => "null",
        string text => Quote(text, '"'),
        char character => Quote(character.ToString(), '\''),
        bool flag => flag ? "true" : "false",
        float single => single.ToString("R", CultureInfo.InvariantCulture) + "F",
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Qualify(string space, string name) => space.Length == 0 ? name : space + "." + name;

    private static string AttributeTypeName(MetadataReader reader, CustomAttribute attribute)
    {
        EntityHandle parent;
        if (attribute.Constructor.Kind == HandleKind.MemberReference)
        {
            parent = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
        }
        else if (attribute.Constructor.Kind == HandleKind.MethodDefinition)
        {
            parent = reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType();
        }
        else
        {
            return string.Empty;
        }

        return FullName(reader, parent) ?? string.Empty;
    }

    private static string Quote(string text, char quote)
    {
        var builder = new StringBuilder(text.Length + 2).Append(quote);
        foreach (var character in text)
        {
            builder.Append(character switch
            {
                '\\' => "\\\\",
                '\0' => "\\0",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when character == quote => "\\" + quote,
                _ when char.IsControl(character) => "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture),
                _ => character.ToString(),
            });
        }

        return builder.Append(quote).ToString();
    }
}
