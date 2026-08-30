using Cordis.Cosmokit;
using Microsoft.CSharp.RuntimeBinder;

namespace Cordis.Tests;

/// <summary>Brand marker types; each domain owns one, mirroring <c>Branded&lt;B&gt;</c> usage.</summary>
public sealed class SessionId
{
}

/// <summary>A second brand to prove cross-brand non-assignability.</summary>
public sealed class ToolCallId
{
}

/// <summary>Nominal identity semantics for <see cref="BrandedId{TBrand}"/>.</summary>
public static class BrandedIdTests
{
    public static void SameBrandRoundTrips()
    {
        var id = Brand.Of<SessionId>("abc");
        Assert.Equal("abc", id.Value);
        Assert.Equal("abc", id.ToString());

        string unwrapped = id;
        Assert.Equal("abc", unwrapped);

        var rebuilt = BrandedId<SessionId>.From(unwrapped);
        Assert.Equal(id, rebuilt);
        Assert.True(id == rebuilt);
        Assert.Equal(id.GetHashCode(), rebuilt.GetHashCode());
    }

    public static void SameBrandDistinctValuesAreUnequal()
    {
        var a = Brand.Of<SessionId>("a");
        var b = Brand.Of<SessionId>("b");
        Assert.NotEqual(a, b);
        Assert.True(a != b);
        Assert.False(a == b);
    }

    public static void StringLiteralImplicitlyBrands()
    {
        BrandedId<SessionId> id = "session-1";
        Assert.Equal("session-1", id.Value);
    }

    public static void DifferentBrandsAreDistinctTypes()
    {
        Assert.NotEqual(typeof(BrandedId<SessionId>), typeof(BrandedId<ToolCallId>));
        Assert.False(typeof(BrandedId<SessionId>).IsAssignableFrom(typeof(BrandedId<ToolCallId>)));
        Assert.False(typeof(BrandedId<ToolCallId>).IsAssignableFrom(typeof(BrandedId<SessionId>)));
    }

    public static void DifferentBrandsHaveNoRuntimeConversion()
    {
        // The static compiler rejects the direct assignment; the dynamic binder
        // proves at runtime that no implicit conversion operator exists either.
        dynamic session = Brand.Of<SessionId>("s1");
        Assert.Throws<RuntimeBinderException>(() =>
        {
            BrandedId<ToolCallId> toolCall = session;
        });
    }
}
