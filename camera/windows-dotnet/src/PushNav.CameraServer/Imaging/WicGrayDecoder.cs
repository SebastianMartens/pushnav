// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Imaging;
using Windows.Win32.System.Com;

namespace PushNav.CameraServer.Imaging;

/// <summary>
/// Decodes any image WIC knows (PNG, JPEG, BMP, ...) to GRAY8. Used by the fake backend to load
/// <c>--fake-images</c> and by tests to read back the encoder output. Not thread-safe; one instance per thread.
/// </summary>
internal sealed unsafe class WicGrayDecoder : IDisposable
{
    private IWICImagingFactory* _factory;

    public WicGrayDecoder()
    {
        ComRuntime.EnsureInitialized();
        IWICImagingFactory* factory;
        PInvoke.CoCreateInstance(PInvoke.CLSID_WICImagingFactory, null, CLSCTX.CLSCTX_INPROC_SERVER, out factory).ThrowOnFailure();
        _factory = factory;
    }

    /// <summary>Decodes an image file to GRAY8 (color images are converted to luma by WIC).</summary>
    public GrayImage DecodeFile(string path)
    {
        ObjectDisposedException.ThrowIf(_factory == null, this);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ComRuntime.EnsureInitialized();

        IWICBitmapDecoder* decoder = null;
        try
        {
            fixed (char* wzPath = path)
            {
                decoder = _factory->CreateDecoderFromFilename(
                    new PCWSTR(wzPath),
                    null,
                    (GENERIC_ACCESS_RIGHTS)0x80000000u, // GENERIC_READ
                    WICDecodeOptions.WICDecodeMetadataCacheOnDemand);
                return ReadFirstFrame(decoder);
            }
        }
        finally
        {
            if (decoder != null)
            {
                decoder->Release();
            }
        }
    }

    /// <summary>Decodes an in-memory encoded image (e.g. the JPEG the encoder produced) to GRAY8.</summary>
    public GrayImage Decode(ReadOnlySpan<byte> encoded)
    {
        ObjectDisposedException.ThrowIf(_factory == null, this);
        if (encoded.IsEmpty)
        {
            throw new ArgumentException("No image data.", nameof(encoded));
        }

        ComRuntime.EnsureInitialized();

        IWICStream* stream = null;
        IWICBitmapDecoder* decoder = null;
        try
        {
            fixed (byte* data = encoded)
            {
                IWICStream* newStream;
                _factory->CreateStream(&newStream);
                stream = newStream;
                stream->InitializeFromMemory(data, (uint)encoded.Length);
                decoder = _factory->CreateDecoderFromStream((IStream*)stream, null, WICDecodeOptions.WICDecodeMetadataCacheOnDemand);
                return ReadFirstFrame(decoder);
            }
        }
        finally
        {
            if (decoder != null)
            {
                decoder->Release();
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

    private GrayImage ReadFirstFrame(IWICBitmapDecoder* decoder)
    {
        IWICBitmapFrameDecode* frame = null;
        IWICFormatConverter* converter = null;
        try
        {
            IWICBitmapFrameDecode* newFrame;
            decoder->GetFrame(0, &newFrame);
            frame = newFrame;

            IWICFormatConverter* newConverter;
            _factory->CreateFormatConverter(&newConverter);
            converter = newConverter;

            Guid gray = PInvoke.GUID_WICPixelFormat8bppGray;
            converter->Initialize(
                (IWICBitmapSource*)frame,
                &gray,
                WICBitmapDitherType.WICBitmapDitherTypeNone,
                null,
                0.0,
                WICBitmapPaletteType.WICBitmapPaletteTypeCustom);

            uint width;
            uint height;
            converter->GetSize(&width, &height);
            if (width == 0 || height == 0 || width > 65535 || height > 65535)
            {
                throw new InvalidDataException($"Unsupported image size {width}x{height}.");
            }

            var pixels = new byte[checked((int)(width * height))];
            fixed (byte* dst = pixels)
            {
                converter->CopyPixels(null, width, (uint)pixels.Length, dst);
            }

            return new GrayImage((int)width, (int)height, pixels);
        }
        finally
        {
            if (converter != null)
            {
                converter->Release();
            }

            if (frame != null)
            {
                frame->Release();
            }
        }
    }
}
