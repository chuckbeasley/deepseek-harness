using System;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Dsh.Rpc.Generator;

/// <summary>
/// The Roslyn source generator for typed RPC codecs (the port's typert codec half): a record
/// marked <c>[RpcCodec]</c> gains a generated static <c>&lt;Name&gt;Codec</c> class carrying
/// property-by-property <c>Encode</c> and <c>TryDecode</c>, replacing hand-written JsonElement
/// plumbing with compile-time-checked wire code. Supported member types: string, int, long,
/// double, bool, <c>System.Text.Json.JsonElement</c>, nullable forms of those, and nested
/// <c>[RpcCodec]</c> records (their generated codecs compose). A nullable JsonElement member
/// encodes as an empty object when absent — the RPC error vocabulary always carries details. An
/// unsupported member type emits a <c>#error</c> so the build fails loud instead of shipping a
/// half codec.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class RpcCodecGenerator : IIncrementalGenerator
{
    private const string CodecAttribute = "Dsh.Rpc.Generator.RpcCodecAttribute";

    private const string AttributeSource = """
        namespace Dsh.Rpc.Generator
        {
            /// <summary>Mark a record whose typed codec the RPC gateway uses.</summary>
            [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct)]
            internal sealed class RpcCodecAttribute : System.Attribute
            {
            }
        }
        """;

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(static ctx =>
            ctx.AddSource("RpcCodecAttribute.g.cs", SourceText.From(AttributeSource, Encoding.UTF8)));

        var candidates = context.SyntaxProvider.ForAttributeWithMetadataName(
            CodecAttribute,
            static (node, _) => node is TypeDeclarationSyntax,
            static (ctx, _) => ctx.TargetSymbol as INamedTypeSymbol);

        context.RegisterSourceOutput(candidates, static (spc, type) =>
        {
            if (type is null) return;
            var (hint, source) = Emit(type);
            spc.AddSource(hint, SourceText.From(source, Encoding.UTF8));
        });
    }

    private enum Kind
    {
        String,
        Int32,
        Int64,
        Double,
        Boolean,
        JsonElement,
        Nested,
        Unsupported,
    }

    private sealed record Member(IPropertySymbol Symbol, string Wire, Kind Kind, INamedTypeSymbol? Nested, bool Nullable);

    private static (string Hint, string Source) Emit(INamedTypeSymbol type)
    {
        var members = type.GetMembers().OfType<IPropertySymbol>()
            .Where(property => !property.IsStatic && property.SetMethod is not null)
            .Select(Classify)
            .ToArray();
        var builder = new StringBuilder();
        var unsupported = members.Where(member => member.Kind == Kind.Unsupported).ToArray();
        foreach (var member in unsupported)
        {
            builder.Append("#error RpcCodec: unsupported member type '")
                .Append(member.Symbol.Type.ToDisplayString()).Append("' on ").Append(type.ToDisplayString())
                .Append('.').Append(member.Symbol.Name)
                .AppendLine(" (supported: string, int, long, double, bool, JsonElement, nullable forms, and [RpcCodec] records)");
        }
        var namespaceName = type.ContainingNamespace.IsGlobalNamespace
            ? null
            : type.ContainingNamespace.ToDisplayString();
        if (namespaceName is not null)
        {
            builder.Append("namespace ").Append(namespaceName).AppendLine();
            builder.AppendLine("{");
        }
        builder.AppendLine("#nullable enable");
        builder.AppendLine("using System.IO;");
        builder.AppendLine("using System.Text.Json;");
        builder.AppendLine();
        builder.Append("/// <summary>Generated typed codec for ").Append(type.ToDisplayString()).AppendLine(" (Dsh.Rpc.Generator).</summary>");
        builder.Append("public static class ").Append(type.Name).AppendLine("Codec");
        builder.AppendLine("{");
        EmitEncode(builder, type, members);
        builder.AppendLine();
        EmitTryDecode(builder, type, members);
        builder.AppendLine("}");
        if (namespaceName is not null)
        {
            builder.AppendLine("}");
        }
        return (type.Name + "Codec.g.cs", builder.ToString());
    }

    private static Member Classify(IPropertySymbol property)
    {
        var wire = WireName(property.Name);
        var type = property.Type;
        var nullable = false;
        if (type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            && type is INamedTypeSymbol nullableType)
        {
            nullable = true;
            type = nullableType.TypeArguments[0];
        }
        if (IsJsonElement(type)) return new Member(property, wire, Kind.JsonElement, null, nullable);
        switch (type.SpecialType)
        {
            case SpecialType.System_String: return new Member(property, wire, Kind.String, null, nullable);
            case SpecialType.System_Int32: return new Member(property, wire, Kind.Int32, null, nullable);
            case SpecialType.System_Int64: return new Member(property, wire, Kind.Int64, null, nullable);
            case SpecialType.System_Double: return new Member(property, wire, Kind.Double, null, nullable);
            case SpecialType.System_Boolean: return new Member(property, wire, Kind.Boolean, null, nullable);
        }
        if (type is INamedTypeSymbol named && named.GetAttributes().Any(attribute =>
                attribute.AttributeClass?.ToDisplayString() == CodecAttribute))
        {
            return new Member(property, wire, Kind.Nested, named, nullable);
        }
        return new Member(property, wire, Kind.Unsupported, null, false);
    }

    private static bool IsJsonElement(ITypeSymbol type)
        => type is INamedTypeSymbol named
            && named.Name == "JsonElement"
            && named.ContainingNamespace.ToDisplayString() == "System.Text.Json";

    private static string WireName(string propertyName)
        => char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1);

    private static void EmitEncode(StringBuilder builder, INamedTypeSymbol type, Member[] members)
    {
        builder.Append("    /// <summary>Encode one value as a JSON object (property order is declaration order).</summary>");
        builder.AppendLine();
        builder.Append("    public static JsonElement Encode(").Append(type.ToDisplayString()).AppendLine(" value)");
        builder.AppendLine("    {");
        builder.AppendLine("        using var stream = new MemoryStream();");
        builder.AppendLine("        using (var writer = new Utf8JsonWriter(stream))");
        builder.AppendLine("        {");
        builder.AppendLine("            writer.WriteStartObject();");
        foreach (var member in members)
        {
            if (member.Kind == Kind.Unsupported) continue;
            EmitMemberEncode(builder, member);
        }
        builder.AppendLine("            writer.WriteEndObject();");
        builder.AppendLine("        }");
        builder.AppendLine("        return JsonSerializer.Deserialize<JsonElement>(stream.ToArray());");
        builder.AppendLine("    }");
    }

    private static void EmitMemberEncode(StringBuilder builder, Member member)
    {
        var name = member.Symbol.Name;
        switch (member.Kind)
        {
            case Kind.String when !member.Nullable:
                builder.Append("            writer.WriteString(\"").Append(member.Wire).Append("\", value.").Append(name).AppendLine(");");
                break;
            case Kind.Int32 when !member.Nullable:
            case Kind.Int64 when !member.Nullable:
            case Kind.Double when !member.Nullable:
                builder.Append("            writer.WriteNumber(\"").Append(member.Wire).Append("\", value.").Append(name).AppendLine(");");
                break;
            case Kind.Boolean when !member.Nullable:
                builder.Append("            writer.WriteBoolean(\"").Append(member.Wire).Append("\", value.").Append(name).AppendLine(");");
                break;
            case Kind.JsonElement when !member.Nullable:
                builder.Append("            writer.WritePropertyName(\"").Append(member.Wire).AppendLine("\");");
                builder.Append("            value.").Append(name).AppendLine(".WriteTo(writer);");
                break;
            case Kind.JsonElement:
                builder.Append("            writer.WritePropertyName(\"").Append(member.Wire).AppendLine("\");");
                builder.Append("            if (value.").Append(name).AppendLine(" is JsonElement element)");
                builder.AppendLine("            {");
                builder.AppendLine("                element.WriteTo(writer);");
                builder.AppendLine("            }");
                builder.AppendLine("            else");
                builder.AppendLine("            {");
                builder.AppendLine("                writer.WriteStartObject();");
                builder.AppendLine("                writer.WriteEndObject();");
                builder.AppendLine("            }");
                break;
            case Kind.Nested when !member.Nullable:
                builder.Append("            writer.WritePropertyName(\"").Append(member.Wire).AppendLine("\");");
                builder.Append("            ").Append(member.Nested!.Name).Append("Codec.Encode(value.").Append(name).AppendLine(").WriteTo(writer);");
                break;
            case Kind.Nested:
                builder.Append("            if (value.").Append(name).AppendLine(" is { } nestedValue)");
                builder.AppendLine("            {");
                builder.Append("                writer.WritePropertyName(\"").Append(member.Wire).AppendLine("\");");
                builder.Append("                ").Append(member.Nested!.Name).AppendLine("Codec.Encode(nestedValue).WriteTo(writer);");
                builder.AppendLine("            }");
                break;
            case Kind.String:
            case Kind.Int32:
            case Kind.Int64:
            case Kind.Double:
            case Kind.Boolean:
                builder.Append("            if (value.").Append(name).AppendLine(" is { } memberValue)");
                builder.AppendLine("            {");
                builder.Append("                writer.Write").Append(member.Kind == Kind.String ? "String" : member.Kind == Kind.Boolean ? "Boolean" : "Number")
                    .Append("(\"").Append(member.Wire).Append("\", memberValue);");
                builder.AppendLine();
                builder.AppendLine("            }");
                break;
        }
    }

    private static void EmitTryDecode(StringBuilder builder, INamedTypeSymbol type, Member[] members)
    {
        builder.Append("    /// <summary>Decode one JSON object into a value, or return the first property failure.</summary>");
        builder.AppendLine();
        builder.Append("    public static (").Append(type.ToDisplayString()).AppendLine("? Value, string? Error) TryDecode(JsonElement element)");
        builder.AppendLine("    {");
        builder.AppendLine("        if (element.ValueKind != JsonValueKind.Object)");
        builder.AppendLine("        {");
        builder.AppendLine("            return (null, \"expected a JSON object\");");
        builder.AppendLine("        }");
        foreach (var member in members)
        {
            if (member.Kind == Kind.Unsupported) continue;
            EmitMemberDecode(builder, member);
        }
        // A positional record has no parameterless constructor, so the value is built through
        // its primary constructor when one exists whose parameters all name decoded members;
        // otherwise the object initializer applies (plain classes with default constructors).
        var constructor = type.InstanceConstructors.FirstOrDefault(ctor => ctor.Parameters.Length > 0);
        if (constructor is not null && constructor.Parameters.All(parameter =>
                members.Any(member => member.Symbol.Name == parameter.Name)))
        {
            builder.Append("        return (new ").Append(type.ToDisplayString()).Append('(');
            builder.Append(string.Join(", ", constructor.Parameters.Select(parameter =>
                parameter.Name + "Value")));
            builder.AppendLine("), null);");
        }
        else
        {
            builder.Append("        return (new ").Append(type.ToDisplayString()).AppendLine(" {");
            var assignments = members
                .Where(member => member.Kind != Kind.Unsupported)
                .Select(member => $"            {member.Symbol.Name} = {member.Symbol.Name}Value");
            builder.Append(string.Join("," + "\n", assignments));
            builder.AppendLine();
            builder.AppendLine("        }, null);");
        }
        builder.AppendLine("    }");
    }

    private static void EmitMemberDecode(StringBuilder builder, Member member)
    {
        var name = member.Symbol.Name;
        var value = name + "Value";
        switch (member.Kind)
        {
            case Kind.String when !member.Nullable:
                builder.Append("        if (!element.TryGetProperty(\"").Append(member.Wire).Append("\", out var ").Append(value).AppendLine("Property)");
                builder.Append("            || ").Append(value).AppendLine("Property.ValueKind != JsonValueKind.String)");
                builder.AppendLine("        {");
                builder.Append("            return (null, \"property \\\"").Append(member.Wire).AppendLine("\\\" must be a string\");");
                builder.AppendLine("        }");
                builder.Append("        var ").Append(value).Append(" = ").Append(value).AppendLine("Property.GetString()!;");
                break;
            case Kind.Int32 when !member.Nullable:
            case Kind.Int64 when !member.Nullable:
            case Kind.Double when !member.Nullable:
                builder.Append("        if (!element.TryGetProperty(\"").Append(member.Wire).Append("\", out var ").Append(value).AppendLine("Property)");
                builder.Append("            || !").Append(value).AppendLine("Property.TryGetInt32(out var " + value + "))");
                builder.AppendLine("        {");
                builder.Append("            return (null, \"property \\\"").Append(member.Wire).AppendLine("\\\" must be a number\");");
                builder.AppendLine("        }");
                break;
            case Kind.Boolean when !member.Nullable:
                builder.Append("        if (!element.TryGetProperty(\"").Append(member.Wire).Append("\", out var ").Append(value).AppendLine("Property)");
                builder.Append("            || (").Append(value).AppendLine("Property.ValueKind != JsonValueKind.True && " + value + "Property.ValueKind != JsonValueKind.False)");
                builder.AppendLine("        {");
                builder.Append("            return (null, \"property \\\"").Append(member.Wire).AppendLine("\\\" must be a boolean\");");
                builder.AppendLine("        }");
                builder.Append("        var ").Append(value).Append(" = ").Append(value).AppendLine("Property.GetBoolean();");
                break;
            case Kind.JsonElement when !member.Nullable:
                builder.Append("        if (!element.TryGetProperty(\"").Append(member.Wire).Append("\", out var ").Append(value).AppendLine("Property))");
                builder.AppendLine("        {");
                builder.Append("            return (null, \"missing property \\\"").Append(member.Wire).AppendLine("\\\"\");");
                builder.AppendLine("        }");
                builder.Append("        var ").Append(value).Append(" = ").Append(value).AppendLine("Property.Clone();");
                break;
            case Kind.JsonElement:
                builder.Append("        var ").Append(value).Append(" = element.TryGetProperty(\"").Append(member.Wire).Append("\", out var ")
                    .Append(value).AppendLine("Found) ? " + value + "Found.Clone() : (JsonElement?)null;");
                break;
            case Kind.Nested when !member.Nullable:
                builder.Append("        if (!element.TryGetProperty(\"").Append(member.Wire).Append("\", out var ").Append(value).AppendLine("Property))");
                builder.AppendLine("        {");
                builder.Append("            return (null, \"missing property \\\"").Append(member.Wire).AppendLine("\\\"\");");
                builder.AppendLine("        }");
                builder.Append("        var (").Append(value).Append(", ").Append(name).AppendLine("DecodeError) = " + member.Nested!.Name + "Codec.TryDecode(" + value + "Property);");
                builder.Append("        if (").Append(name).AppendLine("DecodeError is not null)");
                builder.AppendLine("        {");
                builder.Append("            return (null, ").Append(name).AppendLine("DecodeError);");
                builder.AppendLine("        }");
                break;
            case Kind.Nested:
                builder.Append("        ").Append(member.Nested!.Name).AppendLine("? " + value + " = null;");
                builder.Append("        if (element.TryGetProperty(\"").Append(member.Wire).Append("\", out var ").Append(value).AppendLine("Property))");
                builder.AppendLine("        {");
                builder.Append("            var (").Append(name).AppendLine("Decoded, " + name + "DecodeError) = " + member.Nested.Name + "Codec.TryDecode(" + value + "Property);");
                builder.Append("            if (").Append(name).AppendLine("DecodeError is not null)");
                builder.AppendLine("            {");
                builder.Append("                return (null, ").Append(name).AppendLine("DecodeError);");
                builder.AppendLine("            }");
                builder.Append("            ").Append(value).Append(" = ").Append(name).AppendLine("Decoded;");
                builder.AppendLine("        }");
                break;
            case Kind.String:
            case Kind.Int32:
            case Kind.Int64:
            case Kind.Double:
            case Kind.Boolean:
                EmitNullablePrimitiveDecode(builder, member, value);
                break;
        }
    }

    private static void EmitNullablePrimitiveDecode(StringBuilder builder, Member member, string value)
    {
        builder.Append("        ").Append(member.Symbol.Type.ToDisplayString()).Append(' ').Append(value).AppendLine(" = null;");
        builder.Append("        if (element.TryGetProperty(\"").Append(member.Wire).Append("\", out var ").Append(value).AppendLine("Property))");
        builder.AppendLine("        {");
        switch (member.Kind)
        {
            case Kind.String:
                builder.Append("            if (").Append(value).AppendLine("Property.ValueKind != JsonValueKind.String)");
                builder.AppendLine("            {");
                builder.Append("                return (null, \"property \\\"").Append(member.Wire).AppendLine("\\\" must be a string\");");
                builder.AppendLine("            }");
                builder.Append("            ").Append(value).Append(" = ").Append(value).AppendLine("Property.GetString();");
                break;
            case Kind.Int32:
                builder.Append("            if (!").Append(value).AppendLine("Property.TryGetInt32(out var parsedInt32))");
                builder.AppendLine("            {");
                builder.Append("                return (null, \"property \\\"").Append(member.Wire).AppendLine("\\\" must be a number\");");
                builder.AppendLine("            }");
                builder.Append("            ").Append(value).AppendLine(" = parsedInt32;");
                break;
            case Kind.Int64:
                builder.Append("            if (!").Append(value).AppendLine("Property.TryGetInt64(out var parsedInt64))");
                builder.AppendLine("            {");
                builder.Append("                return (null, \"property \\\"").Append(member.Wire).AppendLine("\\\" must be a number\");");
                builder.AppendLine("            }");
                builder.Append("            ").Append(value).AppendLine(" = parsedInt64;");
                break;
            case Kind.Double:
                builder.Append("            if (!").Append(value).AppendLine("Property.TryGetDouble(out var parsedDouble))");
                builder.AppendLine("            {");
                builder.Append("                return (null, \"property \\\"").Append(member.Wire).AppendLine("\\\" must be a number\");");
                builder.AppendLine("            }");
                builder.Append("            ").Append(value).AppendLine(" = parsedDouble;");
                break;
            case Kind.Boolean:
                builder.Append("            if (").Append(value).AppendLine("Property.ValueKind != JsonValueKind.True && " + value + "Property.ValueKind != JsonValueKind.False)");
                builder.AppendLine("            {");
                builder.Append("                return (null, \"property \\\"").Append(member.Wire).AppendLine("\\\" must be a boolean\");");
                builder.AppendLine("            }");
                builder.Append("            ").Append(value).AppendLine(" = " + value + "Property.GetBoolean();");
                break;
        }
        builder.AppendLine("        }");
    }
}
