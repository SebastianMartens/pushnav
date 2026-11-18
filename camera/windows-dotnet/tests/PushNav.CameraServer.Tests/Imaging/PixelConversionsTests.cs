// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Imaging;

namespace PushNav.CameraServer.Tests.Imaging;

public class PixelConversionsTests
{
    [Fact]
    public void Yuy2TakesTheLumaBytes()
    {
        // 4x2 image. Y values 10,20,30,40 / 50,60,70,80, chroma bytes 0xAA / 0xBB must be ignored.
        byte[] yuy2 =
        [
            10, 0xAA, 20, 0xBB, 30, 0xAA, 40, 0xBB,
            50, 0xAA, 60, 0xBB, 70, 0xAA, 80, 0xBB,
        ];
        var gray = new byte[8];

        PixelConversions.Yuy2ToGray8(yuy2, 4, 2, 8, gray, 4);

        Assert.Equal(new byte[] { 10, 20, 30, 40, 50, 60, 70, 80 }, gray);
    }

    [Fact]
    public void Yuy2HonoursSourceAndDestinationStride()
    {
        // 2x2 image, source stride 6 (2 padding bytes), destination stride 3 (1 padding byte, left untouched).
        byte[] yuy2 =
        [
            1, 0x80, 2, 0x80, 0xEE, 0xEE,
            3, 0x80, 4, 0x80, 0xEE, 0xEE,
        ];
        var gray = new byte[(3 * 2) - 1];
        gray.AsSpan().Fill(0x55);

        PixelConversions.Yuy2ToGray8(yuy2, 2, 2, 6, gray, 3);

        Assert.Equal(new byte[] { 1, 2, 0x55, 3, 4 }, gray);
    }

    [Fact]
    public void Nv12CopiesTheYPlaneAndIgnoresChroma()
    {
        // 4x2 image: Y plane 8 bytes, UV plane 4 bytes (one row of 2 UV pairs).
        byte[] nv12 = [11, 12, 13, 14, 15, 16, 17, 18, 0x80, 0x81, 0x82, 0x83];
        var gray = new byte[8];

        PixelConversions.Nv12ToGray8(nv12, 4, 2, 4, gray, 4);

        Assert.Equal(new byte[] { 11, 12, 13, 14, 15, 16, 17, 18 }, gray);
    }

    [Fact]
    public void Nv12HonoursStrides()
    {
        // 2x2 image, source stride 4 (padding), destination stride 2.
        byte[] nv12 =
        [
            1, 2, 0xEE, 0xEE,
            3, 4, 0xEE, 0xEE,
            0x80, 0x80, 0xEE, 0xEE,
        ];
        var gray = new byte[4];

        PixelConversions.Nv12ToGray8(nv12, 2, 2, 4, gray, 2);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, gray);
    }

    [Fact]
    public void ConvertsAFullSizeFrame()
    {
        const int w = 1280, h = 720;
        var yuy2 = new byte[w * h * 2];
        for (int i = 0; i < w * h; i++)
        {
            yuy2[2 * i] = (byte)(i % 251);
            yuy2[(2 * i) + 1] = 0x80;
        }

        var gray = new byte[w * h];
        PixelConversions.Yuy2ToGray8(yuy2, w, h, w * 2, gray, w);

        for (int i = 0; i < gray.Length; i += 997)
        {
            Assert.Equal((byte)(i % 251), gray[i]);
        }
    }

    [Fact]
    public void RejectsTooSmallBuffersAndBadStrides()
    {
        Assert.Throws<ArgumentException>(() => PixelConversions.Yuy2ToGray8(new byte[10], 4, 2, 8, new byte[8], 4));
        Assert.Throws<ArgumentException>(() => PixelConversions.Yuy2ToGray8(new byte[16], 4, 2, 8, new byte[3], 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelConversions.Yuy2ToGray8(new byte[16], 4, 2, 6, new byte[8], 4));
        Assert.Throws<ArgumentException>(() => PixelConversions.Nv12ToGray8(new byte[8], 4, 2, 4, new byte[8], 4)); // UV plane missing
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelConversions.Nv12ToGray8(new byte[12], 4, 2, 3, new byte[8], 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelConversions.Nv12ToGray8(new byte[12], 0, 2, 4, new byte[8], 4));
    }

    [Fact]
    public void Bin2x2AveragesBlocks()
    {
        var img = new GrayImage(4, 2, [0, 4, 10, 10, 4, 0, 10, 11]);

        var binned = img.Bin2x2();

        Assert.Equal((2, 1), (binned.Width, binned.Height));
        Assert.Equal(new byte[] { 2, 10 }, binned.Pixels); // (0+4+4+0)/4 = 2 ; (10+10+10+11+2)/4 = 10
    }
}
