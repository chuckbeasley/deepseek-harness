using System.Text;

namespace Cordis.Cosmokit;

/// <summary>Base64 and hex conversion helpers (port of cosmokit <c>Binary</c>).</summary>
public static class Binary
{
    /// <summary>Decodes a base64 string into bytes.</summary>
    /// <exception cref="FormatException"><paramref name="source"/> is not valid base64.</exception>
    public static byte[] FromBase64(string source) => Convert.FromBase64String(source);

    /// <summary>Encodes bytes as base64.</summary>
    public static string ToBase64(byte[] source) => Convert.ToBase64String(source);

    /// <summary>
    /// Decodes a hex string into bytes. An odd-length source drops its final
    /// character before decoding, mirroring the cosmokit behavior.
    /// </summary>
    public static byte[] FromHex(string source)
    {
        var hex = source.Length % 2 == 0 ? source : source[..^1];
        var buffer = new byte[hex.Length / 2];
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return buffer;
    }

    /// <summary>Encodes bytes as lowercase hex.</summary>
    public static string ToHex(byte[] source)
    {
        var builder = new StringBuilder(source.Length * 2);
        foreach (var b in source)
        {
            builder.Append(b.ToString("x2"));
        }
        return builder.ToString();
    }
}
