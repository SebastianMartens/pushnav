// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

namespace PushNav.CameraServer.Tests;

internal static class TestImages
{
    /// <summary>Deterministic smooth gradient plus a few soft blobs and mild noise (sigma about 2), like a dark sky frame.</summary>
    public static byte[] SkyLike(int width, int height, int seed = 7)
    {
        var rng = new Random(seed);
        var px = new byte[width * height];
        var stars = new (double X, double Y, double Peak)[40];
        for (int i = 0; i < stars.Length; i++)
        {
            stars[i] = (rng.NextDouble() * width, rng.NextDouble() * height, 60 + (rng.NextDouble() * 190));
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double v = 20 + (10.0 * x / width) + (6.0 * y / height);
                foreach (var s in stars)
                {
                    double dx = x - s.X;
                    double dy = y - s.Y;
                    if ((dx * dx) + (dy * dy) < 64)
                    {
                        v += s.Peak * Math.Exp(-((dx * dx) + (dy * dy)) / 3.0);
                    }
                }

                v += (rng.NextDouble() + rng.NextDouble() + rng.NextDouble() - 1.5) * 4.0;
                px[(y * width) + x] = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
            }
        }

        return px;
    }

    public static double MeanAbsError(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        Assert.Equal(a.Length, b.Length);
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            sum += Math.Abs(a[i] - b[i]);
        }

        return (double)sum / a.Length;
    }
}

/// <summary>Minimal JPEG header reader (just enough to check the encoder output is a 1-component image).</summary>
internal static class JpegInfo
{
    public static (int Width, int Height, int Components) ReadFrameHeader(ReadOnlySpan<byte> jpeg)
    {
        Assert.True(jpeg.Length > 4 && jpeg[0] == 0xFF && jpeg[1] == 0xD8, "missing SOI");
        int i = 2;
        while (i + 4 <= jpeg.Length)
        {
            Assert.Equal(0xFF, jpeg[i]);
            byte marker = jpeg[i + 1];
            if (marker == 0xFF)
            {
                i++;
                continue;
            }

            int len = (jpeg[i + 2] << 8) | jpeg[i + 3];
            bool isSof = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (isSof)
            {
                int height = (jpeg[i + 5] << 8) | jpeg[i + 6];
                int width = (jpeg[i + 7] << 8) | jpeg[i + 8];
                return (width, height, jpeg[i + 9]);
            }

            i += 2 + len;
        }

        throw new InvalidDataException("no SOF marker");
    }
}
