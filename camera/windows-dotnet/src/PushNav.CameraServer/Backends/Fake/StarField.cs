// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Imaging;

namespace PushNav.CameraServer.Backends.Fake;

/// <summary>
/// Deterministic synthetic night-sky frame for the fake backend when no <c>--fake-images</c> are given:
/// a dim gradient background, sensor-like noise and a few hundred Gaussian stars with a realistic
/// brightness distribution (many faint, few bright). It is not meant to be plate-solvable.
/// </summary>
internal static class StarField
{
    /// <summary>The reference sensor, IDS UI-306xCP-M (spec 2): 1936 x 1216.</summary>
    public const int DefaultWidth = 1936;

    public const int DefaultHeight = 1216;

    public static GrayImage Render(int width = DefaultWidth, int height = DefaultHeight, int seed = 20261118, int starCount = 350)
    {
        var rng = new Random(seed);
        var field = new float[width * height];

        // Background: faint gradient (sky glow towards one corner).
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                field[(y * width) + x] = 14f + (10f * x / width) + (6f * y / height);
            }
        }

        // Stars: peak follows a steep power law, size grows slightly with brightness.
        for (int i = 0; i < starCount; i++)
        {
            double cx = rng.NextDouble() * width;
            double cy = rng.NextDouble() * height;
            double peak = Math.Min(250.0, 12.0 / Math.Pow(1.0 - (rng.NextDouble() * 0.985), 0.8));
            double sigma = 1.1 + (peak / 250.0 * 1.6);
            int radius = (int)Math.Ceiling(4 * sigma);
            double inv = 1.0 / (2 * sigma * sigma);
            int x0 = Math.Max(0, (int)cx - radius);
            int x1 = Math.Min(width - 1, (int)cx + radius);
            int y0 = Math.Max(0, (int)cy - radius);
            int y1 = Math.Min(height - 1, (int)cy + radius);
            for (int y = y0; y <= y1; y++)
            {
                double dy = y - cy;
                for (int x = x0; x <= x1; x++)
                {
                    double dx = x - cx;
                    field[(y * width) + x] += (float)(peak * Math.Exp(-((dx * dx) + (dy * dy)) * inv));
                }
            }
        }

        // Sensor noise: sum of uniforms is close enough to Gaussian (sigma about 2.5).
        var pixels = new byte[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            double noise = (rng.NextDouble() + rng.NextDouble() + rng.NextDouble() + rng.NextDouble() - 2.0) * 4.3;
            pixels[i] = (byte)Math.Clamp((int)Math.Round(field[i] + noise), 0, 255);
        }

        return new GrayImage(width, height, pixels);
    }
}
