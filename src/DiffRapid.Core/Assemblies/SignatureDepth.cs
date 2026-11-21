using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;

namespace DiffRapid.Core.Assemblies;

/// <summary>
/// Checks how deeply a signature blob nests its types before System.Reflection.Metadata's SignatureDecoder,
/// which recurses once per level, reads it. A crafted blob nested thousands of levels deep would otherwise
/// overflow the stack, which no exception handler can catch. The scan follows ECMA-335 II.23.2 with an
/// explicit stack, so it never recurses itself; its time and memory are linear in the blob's length.
/// </summary>
internal static class SignatureDepth
{
    private enum Then : byte
    {
        Pop,
        GenericArguments,
        ArrayShape,
    }

    /// <summary>Checks a field, method, property, local variables or method specification signature.</summary>
    /// <exception cref="InvalidDataException">The signature is malformed or nests types deeper than <see cref="MetadataNames.MaxNestingDepth"/>.</exception>
    public static void CheckSignature(MetadataReader reader, BlobHandle handle) => Scan(reader.GetBlobReader(handle), false);

    /// <summary>Checks the type held by a type specification.</summary>
    /// <exception cref="InvalidDataException">The type is malformed or nests deeper than <see cref="MetadataNames.MaxNestingDepth"/>.</exception>
    public static void CheckTypeSpecification(MetadataReader reader, TypeSpecificationHandle handle) =>
        Scan(reader.GetBlobReader(reader.GetTypeSpecification(handle).Signature), true);

    private static void Scan(BlobReader blob, bool isTypeSpecification)
    {
        try
        {
            // Each frame is one level of the decoder's recursion: the types it still has to read there,
            // and what follows them (a generic argument list or an array shape).
            var frames = new List<(int Remaining, Then Then)> { (isTypeSpecification ? 1 : HeaderTypeCount(ref blob), Then.Pop) };
            while (frames.Count > 0)
            {
                var top = frames.Count - 1;
                var (remaining, then) = frames[top];
                if (remaining == 0)
                {
                    if (then == Then.GenericArguments)
                    {
                        frames[top] = (blob.ReadCompressedInteger(), Then.Pop);
                        continue;
                    }

                    if (then == Then.ArrayShape)
                    {
                        SkipArrayShape(ref blob);
                    }

                    frames.RemoveAt(top);
                    continue;
                }

                frames[top] = (remaining - 1, then);
                ScanType(ref blob, frames, top, remaining, then);
            }
        }
        catch (BadImageFormatException exception)
        {
            throw new InvalidDataException("The assembly has a malformed signature: " + exception.Message, exception);
        }
    }

    private static void ScanType(ref BlobReader blob, List<(int Remaining, Then Then)> frames, int top, int remaining, Then then)
    {
        var code = (SignatureTypeCode)blob.ReadByte();
        switch (code)
        {
            case >= SignatureTypeCode.Void and <= SignatureTypeCode.String:
            case SignatureTypeCode.TypedReference:
            case SignatureTypeCode.IntPtr:
            case SignatureTypeCode.UIntPtr:
            case SignatureTypeCode.Object:
                break;
            case (SignatureTypeCode)SignatureTypeKind.ValueType:
            case (SignatureTypeCode)SignatureTypeKind.Class:
                blob.ReadTypeHandle();
                break;
            case SignatureTypeCode.GenericTypeParameter:
            case SignatureTypeCode.GenericMethodParameter:
                blob.ReadCompressedInteger();
                break;
            case SignatureTypeCode.Pointer:
            case SignatureTypeCode.ByReference:
            case SignatureTypeCode.SZArray:
            case SignatureTypeCode.Pinned:
                Push(frames, 1, Then.Pop);
                break;
            case SignatureTypeCode.RequiredModifier:
            case SignatureTypeCode.OptionalModifier:
                blob.ReadTypeHandle();
                Push(frames, 1, Then.Pop);
                break;
            case SignatureTypeCode.Sentinel:
                // Marks where a vararg call's optional parameters start; it is not a type itself.
                frames[top] = (remaining, then);
                break;
            case SignatureTypeCode.GenericTypeInstance:
                Push(frames, 1, Then.GenericArguments);
                break;
            case SignatureTypeCode.Array:
                Push(frames, 1, Then.ArrayShape);
                break;
            case SignatureTypeCode.FunctionPointer:
                var header = blob.ReadSignatureHeader();
                if (header.Kind != SignatureKind.Method)
                {
                    throw new BadImageFormatException("A function pointer does not hold a method signature.");
                }

                Push(frames, MethodTypeCount(ref blob, header), Then.Pop);
                break;
            default:
                throw new BadImageFormatException($"Unexpected type code 0x{(byte)code:x2}.");
        }
    }

    private static int HeaderTypeCount(ref BlobReader blob)
    {
        var header = blob.ReadSignatureHeader();
        return header.Kind switch
        {
            SignatureKind.Field => 1,
            SignatureKind.Method or SignatureKind.Property => MethodTypeCount(ref blob, header),
            SignatureKind.LocalVariables or SignatureKind.MethodSpecification => blob.ReadCompressedInteger(),
            _ => throw new BadImageFormatException($"Unexpected signature kind {header.Kind}."),
        };
    }

    // The return type and then the parameters; a compressed count is at most 0x1FFFFFFF, so adding one cannot overflow.
    private static int MethodTypeCount(ref BlobReader blob, SignatureHeader header)
    {
        if (header.IsGeneric)
        {
            blob.ReadCompressedInteger();
        }

        return blob.ReadCompressedInteger() + 1;
    }

    // Each read consumes at least one byte or throws at the blob's end, so these loops end with the blob.
    private static void SkipArrayShape(ref BlobReader blob)
    {
        blob.ReadCompressedInteger();
        for (var sizes = blob.ReadCompressedInteger(); sizes > 0; sizes--)
        {
            blob.ReadCompressedInteger();
        }

        for (var bounds = blob.ReadCompressedInteger(); bounds > 0; bounds--)
        {
            blob.ReadCompressedSignedInteger();
        }
    }

    private static void Push(List<(int Remaining, Then Then)> frames, int count, Then then)
    {
        if (frames.Count >= MetadataNames.MaxNestingDepth)
        {
            throw new InvalidDataException($"The assembly has a signature whose types nest more than {MetadataNames.MaxNestingDepth} levels deep.");
        }

        frames.Add((count, then));
    }
}
