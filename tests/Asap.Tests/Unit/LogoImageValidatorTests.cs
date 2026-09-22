namespace Asap.Tests.Unit;

[TestClass]
public sealed class LogoImageValidatorTests
{
    [TestMethod]
    public void AcceptsOriginalPngJpegAndGifAndRejectsSpoofedOrUnsafeImages()
    {
        var validator = typeof(global::Program).Assembly.GetType("Asap.Shared.LogoImageValidator", throwOnError: true)!;
        var tryValidate = validator.GetMethods()
            .Single(method => method.Name == "TryValidate" &&
                method.GetParameters()[0].ParameterType == typeof(byte[]));
        var maxBytes = (int)validator.GetField("MaxBytes")!.GetRawConstantValue()!;
        var maxDimension = (int)validator.GetField("MaxDimension")!.GetRawConstantValue()!;
        var png = Png(1, 1);
        var jpeg = Jpeg(1, 1);
        var gif = Gif(1, 1);
        var overSized = new byte[maxBytes + 1];
        var overDimension = Png(maxDimension + 1, 1);

        var cases = new[]
        {
            ("png", png, "image/png", true),
            ("jpeg", jpeg, "image/jpeg", true),
            ("gif", gif, "image/gif", true),
            ("signature mismatch", png, "image/jpeg", false),
            ("svg", "<svg></svg>"u8.ToArray(), "image/svg+xml", false),
            ("oversized", overSized, "image/png", false),
            ("overdimension", overDimension, "image/png", false)
        };

        foreach (var (name, bytes, contentType, expected) in cases)
        {
            var arguments = new object?[] { bytes, contentType, null, null };
            var actual = (bool)tryValidate.Invoke(null, arguments)!;
            var image = arguments[2];
            Assert.AreEqual(expected, actual, name);
            if (expected)
            {
                Assert.IsNotNull(image, name);
                Assert.AreEqual(contentType, image!.GetType().GetProperty("ContentType")!.GetValue(image), name);
                Assert.AreEqual(1, image.GetType().GetProperty("Width")!.GetValue(image), name);
                Assert.AreEqual(1, image.GetType().GetProperty("Height")!.GetValue(image), name);
            }
            else
            {
                Assert.IsNull(image, name);
            }
        }
    }

    private static byte[] Png(int width, int height)
    {
        var result = new byte[24];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(result, 0);
        result[11] = 13;
        "IHDR"u8.CopyTo(result.AsSpan(12));
        WriteBigEndian(result, 16, (uint)width);
        WriteBigEndian(result, 20, (uint)height);
        return result;
    }

    private static byte[] Gif(int width, int height)
    {
        var result = new byte[10];
        "GIF89a"u8.CopyTo(result);
        result[6] = (byte)width;
        result[7] = (byte)(width >> 8);
        result[8] = (byte)height;
        result[9] = (byte)(height >> 8);
        return result;
    }

    private static byte[] Jpeg(int width, int height)
    {
        var result = new byte[]
        {
            0xff, 0xd8,
            0xff, 0xc0,
            0x00, 0x0b,
            0x08,
            0x00, 0x01,
            0x00, 0x01,
            0x01, 0x01, 0x11, 0x00
        };
        result[7] = (byte)(height >> 8);
        result[8] = (byte)height;
        result[9] = (byte)(width >> 8);
        result[10] = (byte)width;
        return result;
    }

    private static void WriteBigEndian(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }
}
