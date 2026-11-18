// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

namespace PushNav.CameraServer.Imaging;

/// <summary>
/// Conversions from the raw formats UVC cameras deliver to GRAY8 (the JPEG encoder input).
/// Only the luma samples are used; chroma is dropped on purpose (plate solving is mono).
/// </summary>
internal static class PixelConversions
{
    /// <summary>
    /// YUY2 (YUYV 4:2:2, bytes Y0 U Y1 V per two pixels) to GRAY8: takes every even byte.
    /// </summary>
    /// <param name="yuy2">Source; each row has <paramref name="srcStride"/> bytes, of which the first <c>2 * width</c> are pixels.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="srcStride">Bytes per source row (at least 2 * width).</param>
    /// <param name="gray">Destination, at least <c>dstStride * (height - 1) + width</c> bytes.</param>
    /// <param name="dstStride">Bytes per destination row (at least width).</param>
    public static void Yuy2ToGray8(ReadOnlySpan<byte> yuy2, int width, int height, int srcStride, Span<byte> gray, int dstStride)
    {
        ValidateDimensions(width, height);
        if (srcStride < 2 * width)
        {
            throw new ArgumentOutOfRangeException(nameof(srcStride), "YUY2 stride must be at least 2 * width.");
        }

        if (dstStride < width)
        {
            throw new ArgumentOutOfRangeException(nameof(dstStride), "GRAY8 stride must be at least width.");
        }

        RequireLength(yuy2.Length, ((long)srcStride * (height - 1)) + (2L * width), nameof(yuy2));
        RequireLength(gray.Length, ((long)dstStride * (height - 1)) + width, nameof(gray));

        for (int y = 0; y < height; y++)
        {
            var srcRow = yuy2.Slice(y * srcStride, 2 * width);
            var dstRow = gray.Slice(y * dstStride, width);
            for (int x = 0; x < dstRow.Length; x++)
            {
                dstRow[x] = srcRow[2 * x];
            }
        }
    }

    /// <summary>
    /// NV12 (planar 4:2:0: a Y plane, then interleaved UV) to GRAY8: copies the Y plane row by row.
    /// </summary>
    /// <param name="nv12">Source; the Y plane starts at offset 0 with <paramref name="srcStride"/> bytes per row.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="srcStride">Bytes per source row (at least width).</param>
    /// <param name="gray">Destination, at least <c>dstStride * (height - 1) + width</c> bytes.</param>
    /// <param name="dstStride">Bytes per destination row (at least width).</param>
    public static void Nv12ToGray8(ReadOnlySpan<byte> nv12, int width, int height, int srcStride, Span<byte> gray, int dstStride)
    {
        ValidateDimensions(width, height);
        if (srcStride < width)
        {
            throw new ArgumentOutOfRangeException(nameof(srcStride), "NV12 stride must be at least width.");
        }

        if (dstStride < width)
        {
            throw new ArgumentOutOfRangeException(nameof(dstStride), "GRAY8 stride must be at least width.");
        }

        // Only the Y plane is read, but a valid NV12 buffer also carries the UV plane; require it so a
        // truncated buffer (wrong format guess) is caught here instead of giving a plausible-looking image.
        RequireLength(nv12.Length, ((long)srcStride * height) + ((long)srcStride * ((height + 1) / 2)), nameof(nv12));
        RequireLength(gray.Length, ((long)dstStride * (height - 1)) + width, nameof(gray));

        for (int y = 0; y < height; y++)
        {
            nv12.Slice(y * srcStride, width).CopyTo(gray.Slice(y * dstStride, width));
        }
    }

    private static void ValidateDimensions(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
    }

    private static void RequireLength(int actual, long required, string paramName)
    {
        if (actual < required)
        {
            throw new ArgumentException($"Buffer too small: {actual} bytes, need {required}.", paramName);
        }
    }
}
