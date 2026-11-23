// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Diagnostics;
using PushNav.CameraServer.Controls;
using PushNav.CameraServer.Imaging;

namespace PushNav.CameraServer.Backends.Fake;

/// <summary>
/// A backend without hardware (spec 1.1 "Fake backend"): replays sample images (or a synthetic star field),
/// simulates exposure / gain / fps / binning with IDS-like ranges, and encodes every frame like a real backend would.
/// Used by the xUnit tests, the protocol conformance suite and CI.
/// </summary>
/// <remarks>
/// Simulated behaviour:
/// <list type="bullet">
/// <item>exposure 92 us .. 4 s, gain 0..100, fps 1..40.98, binning 1 or 2 (2x2 halves the frame size);</item>
/// <item>exposure priority (protocol 11.4.3): a longer exposure lowers fps; a higher fps is clamped to what the exposure allows;
/// beyond 1 s (fps is already at its 1 fps floor) the frame interval simply follows the exposure;</item>
/// <item>brightness follows exposure and gain, and is exactly the source image at the default values.</item>
/// </list>
/// </remarks>
internal sealed class FakeBackend : ICameraBackend
{
    public const string BackendId = "fake";

    public const double DefaultExposureUs = 50_000;
    public const double DefaultGain = 20;
    public const double DefaultFpsValue = 10;
    public const double MinExposureUs = 92;
    public const double MaxExposureUs = 4_000_000;
    public const double MinFps = 1.0;
    public const double MaxFps = 40.98;

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp"];

    private readonly string? _imagesDirectory;
    private readonly int _jpegQuality;
    private readonly Logger _log;
    private readonly object _gate = new();      // guards the simulated control state
    private readonly object _streamLock = new(); // held while a frame is produced / the geometry changes
    private readonly ManualResetEventSlim _wake = new(false);

    private List<GrayImage> _images = [];
    private readonly Dictionary<int, List<GrayImage>> _binned = [];
    private DeviceInfo? _device;
    private double _exposureUs = DefaultExposureUs;
    private double _gain = DefaultGain;
    private double _fps = DefaultFpsValue;
    private int _binning = 1;
    private IFrameSink? _sink;
    private Thread? _thread;
    private volatile bool _stopping;
    private long _framesProduced;

    public FakeBackend(string? imagesDirectory, int jpegQuality, Logger log)
    {
        _imagesDirectory = imagesDirectory;
        _jpegQuality = jpegQuality;
        _log = log;
    }

    public string Id => BackendId;

    public string HelloName => "windows-dotnet-fake";

    public event Action<string>? Fatal;

    public DeviceInfo Device => _device ?? throw new InvalidOperationException("Backend not opened");

    public int DefaultFps => (int)Math.Round(DefaultFpsValue);

    /// <summary>The IDS UI-306xCP-M (IMX174, 5.86 um), whatever the replayed images measure: the sensor is what the engine plans the optics with.</summary>
    public SensorInfo? Sensor { get; } = new(1936, 1216, 5.86, true);

    /// <summary>Frames produced since <see cref="Start"/> (tests, diagnostics).</summary>
    public long FramesProduced => Interlocked.Read(ref _framesProduced);

    public StreamGeometry Geometry
    {
        get
        {
            lock (_gate)
            {
                return GeometryFor(_binning);
            }
        }
    }

    public IReadOnlyList<ControlDescriptor> Controls
    {
        get
        {
            lock (_gate)
            {
                return BuildControls();
            }
        }
    }

    public IReadOnlyList<DeviceInfo> Probe() => [new DeviceInfo(BackendId, "FakeCamera", Serial: "FAKE0001")];

    public void Open(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_gate)
        {
            _images = LoadImages();
            _binned.Clear();
            _device = device;
            _exposureUs = DefaultExposureUs;
            _gain = DefaultGain;
            _fps = DefaultFpsValue;
            _binning = 1;
        }

        var g = Geometry;
        _log.Info($"Fake camera opened: {g.Width}x{g.Height}, {_images.Count} image(s)" +
                  (_imagesDirectory == null ? " (synthetic star field)" : $" from {_imagesDirectory}"));
    }

    public ControlResult SetControl(string id, double value)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (id == ControlIds.Binning)
        {
            // Binning restarts the stream: wait for the frame in flight, so no frame of the old size follows.
            lock (_streamLock)
            {
                lock (_gate)
                {
                    return ApplyLocked(id, value);
                }
            }
        }

        lock (_gate)
        {
            var result = ApplyLocked(id, value);
            if (result.Accepted)
            {
                _wake.Set(); // pace changes (fps, exposure) take effect at once
            }

            return result;
        }
    }

    public void Start(IFrameSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (_device == null)
        {
            throw new InvalidOperationException("Backend not opened");
        }

        if (_thread != null)
        {
            throw new InvalidOperationException("Already started");
        }

        _sink = sink;
        _stopping = false;
        _thread = new Thread(CaptureLoop) { Name = "fake-capture", IsBackground = true };
        _thread.Start();
    }

    public void Stop()
    {
        _stopping = true;
        _wake.Set();
        var thread = _thread;
        _thread = null;
        thread?.Join(TimeSpan.FromSeconds(3));
    }

    public void Dispose()
    {
        Stop();
        _wake.Dispose();
    }

    /// <summary>Test hook: behaves like a lost device (capture stops, <see cref="Fatal"/> is raised).</summary>
    internal void SimulateDeviceLoss(string message)
    {
        _stopping = true;
        _wake.Set();
        Fatal?.Invoke(message);
    }

    // -- controls -----------------------------------------------------------

    private ControlResult ApplyLocked(string id, double value)
    {
        var controls = BuildControls();
        var control = controls.FirstOrDefault(c => c.Id == id);
        if (control == null)
        {
            return ControlResult.Rejected($"unknown control '{id}'");
        }

        if (!control.TryCoerce(value, out double coerced))
        {
            return ControlResult.Rejected($"value {value} not accepted for '{id}'");
        }

        switch (id)
        {
            case ControlIds.Exposure:
                _exposureUs = coerced;
                // Exposure priority: lower fps so the exposure fits into the frame interval.
                _fps = Math.Min(_fps, MaxFpsFor(_exposureUs));
                break;
            case ControlIds.Gain:
                _gain = coerced;
                break;
            case ControlIds.Fps:
                _fps = coerced; // already clamped to the exposure-coupled maximum by the descriptor
                break;
            case ControlIds.Binning:
                _binning = (int)coerced;
                break;
        }

        return ControlResult.Ok;
    }

    private List<ControlDescriptor> BuildControls() =>
    [
        new ControlDescriptor
        {
            Id = ControlIds.Exposure,
            Label = "Exposure",
            Kind = ControlKind.Int,
            Unit = "us",
            Min = MinExposureUs,
            Max = MaxExposureUs,
            Step = 1,
            Cur = _exposureUs,
            Scale = "log",
        },
        new ControlDescriptor
        {
            Id = ControlIds.Gain,
            Label = "Gain",
            Kind = ControlKind.Int,
            Unit = "raw",
            Min = 0,
            Max = 100,
            Step = 1,
            Cur = _gain,
        },
        new ControlDescriptor
        {
            Id = ControlIds.Fps,
            Label = "Frame rate",
            Kind = ControlKind.Float,
            Unit = "fps",
            Min = MinFps,
            Max = MaxFpsFor(_exposureUs),
            Step = 0.01,
            Cur = _fps,
        },
        new ControlDescriptor
        {
            Id = ControlIds.Binning,
            Label = "Binning",
            Kind = ControlKind.Enum,
            Cur = _binning,
            Options = [new ControlOption(1, "1x1"), new ControlOption(2, "2x2")],
            RestartsStream = true,
        },
    ];

    /// <summary>Highest fps the given exposure allows (frame interval must cover the exposure), floored to the 0.01 step.</summary>
    internal static double MaxFpsFor(double exposureUs)
    {
        double limit = Math.Floor(1_000_000.0 / exposureUs * 100) / 100;
        return Math.Clamp(limit, MinFps, MaxFps);
    }

    // -- images -------------------------------------------------------------

    private StreamGeometry GeometryFor(int binning)
    {
        var first = _images.Count > 0 ? _images[0] : throw new InvalidOperationException("Backend not opened");
        return binning == 2
            ? new StreamGeometry(first.Width / 2, first.Height / 2, 2)
            : new StreamGeometry(first.Width, first.Height, 1);
    }

    private List<GrayImage> LoadImages()
    {
        if (_imagesDirectory == null)
        {
            return [StarField.Render()];
        }

        if (!Directory.Exists(_imagesDirectory))
        {
            throw new CameraException($"Fake image directory not found: {_imagesDirectory}");
        }

        var files = Directory.EnumerateFiles(_imagesDirectory)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var images = new List<GrayImage>();
        using var decoder = new WicGrayDecoder();
        foreach (string file in files)
        {
            try
            {
                var image = decoder.DecodeFile(file);
                if (images.Count > 0 && (image.Width != images[0].Width || image.Height != images[0].Height))
                {
                    _log.Warn($"Skipping {Path.GetFileName(file)}: {image.Width}x{image.Height} differs from the first image ({images[0].Width}x{images[0].Height})");
                    continue;
                }

                images.Add(image);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or InvalidDataException)
            {
                _log.Warn($"Skipping {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        if (images.Count == 0)
        {
            throw new CameraException($"No usable images (png/jpg/bmp) in {_imagesDirectory}");
        }

        return images;
    }

    private GrayImage SourceFor(int binning, long frameIndex)
    {
        if (binning == 1)
        {
            return _images[(int)(frameIndex % _images.Count)];
        }

        if (!_binned.TryGetValue(binning, out var list))
        {
            list = _images.Select(i => i.Bin2x2()).ToList();
            _binned[binning] = list;
        }

        return list[(int)(frameIndex % list.Count)];
    }

    // -- capture ------------------------------------------------------------

    private void CaptureLoop()
    {
        try
        {
            using var encoder = new WicJpegEncoder();
            var full = Geometry;
            var jpeg = new byte[WicJpegEncoder.MaxEncodedSize(full.Width, full.Height)];
            var scratch = new byte[full.Width * full.Height];
            long next = Stopwatch.GetTimestamp();
            long index = 0;

            while (!_stopping)
            {
                double intervalUs;
                lock (_streamLock)
                {
                    StreamGeometry geometry;
                    GrayImage source;
                    double factor;
                    lock (_gate)
                    {
                        geometry = GeometryFor(_binning);
                        source = SourceFor(_binning, index);
                        factor = BrightnessFactor(_exposureUs, _gain);
                        intervalUs = FrameIntervalUs(_fps, _exposureUs);
                    }

                    ReadOnlySpan<byte> pixels = source.Pixels;
                    if (factor != 1.0)
                    {
                        ApplyBrightness(source.Pixels, scratch, factor);
                        pixels = scratch.AsSpan(0, source.Pixels.Length);
                    }

                    int n = encoder.Encode(pixels, geometry.Width, geometry.Height, geometry.Width, _jpegQuality, jpeg);
                    if (_stopping)
                    {
                        break;
                    }

                    _sink!.OnFrame(jpeg.AsSpan(0, n), FrameKind.Jpeg, geometry);
                    Interlocked.Increment(ref _framesProduced);
                    index++;
                }

                // Pace: next frame one interval after the previous deadline (no drift), but never faster than we can encode.
                long interval = (long)(Stopwatch.Frequency * intervalUs / 1_000_000.0);
                next += interval;
                long now = Stopwatch.GetTimestamp();
                if (next < now)
                {
                    next = now;
                }

                _wake.Reset();
                int waitMs = (int)((next - now) * 1000 / Stopwatch.Frequency);
                if (waitMs > 0 && !_stopping)
                {
                    _wake.Wait(waitMs);
                    if (_wake.IsSet)
                    {
                        next = Stopwatch.GetTimestamp(); // a control changed the pace: restart the schedule
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_stopping)
            {
                _stopping = true;
                Fatal?.Invoke($"Fake capture failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Time between frames: one over the frame rate, but never shorter than the exposure. Below 1 fps the frame rate cannot
    /// drop any further, so a longer exposure stretches the frame like the real camera's long-exposure mode does
    /// (otherwise a 4 s exposure would still come out once a second).
    /// </summary>
    internal static double FrameIntervalUs(double fps, double exposureUs) => Math.Max(1_000_000.0 / fps, exposureUs);

    /// <summary>1.0 at the default exposure and gain (so default frames are the unmodified source image).</summary>
    internal static double BrightnessFactor(double exposureUs, double gain)
    {
        double exposureFactor = exposureUs / DefaultExposureUs;
        double gainFactor = (1 + (gain / 50.0)) / (1 + (DefaultGain / 50.0));
        return exposureFactor * gainFactor;
    }

    private static void ApplyBrightness(byte[] source, byte[] destination, double factor)
    {
        Span<byte> lut = stackalloc byte[256];
        for (int v = 0; v < 256; v++)
        {
            lut[v] = (byte)Math.Min(255, (int)Math.Round(v * factor));
        }

        for (int i = 0; i < source.Length; i++)
        {
            destination[i] = lut[source[i]];
        }
    }
}
