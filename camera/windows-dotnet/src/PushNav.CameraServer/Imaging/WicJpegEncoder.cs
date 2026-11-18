// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Imaging;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;

namespace PushNav.CameraServer.Imaging;

/// <summary>
/// 8-bit grayscale JPEG encoder on top of the Windows Imaging Component (spec 3.3: part of Windows, nothing
/// to redistribute). Works under Native AOT through the CsWin32 struct/vtable COM wrappers (no ComImport).
/// An instance owns one WIC factory and is NOT thread-safe: use one encoder per capture thread.
/// </summary>
internal sealed unsafe class WicJpegEncoder : IDisposable
{
    /// <summary>Quality used when the caller does not choose one (spec: 90).</summary>
    public const int DefaultQuality = 90;

    private IWICImagingFactory* _factory;

    public WicJpegEncoder()
    {
        ComRuntime.EnsureInitialized();
        IWICImagingFactory* factory;
        PInvoke.CoCreateInstance(PInvoke.CLSID_WICImagingFactory, null, CLSCTX.CLSCTX_INPROC_SERVER, out factory).ThrowOnFailure();
        _factory = factory;
    }

    /// <summary>
    /// Destination size that always fits a <paramref name="width"/> x <paramref name="height"/> GRAY8 image at any
    /// quality. Real frames are far smaller; incompressible noise at q100 needs more than the raw size, hence the factor 2.
    /// </summary>
    public static int MaxEncodedSize(int width, int height) => checked((2 * width * height) + 65536);

    /// <summary>Encodes into a new array of exactly the encoded length.</summary>
    public byte[] Encode(ReadOnlySpan<byte> gray, int width, int height, int stride, int quality = DefaultQuality)
    {
        var buffer = new byte[MaxEncodedSize(width, height)];
        int n = Encode(gray, width, height, stride, quality, buffer);
        return buffer.AsSpan(0, n).ToArray();
    }

    /// <summary>
    /// Encodes a GRAY8 image into <paramref name="destination"/> and returns the number of bytes written.
    /// </summary>
    /// <param name="gray">Source pixels, <c>stride * (height - 1) + width</c> bytes at least.</param>
    /// <param name="width">Width in pixels (1..65535, JPEG limit).</param>
    /// <param name="height">Height in pixels (1..65535).</param>
    /// <param name="stride">Bytes per source row (at least width).</param>
    /// <param name="quality">1..100.</param>
    /// <param name="destination">Output; size it with <see cref="MaxEncodedSize"/>.</param>
    public int Encode(ReadOnlySpan<byte> gray, int width, int height, int stride, int quality, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_factory == null, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);
        ArgumentOutOfRangeException.ThrowIfLessThan(quality, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, 100);
        long needed = ((long)stride * (height - 1)) + width;
        if (gray.Length < needed)
        {
            throw new ArgumentException($"Pixel buffer too small: {gray.Length} bytes, need {needed}.", nameof(gray));
        }

        ComRuntime.EnsureInitialized();

        IWICStream* stream = null;
        IWICBitmapEncoder* encoder = null;
        IWICBitmapFrameEncode* frame = null;
        IPropertyBag2* bag = null;
        try
        {
            fixed (byte* dst = destination)
            fixed (byte* src = gray)
            {
                IWICStream* newStream;
                _factory->CreateStream(&newStream);
                stream = newStream;
                stream->InitializeFromMemory(dst, (uint)destination.Length);

                Guid jpegContainer = PInvoke.GUID_ContainerFormatJpeg;
                encoder = _factory->CreateEncoder(&jpegContainer, null);
                encoder->Initialize((IStream*)stream, WICBitmapEncoderCacheOption.WICBitmapEncoderNoCache);

                IWICBitmapFrameEncode* newFrame;
                IPropertyBag2* newBag;
                encoder->CreateNewFrame(&newFrame, &newBag);
                frame = newFrame;
                bag = newBag;

                SetImageQuality(bag, quality / 100f);
                frame->Initialize(bag);
                frame->SetSize((uint)width, (uint)height);

                Guid format = PInvoke.GUID_WICPixelFormat8bppGray;
                frame->SetPixelFormat(&format);
                if (format != PInvoke.GUID_WICPixelFormat8bppGray)
                {
                    throw new InvalidOperationException("WIC JPEG encoder did not accept 8bpp gray input.");
                }

                frame->WritePixels((uint)height, (uint)stride, (uint)needed, src);
                frame->Commit();
                encoder->Commit();

                ulong position;
                ((IStream*)stream)->Seek(0, SeekOrigin.Current, &position);

                // WIC reports success without data when the memory stream is too small; never trust the length alone.
                int length = checked((int)position);
                if (length < 4 || destination[0] != 0xFF || destination[1] != 0xD8 || destination[length - 2] != 0xFF || destination[length - 1] != 0xD9)
                {
                    throw new InvalidOperationException(
                        $"WIC produced no complete JPEG (length {length}, destination {destination.Length} bytes); destination too small?");
                }

                return length;
            }
        }
        finally
        {
            if (bag != null)
            {
                bag->Release();
            }

            if (frame != null)
            {
                frame->Release();
            }

            if (encoder != null)
            {
                encoder->Release();
            }

            if (stream != null)
            {
                stream->Release();
            }
        }
    }

    public void Dispose()
    {
        if (_factory != null)
        {
            _factory->Release();
            _factory = null;
        }
    }

    private static void SetImageQuality(IPropertyBag2* bag, float quality)
    {
        fixed (char* name = "ImageQuality")
        {
            PROPBAG2 prop = default;
            prop.dwType = 1; // PROPBAG2_TYPE_DATA
            prop.vt = VARENUM.VT_R4;
            prop.pstrName = name;
            VARIANT value = default;
            value.Anonymous.Anonymous.vt = VARENUM.VT_R4;
            value.Anonymous.Anonymous.Anonymous.fltVal = quality;
            bag->Write(1, &prop, &value);
        }
    }
}
