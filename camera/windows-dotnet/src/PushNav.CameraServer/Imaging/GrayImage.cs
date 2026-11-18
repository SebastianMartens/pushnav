// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

namespace PushNav.CameraServer.Imaging;

/// <summary>An 8-bit grayscale image with a tightly packed stride (stride == width).</summary>
internal sealed class GrayImage
{
    public GrayImage(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != checked(width * height))
        {
            throw new ArgumentException("Pixel buffer size does not match width * height.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Row-major GRAY8 pixels.</summary>
    public byte[] Pixels { get; }

    /// <summary>
    /// 2x2 box-filter binning (average of each 2x2 block, rounded). An odd trailing row/column is dropped,
    /// like sensor binning that only combines complete blocks.
    /// </summary>
    public GrayImage Bin2x2()
    {
        int w = Width / 2;
        int h = Height / 2;
        if (w == 0 || h == 0)
        {
            throw new InvalidOperationException("Image too small to bin.");
        }

        var dst = new byte[w * h];
        var src = Pixels;
        for (int y = 0; y < h; y++)
        {
            int s0 = 2 * y * Width;
            int s1 = s0 + Width;
            int d = y * w;
            for (int x = 0; x < w; x++)
            {
                int sx = 2 * x;
                int sum = src[s0 + sx] + src[s0 + sx + 1] + src[s1 + sx] + src[s1 + sx + 1];
                dst[d + x] = (byte)((sum + 2) >> 2);
            }
        }

        return new GrayImage(w, h, dst);
    }
}
