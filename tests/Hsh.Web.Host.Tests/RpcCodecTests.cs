using System.Text.Json;

namespace Harness.Web.Host.Tests;

/// <summary>
/// The generated typed codec (Harness.Rpc.Generator): the exact error-vocabulary wire shape, the
/// round trip, and the loud refusal vocabulary. The gateway and mux suites pin the same shape
/// through the real carriers.
/// </summary>
public static class RpcCodecTests
{
    public static void Encode_ProducesTheErrorVocabularyShape()
    {
        var element = RpcErrorCodec.Encode(new RpcError(RpcErrorCodes.Internal, "boom"));
        Assert.Equal(3, CountProperties(element), "the error vocabulary carries code, message, and details");
        Assert.Equal("gateway/internal", element.GetProperty("code").GetString(), "the code rides verbatim");
        Assert.Equal("boom", element.GetProperty("message").GetString(), "the message rides verbatim");
        Assert.Equal(JsonValueKind.Object, element.GetProperty("details").ValueKind, "details is always an object");
        Assert.True(!element.GetProperty("details").EnumerateObject().Any(), "absent details encode as an empty object");
    }

    public static void Encode_CarriesStructuredDetails()
    {
        var details = JsonSerializer.SerializeToElement(new { agentPreset = "shipped", reason = "it ships with the deployment" });
        var element = RpcErrorCodec.Encode(new RpcError("agent-preset/read-only", "refused", details));
        Assert.Equal("shipped", element.GetProperty("details").GetProperty("agentPreset").GetString(), "the details object rides through");
        Assert.Equal("it ships with the deployment", element.GetProperty("details").GetProperty("reason").GetString(), "the details object rides through");
    }

    public static void TryDecode_RoundTripsEncode()
    {
        var original = new RpcError("a/b", "message", JsonSerializer.SerializeToElement(new { x = 1 }));
        var (decoded, error) = RpcErrorCodec.TryDecode(RpcErrorCodec.Encode(original));
        Assert.Null(error, "the round trip must decode cleanly");
        Assert.Equal("a/b", decoded!.Code, "the code round-trips");
        Assert.Equal("message", decoded.Message, "the message round-trips");
        Assert.Equal(1, decoded.Details!.Value.GetProperty("x").GetInt32(), "the details round-trip");
    }

    public static void TryDecode_RejectsMalformedElements()
    {
        var (_, nonObject) = RpcErrorCodec.TryDecode(JsonSerializer.SerializeToElement("nope"));
        Assert.True(nonObject is not null && nonObject.Contains("expected a JSON object", StringComparison.Ordinal), "a non-object is refused");

        var (_, missingCode) = RpcErrorCodec.TryDecode(JsonSerializer.SerializeToElement(new { message = "m" }));
        Assert.True(missingCode is not null && missingCode.Contains("code", StringComparison.Ordinal), "a missing code is refused");

        var (_, wrongType) = RpcErrorCodec.TryDecode(JsonSerializer.SerializeToElement(new { code = 5, message = "m" }));
        Assert.True(wrongType is not null && wrongType.Contains("must be a string", StringComparison.Ordinal), "a numeric code is refused");

        var (decoded, absentDetails) = RpcErrorCodec.TryDecode(JsonSerializer.SerializeToElement(new { code = "a/b", message = "m" }));
        Assert.Null(absentDetails, "absent details are tolerated (the member is nullable)");
        Assert.Null(decoded!.Details, "absent details decode as null");
    }

    private static int CountProperties(JsonElement element)
    {
        var count = 0;
        foreach (var _ in element.EnumerateObject()) count++;
        return count;
    }
}
