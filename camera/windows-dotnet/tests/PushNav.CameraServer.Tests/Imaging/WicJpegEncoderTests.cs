// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Imaging;

namespace PushNav.CameraServer.Tests.Imaging;

public class WicJpegEncoderTests
{
    [Theory]
    [InlineData(1936, 1216)]
    [InlineData(968, 608)]
    [InlineData(1280, 720)]
    [InlineData(333, 211)] // odd, non-multiple-of-8 sizes
    public void RoundTripAtQuality90StaysWithinErrorBound(int width, int height)
    {
        var source = TestImages.SkyLike(width, height);
        using var encoder = new WicJpegEncoder();
        using var decoder = new WicGrayDecoder();

        byte[] jpeg = encoder.Encode(source, width, height, width, 90);
        var decoded = decoder.Decode(jpeg);

        Assert.Equal(width, decoded.Width);
        Assert.Equal(height, decoded.Height);
        double mae = TestImages.MeanAbsError(source, decoded.Pixels);
        Assert.True(mae < 2.0, $"mean abs error {mae:F3} >= 2 at q90");
    }

    [Fact]
    public void OutputIsAGrayscaleJpegWithTheRequestedSize()
    {
        var source = TestImages.SkyLike(640, 480);
        using var encoder = new WicJpegEncoder();

        byte[] jpeg = encoder.Encode(source, 640, 480, 640);

        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);
        Assert.Equal(0xFF, jpeg[^2]);
        Assert.Equal(0xD9, jpeg[^1]);
        var (w, h, components) = JpegInfo.ReadFrameHeader(jpeg);
        Assert.Equal((640, 480, 1), (w, h, components));
    }

    [Fact]
    public void SizeIsSaneAndQualityControlsIt()
    {
        var source = TestImages.SkyLike(1936, 1216);
        using var encoder = new WicJpegEncoder();

        int q50 = encoder.Encode(source, 1936, 1216, 1936, 50).Length;
        int q90 = encoder.Encode(source, 1936, 1216, 1936, 90).Length;
        int q99 = encoder.Encode(source, 1936, 1216, 1936, 99).Length;

        Assert.True(q50 < q90 && q90 < q99, $"sizes not monotonic: q50={q50} q90={q90} q99={q99}");
        Assert.InRange(q90, 20_000, 1_200_000); // far below the 2.35 MB raw frame
    }

    [Fact]
    public void RespectsSourceStride()
    {
        const int width = 100, height = 60, stride = 128;
        var packed = TestImages.SkyLike(width, height);
        var padded = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            packed.AsSpan(y * width, width).CopyTo(padded.AsSpan(y * stride, width));
            padded.AsSpan((y * stride) + width, stride - width).Fill(0xEE);
        }

        using var encoder = new WicJpegEncoder();
        using var decoder = new WicGrayDecoder();
        var decoded = decoder.Decode(encoder.Encode(padded, width, height, stride, 95));

        Assert.Equal(width, decoded.Width);
        Assert.True(TestImages.MeanAbsError(packed, decoded.Pixels) < 2.0);
    }

    [Fact]
    public void EncodesIntoACallerBufferAndReportsTheLength()
    {
        var source = TestImages.SkyLike(256, 256);
        using var encoder = new WicJpegEncoder();
        var buffer = new byte[WicJpegEncoder.MaxEncodedSize(256, 256)];

        int n = encoder.Encode(source, 256, 256, 256, 90, buffer);

        Assert.InRange(n, 1000, buffer.Length);
        Assert.Equal(0xD8, buffer[1]);
        Assert.Equal(0xD9, buffer[n - 1]);
    }

    [Fact]
    public void WorstCaseNoiseFitsTheAdvertisedMaximumSize()
    {
        var rng = new Random(1);
        var noise = new byte[512 * 512];
        rng.NextBytes(noise);
        using var encoder = new WicJpegEncoder();

        byte[] jpeg = encoder.Encode(noise, 512, 512, 512, 100);

        Assert.True(jpeg.Length <= WicJpegEncoder.MaxEncodedSize(512, 512));
    }

    [Fact]
    public void TooSmallDestinationFailsInsteadOfWritingOutOfBounds()
    {
        var source = TestImages.SkyLike(256, 256);
        using var encoder = new WicJpegEncoder();

        Assert.ThrowsAny<Exception>(() => encoder.Encode(source, 256, 256, 256, 90, new byte[100]));
    }

    [Theory]
    [InlineData(0, 10, 10, 90)]
    [InlineData(10, 0, 10, 90)]
    [InlineData(10, 10, 5, 90)]
    [InlineData(10, 10, 10, 0)]
    [InlineData(10, 10, 10, 101)]
    public void RejectsInvalidArguments(int width, int height, int stride, int quality)
    {
        using var encoder = new WicJpegEncoder();
        Assert.ThrowsAny<ArgumentException>(() => encoder.Encode(new byte[1000], width, height, stride, quality));
    }

    [Fact]
    public void RejectsAShortPixelBuffer()
    {
        using var encoder = new WicJpegEncoder();
        Assert.Throws<ArgumentException>(() => encoder.Encode(new byte[10], 10, 10, 10, 90));
    }

    [Fact]
    public void EncoderWorksOnAFreshThread()
    {
        var source = TestImages.SkyLike(64, 64);
        byte[]? jpeg = null;
        var t = new Thread(() =>
        {
            using var encoder = new WicJpegEncoder();
            jpeg = encoder.Encode(source, 64, 64, 64);
        });
        t.Start();
        t.Join();
        Assert.NotNull(jpeg);
        Assert.Equal(0xD8, jpeg[1]);
    }
}
