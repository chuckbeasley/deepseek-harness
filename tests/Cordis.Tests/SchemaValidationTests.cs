using System.Text.RegularExpressions;
using Cordis.Schemastery;

namespace Cordis.Tests;

/// <summary>Schema definition and validation behavior for the minimal Schemastery port.</summary>
public static class SchemaValidationTests
{
    private static Dictionary<string, object?> Obj(params (string Key, object? Value)[] pairs)
        => pairs.ToDictionary(pair => pair.Key, pair => pair.Value);

    private static Schema PersonSchema() => Schema.Object(new Dictionary<string, Schema>
    {
        ["name"] = Schema.String().Required(),
        ["age"] = Schema.Number(),
    });

    public static void ValidObjectWithTypedFieldsNormalizes()
    {
        var result = (Dictionary<string, object?>)PersonSchema().Validate(Obj(("name", "alice"), ("age", 30)))!;
        Assert.Equal("alice", result["name"]);
        Assert.Equal(30, result["age"]); // the CLR number is preserved as-is
    }

    public static void InvalidFieldReportsStructuredError()
    {
        var schema = Schema.Object(new Dictionary<string, Schema> { ["age"] = Schema.Number() });
        var error = Assert.Throws<ValidationError>(() => schema.Validate(Obj(("age", "not-a-number"))));
        Assert.Equal(new object[] { "age" }, error.Path);
        Assert.Equal("$.age expected number but got not-a-number", error.Message);
        Assert.Equal("expected number but got not-a-number", error.RawMessage);
    }

    public static void MissingRequiredFieldThrowsWithPath()
    {
        var error = Assert.Throws<ValidationError>(() => PersonSchema().Validate(Obj(("age", 1))));
        Assert.Equal(new object[] { "name" }, error.Path);
        Assert.Equal("$.name missing required value", error.Message);
    }

    public static void MissingOptionalFieldIsOmittedFromOutput()
    {
        var result = (Dictionary<string, object?>)PersonSchema().Validate(Obj(("name", "bob")))!;
        Assert.Equal(new[] { "name" }, result.Keys.ToList());
    }

    public static void DefaultSuppliesMissingValue()
    {
        var schema = Schema.Object(new Dictionary<string, Schema>
        {
            ["name"] = Schema.String().Default("anonymous"),
        });
        var result = (Dictionary<string, object?>)schema.Validate(Obj())!;
        Assert.Equal("anonymous", result["name"]);
    }

    public static void NestedSchemaPathsAreStructured()
    {
        var inner = Schema.Object(new Dictionary<string, Schema> { ["x"] = Schema.Number().Required() });
        var outer = Schema.Object(new Dictionary<string, Schema> { ["inner"] = inner });
        var error = Assert.Throws<ValidationError>(() => outer.Validate(Obj(("inner", Obj()))));
        Assert.Equal(new object[] { "inner", "x" }, error.Path);
        Assert.Equal("$.inner.x missing required value", error.Message);
    }

    public static void NestedValidObjectPasses()
    {
        var inner = Schema.Object(new Dictionary<string, Schema> { ["x"] = Schema.Number() });
        var outer = Schema.Object(new Dictionary<string, Schema> { ["inner"] = inner });
        var result = (Dictionary<string, object?>)outer.Validate(Obj(("inner", Obj(("x", 5)))))!;
        var nested = Assert.IsType<Dictionary<string, object?>>(result["inner"]);
        Assert.Equal(5, nested["x"]);
    }

    public static void ArrayValidatesEachElement()
    {
        var result = (object?[])Schema.Array(Schema.String()).Validate(new object[] { "a", "b" })!;
        Assert.Equal(new object?[] { "a", "b" }, result);
    }

    public static void ArrayElementErrorIncludesIndex()
    {
        var error = Assert.Throws<ValidationError>(() => Schema.Array(Schema.String()).Validate(new object[] { "a", 42 }));
        Assert.Equal(new object[] { 1 }, error.Path);
        Assert.Equal("$[1] expected string but got 42", error.Message);
    }

    public static void TryValidateReturnsStructuredErrorsWithoutThrowing()
    {
        var schema = Schema.Object(new Dictionary<string, Schema> { ["age"] = Schema.Number() });
        var failure = schema.TryValidate(Obj(("age", "x")));
        Assert.False(failure.IsValid);
        var error = Assert.Single(failure.Errors);
        Assert.Equal(new object[] { "age" }, error.Path);

        var success = schema.TryValidate(Obj(("age", 3)));
        Assert.True(success.IsValid);
        Assert.Empty(success.Errors);
    }

    public static void UnionAcceptsAnyMemberAndReportsUnionType()
    {
        var schema = Schema.Union(new[] { Schema.String(), Schema.Number() });
        Assert.Equal("x", schema.Validate("x"));
        Assert.Equal(5, schema.Validate(5));
        var error = Assert.Throws<ValidationError>(() => schema.Validate(true));
        Assert.Contains("expected string | number", error.Message);
    }

    public static void ConstAcceptsExactValue()
    {
        var schema = Schema.Const("fixed");
        Assert.Equal("fixed", schema.Validate("fixed"));
        Assert.Throws<ValidationError>(() => schema.Validate("other"));
    }

    public static void NumberRangeConstraintsEnforceBounds()
    {
        var schema = Schema.Number().Min(0).Max(10);
        Assert.Equal(5, schema.Validate(5));
        Assert.Throws<ValidationError>(() => schema.Validate(11));
        Assert.Throws<ValidationError>(() => schema.Validate(-1));
    }

    public static void NaturalRejectsNegativeAndFractionalValues()
    {
        var schema = Schema.Natural();
        Assert.Equal(3, schema.Validate(3));
        Assert.Throws<ValidationError>(() => schema.Validate(-1));
        Assert.Throws<ValidationError>(() => schema.Validate(1.5));
    }

    public static void PatternConstrainsStrings()
    {
        var schema = Schema.String().Pattern(new Regex("^[a-z]+$"));
        Assert.Equal("abc", schema.Validate("abc"));
        var error = Assert.Throws<ValidationError>(() => schema.Validate("ABC"));
        Assert.Contains("regexp", error.Message);
    }

    public static void AutofixRemovesInvalidProperty()
    {
        var schema = Schema.Object(new Dictionary<string, Schema> { ["age"] = Schema.Number() });
        var input = Obj(("age", "bad"));
        var result = (Dictionary<string, object?>)schema.Validate(input, new SchemaOptions { Autofix = true })!;
        Assert.Empty(result);
        Assert.False(input.ContainsKey("age"));
    }

    public static void TransformConvertsValidatedValue()
    {
        var schema = Schema.Transform(Schema.String(), (value, _) => int.Parse((string)value!));
        Assert.Equal(42, schema.Validate("42"));
    }

    public static void IntersectMergesObjectOutputs()
    {
        var a = Schema.Object(new Dictionary<string, Schema> { ["a"] = Schema.Number() });
        var b = Schema.Object(new Dictionary<string, Schema> { ["b"] = Schema.String() });
        var schema = Schema.Intersect(new[] { a, b });
        var result = (Dictionary<string, object?>)schema.Validate(Obj(("a", 1), ("b", "x")))!;
        Assert.Equal(1, result["a"]);
        Assert.Equal("x", result["b"]);
    }

    public static void DictValidatesValuesAndPaths()
    {
        var schema = Schema.Dict(Schema.Number());
        var result = (Dictionary<string, object?>)schema.Validate(Obj(("a", 1), ("b", 2)))!;
        Assert.Equal(1, result["a"]);
        Assert.Equal(2, result["b"]);
        var error = Assert.Throws<ValidationError>(() => schema.Validate(Obj(("a", "x"))));
        Assert.Equal(new object[] { "a" }, error.Path);
    }

    public static void LazySupportsRecursiveSchemas()
    {
        Schema? node = null;
        var leaf = Schema.Const(null!);
        node = Schema.Lazy(() => Schema.Object(new Dictionary<string, Schema>
        {
            ["value"] = Schema.Number().Required(),
            ["next"] = Schema.Union(new[] { leaf, node! }),
        }));
        var result = (Dictionary<string, object?>)node.Validate(Obj(("value", 1), ("next", Obj(("value", 2), ("next", null)))))!;
        var next = Assert.IsType<Dictionary<string, object?>>(result["next"]);
        Assert.Equal(2, next["value"]);
        Assert.Null(next["next"]);
    }

    public static void ToStringFormatsTypeStrings()
    {
        var schema = Schema.Object(new Dictionary<string, Schema>
        {
            ["name"] = Schema.String(),
            ["age"] = Schema.Number().Required(),
        });
        Assert.Equal("{ name?: string, age: number }", schema.ToString());
        Assert.Equal("string[]", Schema.Array(Schema.String()).ToString());
        Assert.Equal("string | number", Schema.Union(new[] { Schema.String(), Schema.Number() }).ToString());
    }

    public static void DefaultValueIsClonedPerValidation()
    {
        var shared = Obj(("x", 1));
        var schema = Schema.Dict(Schema.Number()).Default(shared);
        var first = (Dictionary<string, object?>)schema.Validate(null)!;
        first["x"] = 99;
        var second = (Dictionary<string, object?>)schema.Validate(null)!;
        Assert.Equal(1, second["x"]);
    }

    public static void FromInfersPrimitiveSchemas()
    {
        var constant = Schema.From("x");
        Assert.Equal("x", constant.Validate("x"));
        Assert.Throws<ValidationError>(() => constant.Validate("y"));
        Assert.IsType<Schema>(Schema.From(Schema.String()));
        Assert.Equal("any", Schema.From(null).ToString());
    }

    private sealed class Person
    {
        public string? Name { get; set; }

        public int Age { get; set; }
    }

    public static void ObjectAcceptsPocoInput()
    {
        var schema = Schema.Object(new Dictionary<string, Schema>
        {
            ["Name"] = Schema.String().Required(),
            ["Age"] = Schema.Number(),
        });
        var result = (Dictionary<string, object?>)schema.Validate(new Person { Name = "alice", Age = 30 })!;
        Assert.Equal("alice", result["Name"]);
        Assert.Equal(30, result["Age"]);
    }
}

