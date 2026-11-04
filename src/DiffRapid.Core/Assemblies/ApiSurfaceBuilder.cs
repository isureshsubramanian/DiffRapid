using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Threading;

namespace DiffRapid.Core.Assemblies;

/// <summary>Collects the declarations of the visible types of one assembly.</summary>
internal sealed partial class ApiSurfaceBuilder
{
    private readonly MetadataReader _reader;
    private readonly TypeShapeProvider _provider = TypeShapeProvider.Instance;
    private readonly List<ApiEntry> _entries = [];
    private readonly HashSet<string> _hiddenTypes = new(StringComparer.Ordinal);

    public ApiSurfaceBuilder(MetadataReader reader)
    {
        _reader = reader;
    }

    public IReadOnlyList<ApiEntry> Build(CancellationToken cancellationToken)
    {
        var visible = new List<TypeDefinitionHandle>();
        foreach (var handle in _reader.TypeDefinitions)
        {
            if (IsVisible(_reader.GetTypeDefinition(handle)))
            {
                visible.Add(handle);
            }
            else
            {
                _hiddenTypes.Add(MetadataNames.FullName(_reader, handle));
            }
        }

        foreach (var handle in visible)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddType(new TypeScope(_reader, handle));
        }

        return _entries;
    }

    private bool IsVisible(TypeDefinition definition)
    {
        switch (definition.Attributes & TypeAttributes.VisibilityMask)
        {
            case TypeAttributes.Public:
                return true;
            case TypeAttributes.NestedPublic:
            case TypeAttributes.NestedFamily:
            case TypeAttributes.NestedFamORAssem:
                return IsVisible(_reader.GetTypeDefinition(definition.GetDeclaringType()));
            default:
                return false;
        }
    }

    private void AddType(TypeScope scope)
    {
        var definition = scope.Definition;
        var access = (definition.Attributes & TypeAttributes.VisibilityMask) is TypeAttributes.Public or TypeAttributes.NestedPublic ? "public" : "protected";
        var kind = TypeKind(scope);
        if (kind == "delegate")
        {
            AddDelegate(scope, access);
            return;
        }

        var modifiers = kind == "class" ? ClassModifiers(definition.Attributes) : string.Empty;
        var bases = BaseList(scope, kind);
        var declaration = $"{access} {modifiers}{kind} {scope.DeclaredName}{bases}{Constraints(definition.GetGenericParameters(), scope.Generics, scope.NullableContext)}";
        _entries.Add(new ApiEntry(ApiEntryKind.Type, scope.Namespace, scope.DisplayName, string.Empty, declaration));
        if (kind == "enum")
        {
            AddEnumValues(scope);
            return;
        }

        AddFields(scope);
        AddMethods(scope);
        AddProperties(scope);
        AddEvents(scope);
    }

    private string TypeKind(TypeScope scope)
    {
        var definition = scope.Definition;
        if ((definition.Attributes & TypeAttributes.Interface) != 0)
        {
            return "interface";
        }

        var baseName = definition.BaseType.IsNil ? null : MetadataNames.FullName(_reader, definition.BaseType);
        return baseName switch
        {
            "System.Enum" => "enum",
            "System.ValueType" when scope.FullName != "System.Enum" => "struct",
            "System.MulticastDelegate" => "delegate",
            _ => "class",
        };
    }

    private static string ClassModifiers(TypeAttributes attributes)
    {
        var isAbstract = (attributes & TypeAttributes.Abstract) != 0;
        var isSealed = (attributes & TypeAttributes.Sealed) != 0;
        if (isAbstract && isSealed)
        {
            return "static ";
        }

        if (isAbstract)
        {
            return "abstract ";
        }

        return isSealed ? "sealed " : string.Empty;
    }

    private string BaseList(TypeScope scope, string kind)
    {
        var definition = scope.Definition;
        var bases = new List<string>();
        if (kind == "enum")
        {
            bases.Add(EnumUnderlyingType(scope));
        }
        else if (kind == "class" && !definition.BaseType.IsNil && MetadataNames.FullName(_reader, definition.BaseType) != "System.Object")
        {
            var flags = Flags(definition.GetCustomAttributes(), scope.NullableContext);
            bases.Add(TypeShapeWriter.Write(DecodeType(definition.BaseType, scope.Generics), flags));
        }

        var interfaces = new List<string>();
        foreach (var handle in definition.GetInterfaceImplementations())
        {
            var implementation = _reader.GetInterfaceImplementation(handle);
            var shape = DecodeType(implementation.Interface, scope.Generics);
            if (!IsHidden(shape))
            {
                interfaces.Add(TypeShapeWriter.Write(shape, Flags(implementation.GetCustomAttributes(), scope.NullableContext)));
            }
        }

        interfaces.Sort(StringComparer.Ordinal);
        bases.AddRange(interfaces);
        return bases.Count == 0 ? string.Empty : " : " + string.Join(", ", bases);
    }

    private bool IsHidden(TypeShape shape)
    {
        var unmodified = shape.Unmodified();
        return unmodified.Kind is TypeShapeKind.Named or TypeShapeKind.Generic && _hiddenTypes.Contains(unmodified.Name);
    }

    private string EnumUnderlyingType(TypeScope scope)
    {
        foreach (var handle in scope.Definition.GetFields())
        {
            var field = _reader.GetFieldDefinition(handle);
            if ((field.Attributes & FieldAttributes.Static) == 0)
            {
                return TypeShapeWriter.WriteOblivious(field.DecodeSignature(_provider, scope.Generics));
            }
        }

        return "int";
    }

    private void AddDelegate(TypeScope scope, string access)
    {
        foreach (var handle in scope.Definition.GetMethods())
        {
            var method = _reader.GetMethodDefinition(handle);
            if (_reader.GetString(method.Name) != "Invoke")
            {
                continue;
            }

            var signature = method.DecodeSignature(_provider, scope.Generics);
            var context = MetadataNames.NullableContext(_reader, method.GetCustomAttributes()) ?? scope.NullableContext;
            var rows = ParameterRows(method, signature.ParameterTypes.Length);
            var returns = ReturnText(signature.ReturnType, rows[0], context);
            var parameters = ParameterList(signature.ParameterTypes, rows, context, false);
            var constraints = Constraints(scope.Definition.GetGenericParameters(), scope.Generics, scope.NullableContext);
            var declaration = $"{access} delegate {returns} {scope.DeclaredName}({parameters}){constraints}";
            _entries.Add(new ApiEntry(ApiEntryKind.Type, scope.Namespace, scope.DisplayName, string.Empty, declaration));
            return;
        }
    }

    private void AddEnumValues(TypeScope scope)
    {
        foreach (var handle in scope.Definition.GetFields())
        {
            var field = _reader.GetFieldDefinition(handle);
            if ((field.Attributes & FieldAttributes.Literal) == 0)
            {
                continue;
            }

            var name = _reader.GetString(field.Name);
            var declaration = name + " = " + MetadataNames.ConstantText(_reader, field.GetDefaultValue());
            AddMember(scope, name, declaration);
        }
    }

    private string Constraints(GenericParameterHandleCollection parameters, GenericScope generics, byte context)
    {
        var clauses = new List<string>();
        foreach (var handle in parameters)
        {
            var parameter = _reader.GetGenericParameter(handle);
            var attributes = parameter.Attributes;
            var isStruct = (attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0;
            var parts = new List<string>();
            if ((attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
            {
                var flag = Flags(parameter.GetCustomAttributes(), context).Next();
                parts.Add(flag == 2 ? "class?" : "class");
            }

            if (isStruct)
            {
                parts.Add("struct");
            }

            parts.AddRange(ConstraintTypes(parameter, generics, context, isStruct));
            if ((attributes & GenericParameterAttributes.DefaultConstructorConstraint) != 0 && !isStruct)
            {
                parts.Add("new()");
            }

            if (parts.Count > 0)
            {
                clauses.Add(" where " + _reader.GetString(parameter.Name) + " : " + string.Join(", ", parts));
            }
        }

        return string.Concat(clauses);
    }

    private IEnumerable<string> ConstraintTypes(GenericParameter parameter, GenericScope generics, byte context, bool isStruct)
    {
        foreach (var handle in parameter.GetConstraints())
        {
            var constraint = _reader.GetGenericParameterConstraint(handle);
            var shape = DecodeType(constraint.Type, generics);
            if (isStruct && shape.Name == "System.ValueType")
            {
                continue;
            }

            yield return TypeShapeWriter.Write(shape, Flags(constraint.GetCustomAttributes(), context));
        }
    }

    private TypeShape DecodeType(EntityHandle handle, GenericScope generics) => handle.Kind switch
    {
        HandleKind.TypeDefinition => _provider.GetTypeFromDefinition(_reader, (TypeDefinitionHandle)handle, 0),
        HandleKind.TypeReference => _provider.GetTypeFromReference(_reader, (TypeReferenceHandle)handle, 0),
        HandleKind.TypeSpecification => _reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(_provider, generics),
        _ => TypeShape.Named("?", false),
    };

    private NullableFlags Flags(CustomAttributeHandleCollection attributes, byte context) =>
        new(MetadataNames.NullableFlags(_reader, attributes), context);

    private void AddMember(TypeScope scope, string memberName, string declaration) =>
        _entries.Add(new ApiEntry(ApiEntryKind.Member, scope.Namespace, scope.DisplayName, memberName, declaration));

    /// <summary>Names, generic parameters and the nullable default of one visible type.</summary>
    private sealed class TypeScope
    {
        public TypeScope(MetadataReader reader, TypeDefinitionHandle handle)
        {
            Definition = reader.GetTypeDefinition(handle);
            FullName = MetadataNames.FullName(reader, handle);
            var parameters = Definition.GetGenericParameters().Select(reader.GetGenericParameter).ToList();
            var names = parameters.Select(parameter => reader.GetString(parameter.Name)).ToImmutableArray();
            Generics = new GenericScope(names, Array.Empty<string>());
            DisplayName = TypeShapeWriter.WriteName(FullName, names);
            DeclaredName = TypeShapeWriter.WriteName(FullName, parameters.Select(parameter => Variance(parameter) + reader.GetString(parameter.Name)).ToList());
            IsInterface = (Definition.Attributes & TypeAttributes.Interface) != 0;
            var name = reader.GetString(Definition.Name);
            var tick = name.IndexOf('`');
            SimpleName = tick < 0 ? name : name.Substring(0, tick);
            var outermost = Definition;
            byte? context = MetadataNames.NullableContext(reader, Definition.GetCustomAttributes());
            while (!outermost.GetDeclaringType().IsNil)
            {
                outermost = reader.GetTypeDefinition(outermost.GetDeclaringType());
                context ??= MetadataNames.NullableContext(reader, outermost.GetCustomAttributes());
            }

            Namespace = reader.GetString(outermost.Namespace);
            NullableContext = context ?? 0;
        }

        public TypeDefinition Definition { get; }

        /// <summary>The metadata full name with `N arity markers.</summary>
        public string FullName { get; }

        public string Namespace { get; }

        /// <summary>The full name with type parameters, e.g. System.Collections.Generic.List&lt;T&gt;.</summary>
        public string DisplayName { get; }

        /// <summary>The display name with variance, e.g. System.IComparer&lt;in T&gt;.</summary>
        public string DeclaredName { get; }

        /// <summary>The type name without namespace, outer types or arity, used for constructors.</summary>
        public string SimpleName { get; }

        public GenericScope Generics { get; }

        public byte NullableContext { get; }

        public bool IsInterface { get; }

        private static string Variance(GenericParameter parameter) =>
            (parameter.Attributes & GenericParameterAttributes.VarianceMask) switch
            {
                GenericParameterAttributes.Covariant => "out ",
                GenericParameterAttributes.Contravariant => "in ",
                _ => string.Empty,
            };
    }
}
