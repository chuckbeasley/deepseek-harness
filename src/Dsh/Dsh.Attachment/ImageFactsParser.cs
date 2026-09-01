using System.Security.Cryptography;

namespace Dsh.Attachment;

/// <summary>Parsed image facts for one committed attachment.</summary>
public sealed record ImageFacts(string MediaType, int Width, int Height);

/// <summary>
/// Minimal image-header parsing for attachment admission (port of the TS image-codec surface used
/// by saveImage): PNG/JPEG/GIF/WebP magic-byte verification plus width/height extraction. The
/// bytes' declared format must match the caller's media type, else the ingest refuses.
/// </summary>
public static class ImageFactsParser
{
    private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] Gif87Magic = { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61 };
    private static readonly byte[] Gif89Magic = { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 };
    private static readonly byte[] RiffMagic = { 0x52, 0x49, 0x46, 0x46 };
    private static readonly byte[] WebpMagic = { 0x57, 0x45, 0x42, 0x50 };

    /// <summary>Parse one image buffer, verifying its magic matches <paramref name="declaredType"/>.</summary>
    /// <exception cref="AttachmentError">code IMAGE_TYPE_MISMATCH when the bytes declare a different format.</exception>
    public static ImageFacts Parse(byte[] data, string declaredType)
    {
        if (declaredType == "image/png") return Png(data);
        if (declaredType == "image/jpeg") return Jpeg(data);
        if (declaredType == "image/gif") return Gif(data);
        if (declaredType == "image/webp") return Webp(data);
        throw new AttachmentError($"cannot parse image: unsupported media type {declaredType}", "IMAGE_TYPE_MISMATCH");
    }

    private static ImageFacts Png(byte[] data)
    {
        RequireMagic(data, PngMagic);
        // IHDR: width and height are big-endian at offsets 16 and 20.
        return new ImageFacts("image/png", Be32(data, 16), Be32(data, 20));
    }

    private static ImageFacts Jpeg(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            throw new AttachmentError("cannot parse image: not a JPEG file", "IMAGE_TYPE_MISMATCH");
        }
        var offset = 2;
        while (offset + 9 < data.Length)
        {
            if (data[offset] != 0xFF) { offset++; continue; }
            var marker = data[offset + 1];
            if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7)) { offset += 2; continue; }
            var length = (data[offset + 2] << 8) | data[offset + 3];
            if (marker == 0xC0 || marker == 0xC1 || marker == 0xC2 || marker == 0xC3
                || marker == 0xC5 || marker == 0xC6 || marker == 0xC7 || marker == 0xC9
                || marker == 0xCA || marker == 0xCB || marker == 0xCD || marker == 0xCE || marker == 0xCF)
            {
                var height = (data[offset + 5] << 8) | data[offset + 6];
                var width = (data[offset + 7] << 8) | data[offset + 8];
                return new ImageFacts("image/jpeg", width, height);
            }
            offset += 2 + length;
        }
        throw new AttachmentError("cannot parse image: no JPEG frame found", "IMAGE_TYPE_MISMATCH");
    }

    private static ImageFacts Gif(byte[] data)
    {
        if (!StartsWith(data, Gif87Magic) && !StartsWith(data, Gif89Magic))
        {
            throw new AttachmentError("cannot parse image: not a GIF file", "IMAGE_TYPE_MISMATCH");
        }
        return new ImageFacts("image/gif", data[6] | (data[7] << 8), data[8] | (data[9] << 8));
    }

    private static ImageFacts Webp(byte[] data)
    {
        if (!StartsWith(data, RiffMagic) || data.Length < 12 || !StartsWith(data.AsSpan(8, 4), WebpMagic))
        {
            throw new AttachmentError("cannot parse image: not a WebP file", "IMAGE_TYPE_MISMATCH");
        }
        var chunk = data[12];
        if (chunk == (byte)'V' && data.Length >= 30) // VP8X
        {
            var width = 1 + Le24(data, 24);
            var height = 1 + Le24(data, 27);
            return new ImageFacts("image/webp", width, height);
        }
        if (chunk == (byte)'L' && data.Length >= 25) // VP8L
        {
            var bits = data[21] | (data[22] << 8) | (data[23] << 16);
            return new ImageFacts("image/webp", (bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1);
        }
        if (chunk == (byte)' ' && data.Length >= 30) // VP8 (lossy)
        {
            var width = (data[26] | (data[27] << 8)) & 0x3FFF;
            var height = (data[28] | (data[29] << 8)) & 0x3FFF;
            return new ImageFacts("image/webp", width, height);
        }
        throw new AttachmentError("cannot parse image: unsupported WebP chunk", "IMAGE_TYPE_MISMATCH");
    }

    private static void RequireMagic(byte[] data, byte[] magic)
    {
        if (!StartsWith(data, magic))
        {
            throw new AttachmentError("cannot parse image: bytes do not match the declared image format", "IMAGE_TYPE_MISMATCH");
        }
    }

    private static bool StartsWith(byte[] data, byte[] prefix)
        => data.Length >= prefix.Length && data.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    private static bool StartsWith(ReadOnlySpan<byte> data, byte[] prefix)
        => data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(prefix);

    private static int Be32(byte[] data, int offset)
        => (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

    private static int Le24(byte[] data, int offset)
        => data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
}