namespace Asap.Shared;

public sealed record LogoImageInfo(string ContentType, int Width, int Height);

public static class LogoImageValidator
{
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxDimension = 4096;

    public static bool TryDetectContentType(
        ReadOnlySpan<byte> data,
        out string contentType)
    {
        if (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            contentType = "image/png";
            return true;
        }
        if (data.Length >= 3 && data[0] == 0xff && data[1] == 0xd8 && data[2] == 0xff)
        {
            contentType = "image/jpeg";
            return true;
        }
        if (data.Length >= 6 &&
            (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)))
        {
            contentType = "image/gif";
            return true;
        }

        contentType = string.Empty;
        return false;
    }

    public static bool TryValidate(
        byte[] data,
        string? declaredContentType,
        out LogoImageInfo? image,
        out string error) =>
        TryValidate(data.AsSpan(), declaredContentType, out image, out error);

    public static bool TryValidate(
        ReadOnlySpan<byte> data,
        string? declaredContentType,
        out LogoImageInfo? image,
        out string error)
    {
        image = null;
        if (data.Length is <= 0 or > MaxBytes)
        {
            error = "The logo must be between 1 byte and 2 MB.";
            return false;
        }

        var contentType = declaredContentType?.Trim().ToLowerInvariant();
        if (contentType is not ("image/png" or "image/jpeg" or "image/gif"))
        {
            error = "The logo must be a PNG, JPEG, or GIF image.";
            return false;
        }

        if (!TryDetectContentType(data, out var detectedType) ||
            !string.Equals(contentType, detectedType, StringComparison.Ordinal))
        {
            error = "The logo content type does not match its image signature.";
            return false;
        }

        var dimensions = detectedType switch
        {
            "image/png" => ReadPngDimensions(data),
            "image/jpeg" => ReadJpegDimensions(data),
            "image/gif" => ReadGifDimensions(data),
            _ => null
        };
        if (dimensions is not { } size || size.Width is <= 0 or > MaxDimension || size.Height is <= 0 or > MaxDimension)
        {
            error = "The logo dimensions must be between 1 and 4096 pixels.";
            return false;
        }

        image = new LogoImageInfo(detectedType, size.Width, size.Height);
        error = string.Empty;
        return true;
    }

    private static (int Width, int Height)? ReadPngDimensions(ReadOnlySpan<byte> data)
    {
        if (data.Length < 24 ||
            ReadUInt32BigEndian(data[8..12]) < 13 ||
            !data[12..16].SequenceEqual("IHDR"u8))
        {
            return null;
        }

        var width = ReadUInt32BigEndian(data[16..20]);
        var height = ReadUInt32BigEndian(data[20..24]);
        return width <= int.MaxValue && height <= int.MaxValue
            ? ((int)width, (int)height)
            : null;
    }

    private static (int Width, int Height)? ReadGifDimensions(ReadOnlySpan<byte> data) =>
        data.Length >= 10
            ? (data[6] | data[7] << 8, data[8] | data[9] << 8)
            : null;

    private static (int Width, int Height)? ReadJpegDimensions(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xff || data[1] != 0xd8)
        {
            return null;
        }

        var offset = 2;
        while (offset < data.Length)
        {
            while (offset < data.Length && data[offset] == 0xff) offset++;
            if (offset >= data.Length) return null;
            var marker = data[offset++];
            if (marker == 0x00) return null;
            if (marker is 0xd8 or 0xd9) continue;
            if (marker == 0xda) return null;
            if (offset + 2 > data.Length) return null;

            var segmentLength = (data[offset] << 8) | data[offset + 1];
            if (segmentLength < 2 || offset + segmentLength > data.Length) return null;
            if (IsStartOfFrame(marker))
            {
                if (segmentLength < 7) return null;
                var height = (data[offset + 3] << 8) | data[offset + 4];
                var width = (data[offset + 5] << 8) | data[offset + 6];
                return (width, height);
            }

            offset += segmentLength;
        }

        return null;
    }

    private static bool IsStartOfFrame(byte marker) =>
        marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or
            0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf;

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> value) =>
        ((uint)value[0] << 24) |
        ((uint)value[1] << 16) |
        ((uint)value[2] << 8) |
        value[3];
}
