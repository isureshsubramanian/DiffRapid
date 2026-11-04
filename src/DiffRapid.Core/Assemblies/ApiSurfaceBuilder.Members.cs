using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;

namespace DiffRapid.Core.Assemblies;

internal sealed partial class ApiSurfaceBuilder
{
    private const int PublicRank = 2;
    private const int ProtectedRank = 1;

    private void AddFields(TypeScope scope)
    {
        foreach (var handle in scope.Definition.GetFields())
        {
            var field = _reader.GetFieldDefinition(handle);
            var attributes = field.Attributes;
            var rank = AccessRank((int)(attributes & FieldAttributes.FieldAccessMask));
            if (rank == 0 || (attributes & FieldAttributes.SpecialName) != 0)
            {
                continue;
            }

            var type = TypeShapeWriter.Write(field.DecodeSignature(_provider, scope.Generics), Flags(field.GetCustomAttributes(), scope.NullableContext));
            var name = _reader.GetString(field.Name);
            var modifiers = FieldModifiers(attributes);
            var value = (attributes & FieldAttributes.Literal) != 0 ? " = " + MetadataNames.ConstantText(_reader, field.GetDefaultValue()) : string.Empty;
            AddMember(scope, name, $"{AccessText(rank)} {modifiers}{type} {name}{value}");
        }
    }

    private static string FieldModifiers(FieldAttributes attributes)
    {
        if ((attributes & FieldAttributes.Literal) != 0)
        {
            return "const ";
        }

        var isStatic = (attributes & FieldAttributes.Static) != 0;
        var isReadOnly = (attributes & FieldAttributes.InitOnly) != 0;
        return (isStatic ? "static " : string.Empty) + (isReadOnly ? "readonly " : string.Empty);
    }

    private void AddMethods(TypeScope scope)
    {
        foreach (var handle in scope.Definition.GetMethods())
        {
            var method = _reader.GetMethodDefinition(handle);
            var rank = AccessRank((int)(method.Attributes & MethodAttributes.MemberAccessMask));
            var name = _reader.GetString(method.Name);
            var isAccessor = (method.Attributes & MethodAttributes.SpecialName) != 0 && name != ".ctor" && !name.StartsWith("op_", System.StringComparison.Ordinal);
            if (rank == 0 || isAccessor || name == ".cctor")
            {
                continue;
            }

            AddMethod(scope, method, rank, name);
        }
    }

    private void AddMethod(TypeScope scope, MethodDefinition method, int rank, string name)
    {
        var genericNames = method.GetGenericParameters().Select(handle => _reader.GetString(_reader.GetGenericParameter(handle).Name)).ToImmutableArray();
        var generics = scope.Generics.WithMethodParameters(genericNames);
        var signature = method.DecodeSignature(_provider, generics);
        var context = MetadataNames.NullableContext(_reader, method.GetCustomAttributes()) ?? scope.NullableContext;
        var rows = ParameterRows(method, signature.ParameterTypes.Length);
        var isConstructor = name == ".ctor";
        var isExtension = MetadataNames.HasAttribute(_reader, method.GetCustomAttributes(), MetadataNames.ExtensionAttribute);
        var returns = isConstructor ? string.Empty : ReturnText(signature.ReturnType, rows[0], context) + " ";
        var title = isConstructor ? scope.SimpleName : name + (genericNames.Length == 0 ? string.Empty : "<" + string.Join(", ", genericNames) + ">");
        var parameters = ParameterList(signature.ParameterTypes, rows, context, isExtension);
        var constraints = Constraints(method.GetGenericParameters(), generics, context);
        var modifiers = MethodModifiers(method.Attributes, scope.IsInterface);
        AddMember(scope, name, $"{AccessText(rank)} {modifiers}{returns}{title}({parameters}){constraints}");
    }

    private void AddProperties(TypeScope scope)
    {
        foreach (var handle in scope.Definition.GetProperties())
        {
            var property = _reader.GetPropertyDefinition(handle);
            var accessors = property.GetAccessors();
            var getter = accessors.Getter.IsNil ? (MethodDefinition?)null : _reader.GetMethodDefinition(accessors.Getter);
            var setter = accessors.Setter.IsNil ? (MethodDefinition?)null : _reader.GetMethodDefinition(accessors.Setter);
            var getRank = getter is { } get ? AccessRank((int)(get.Attributes & MethodAttributes.MemberAccessMask)) : 0;
            var setRank = setter is { } set ? AccessRank((int)(set.Attributes & MethodAttributes.MemberAccessMask)) : 0;
            if (getRank == 0 && setRank == 0)
            {
                continue;
            }

            AddProperty(scope, property, getRank > 0 ? getter!.Value : setter!.Value, getRank, setRank, setter);
        }
    }

    private void AddProperty(TypeScope scope, PropertyDefinition property, MethodDefinition primary, int getRank, int setRank, MethodDefinition? setter)
    {
        var context = MetadataNames.NullableContext(_reader, primary.GetCustomAttributes()) ?? scope.NullableContext;
        var signature = property.DecodeSignature(_provider, scope.Generics);
        var type = (signature.ReturnType.Unmodified().Kind == TypeShapeKind.ByRef ? "ref " : string.Empty)
            + TypeShapeWriter.Write(signature.ReturnType, Flags(property.GetCustomAttributes(), context));
        var name = _reader.GetString(property.Name);
        var isIndexer = signature.ParameterTypes.Length > 0;
        var title = name;
        if (isIndexer)
        {
            var accessorSignature = primary.DecodeSignature(_provider, scope.Generics);
            var count = signature.ParameterTypes.Length;
            var rows = ParameterRows(primary, accessorSignature.ParameterTypes.Length);
            title = "this[" + ParameterList(accessorSignature.ParameterTypes.Take(count).ToImmutableArray(), rows, context, false) + "]";
        }

        var rank = System.Math.Max(getRank, setRank);
        var parts = new List<string>();
        if (getRank > 0)
        {
            parts.Add((getRank < rank ? "protected " : string.Empty) + "get;");
        }

        if (setRank > 0)
        {
            var isInit = setter!.Value.DecodeSignature(_provider, scope.Generics).ReturnType.HasModifier(MetadataNames.IsExternalInit);
            parts.Add((setRank < rank ? "protected " : string.Empty) + (isInit ? "init;" : "set;"));
        }

        var modifiers = MethodModifiers(primary.Attributes, scope.IsInterface);
        AddMember(scope, isIndexer ? "this[]" : name, $"{AccessText(rank)} {modifiers}{type} {title} {{ {string.Join(" ", parts)} }}");
    }

    private void AddEvents(TypeScope scope)
    {
        foreach (var handle in scope.Definition.GetEvents())
        {
            var definition = _reader.GetEventDefinition(handle);
            var adderHandle = definition.GetAccessors().Adder;
            if (adderHandle.IsNil)
            {
                continue;
            }

            var adder = _reader.GetMethodDefinition(adderHandle);
            var rank = AccessRank((int)(adder.Attributes & MethodAttributes.MemberAccessMask));
            if (rank == 0)
            {
                continue;
            }

            var context = MetadataNames.NullableContext(_reader, adder.GetCustomAttributes()) ?? scope.NullableContext;
            var type = TypeShapeWriter.Write(DecodeType(definition.Type, scope.Generics), Flags(definition.GetCustomAttributes(), context));
            var name = _reader.GetString(definition.Name);
            AddMember(scope, name, $"{AccessText(rank)} {MethodModifiers(adder.Attributes, scope.IsInterface)}event {type} {name}");
        }
    }

    private static string MethodModifiers(MethodAttributes attributes, bool inInterface)
    {
        var isStatic = (attributes & MethodAttributes.Static) != 0;
        var isAbstract = (attributes & MethodAttributes.Abstract) != 0;
        if (inInterface)
        {
            if (!isStatic)
            {
                return string.Empty;
            }

            return isAbstract ? "static abstract " : "static ";
        }

        if (isStatic)
        {
            return "static ";
        }

        var isNewSlot = (attributes & MethodAttributes.NewSlot) != 0;
        if (isAbstract)
        {
            return isNewSlot ? "abstract " : "abstract override ";
        }

        if ((attributes & MethodAttributes.Virtual) == 0)
        {
            return string.Empty;
        }

        var isFinal = (attributes & MethodAttributes.Final) != 0;
        if (isNewSlot)
        {
            // A sealed new slot is an implicit interface implementation, which is not virtual in C#.
            return isFinal ? string.Empty : "virtual ";
        }

        return isFinal ? "sealed override " : "override ";
    }

    /// <summary>The parameter rows of a method by sequence number; index 0 holds the return value.</summary>
    private Parameter?[] ParameterRows(MethodDefinition method, int count)
    {
        var rows = new Parameter?[count + 1];
        foreach (var handle in method.GetParameters())
        {
            var parameter = _reader.GetParameter(handle);
            if (parameter.SequenceNumber <= count)
            {
                rows[parameter.SequenceNumber] = parameter;
            }
        }

        return rows;
    }

    private string ReturnText(TypeShape shape, Parameter? row, byte context)
    {
        var attributes = row?.GetCustomAttributes() ?? default;
        var flags = row is null ? new NullableFlags(null, context) : Flags(attributes, context);
        if (shape.Unmodified().Kind != TypeShapeKind.ByRef)
        {
            return TypeShapeWriter.Write(shape, flags);
        }

        var isReadOnly = row is not null && MetadataNames.HasAttribute(_reader, attributes, MetadataNames.IsReadOnlyAttribute);
        return (isReadOnly ? "ref readonly " : "ref ") + TypeShapeWriter.Write(shape, flags);
    }

    private string ParameterList(ImmutableArray<TypeShape> types, Parameter?[] rows, byte context, bool isExtension)
    {
        var parameters = new List<string>(types.Length);
        for (var index = 0; index < types.Length; index++)
        {
            var prefix = isExtension && index == 0 ? "this " : string.Empty;
            parameters.Add(prefix + ParameterText(types[index], rows[index + 1], context, index));
        }

        return string.Join(", ", parameters);
    }

    private string ParameterText(TypeShape shape, Parameter? row, byte context, int index)
    {
        if (row is not { } parameter)
        {
            return TypeShapeWriter.Write(shape, new NullableFlags(null, context)) + " arg" + index;
        }

        var attributes = parameter.GetCustomAttributes();
        var prefix = string.Empty;
        if (shape.Unmodified().Kind == TypeShapeKind.ByRef)
        {
            prefix = RefKind(parameter, attributes);
        }

        if (MetadataNames.HasAttribute(_reader, attributes, MetadataNames.ParamArrayAttribute))
        {
            prefix += "params ";
        }

        var text = prefix + TypeShapeWriter.Write(shape, Flags(attributes, context)) + " " + _reader.GetString(parameter.Name);
        var defaultValue = parameter.GetDefaultValue();
        if ((parameter.Attributes & ParameterAttributes.HasDefault) != 0 && !defaultValue.IsNil)
        {
            text += " = " + MetadataNames.ConstantText(_reader, defaultValue);
        }

        return text;
    }

    private string RefKind(Parameter parameter, CustomAttributeHandleCollection attributes)
    {
        var isOut = (parameter.Attributes & ParameterAttributes.Out) != 0;
        var isIn = (parameter.Attributes & ParameterAttributes.In) != 0;
        if (isOut && !isIn)
        {
            return "out ";
        }

        return isIn || MetadataNames.HasAttribute(_reader, attributes, MetadataNames.IsReadOnlyAttribute) ? "in " : "ref ";
    }

    /// <summary>2 for public, 1 for protected (including protected internal), 0 when not visible outside the assembly.</summary>
    private static int AccessRank(int access) => access switch
    {
        6 => PublicRank,
        4 or 5 => ProtectedRank,
        _ => 0,
    };

    private static string AccessText(int rank) => rank == PublicRank ? "public" : "protected";
}
