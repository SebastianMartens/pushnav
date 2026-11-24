// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Backends;
using PushNav.CameraServer.Backends.Fake;
using PushNav.CameraServer.Controls;
using PushNav.CameraServer.Imaging;

namespace PushNav.CameraServer.Tests.Backends;

/// <summary>Task 3.1: the fake backend replays images or a synthetic star field and simulates controls.</summary>
public class FakeBackendTests
{
    private sealed class CollectingSink : IFrameSink
    {
        private readonly object _gate = new();
        private readonly List<(byte[] Data, FrameKind Kind, StreamGeometry Geometry)> _frames = [];

        public void OnFrame(ReadOnlySpan<byte> data, FrameKind kind, StreamGeometry geometry)
        {
            lock (_gate)
            {
                _frames.Add((data.ToArray(), kind, geometry));
            }
        }

        public List<(byte[] Data, FrameKind Kind, StreamGeometry Geometry)> Snapshot()
        {
            lock (_gate)
            {
                return [.. _frames];
            }
        }

        public bool WaitForFrames(int count, int timeoutMs = 10_000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (Snapshot().Count >= count)
                {
                    return true;
                }

                Thread.Sleep(5);
            }

            return false;
        }
    }

    private static readonly Logger NullLog = new(TextWriter.Null, LogLevel.Info);

    private static string SamplesDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "tests", "samples");
            if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "*.png").Length > 0)
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("tests/samples (repo sample images) not found above " + AppContext.BaseDirectory);
    }

    private static FakeBackend OpenedSynthetic()
    {
        var backend = new FakeBackend(null, 90, NullLog);
        backend.Open(backend.Probe().Single());
        return backend;
    }

    private static ControlDescriptor Control(FakeBackend b, string id) => b.Controls.Single(c => c.Id == id);

    [Fact]
    public void ProbeFindsOneFakeDeviceWithAStableKey()
    {
        using var backend = new FakeBackend(null, 90, NullLog);

        var device = Assert.Single(backend.Probe());

        Assert.Equal("fake", device.BackendId);
        Assert.Equal("fake:FakeCamera", device.CameraKey);
        Assert.Equal("windows-dotnet-fake", backend.HelloName);
    }

    [Fact]
    public void SyntheticStarFieldHasTheReferenceSensorSize()
    {
        using var backend = OpenedSynthetic();

        Assert.Equal(new StreamGeometry(1936, 1216, 1), backend.Geometry);
    }

    [Fact]
    public void ControlsHaveIdsLikeRangesInRealUnits()
    {
        using var backend = OpenedSynthetic();

        var exposure = Control(backend, "exposure");
        Assert.Equal((ControlKind.Int, "us", 92d, 4_000_000d, 50_000d), (exposure.Kind, exposure.Unit, exposure.Min, exposure.Max, exposure.Cur));
        var gain = Control(backend, "gain");
        Assert.Equal((0d, 100d, 20d), (gain.Min, gain.Max, gain.Cur));
        var fps = Control(backend, "fps");
        Assert.Equal(ControlKind.Float, fps.Kind);
        Assert.Equal(1.0, fps.Min);
        Assert.Equal(10.0, fps.Cur);
        var binning = Control(backend, "binning");
        Assert.Equal(ControlKind.Enum, binning.Kind);
        Assert.Equal([1d, 2d], binning.Options!.Select(o => o.Value));
        Assert.True(binning.RestartsStream);
    }

    [Fact]
    public void StreamsValidGrayscaleJpegFramesAtTheSensorSize()
    {
        using var backend = OpenedSynthetic();
        var sink = new CollectingSink();

        backend.Start(sink);
        Assert.True(sink.WaitForFrames(3), "no frames arrived");
        backend.Stop();

        foreach (var (data, kind, geometry) in sink.Snapshot())
        {
            Assert.Equal(FrameKind.Jpeg, kind);
            Assert.Equal(new StreamGeometry(1936, 1216, 1), geometry);
            var (w, h, components) = JpegInfo.ReadFrameHeader(data);
            Assert.Equal((1936, 1216, 1), (w, h, components));
        }
    }

    [Fact]
    public void FramesArriveAtTheConfiguredFrameRate()
    {
        using var backend = OpenedSynthetic();
        backend.SetControl("fps", 20);
        backend.SetControl("exposure", 10_000); // keep fps 20 legal
        backend.SetControl("fps", 20);
        var sink = new CollectingSink();

        backend.Start(sink);
        Thread.Sleep(1500);
        backend.Stop();

        int n = sink.Snapshot().Count;
        Assert.InRange(n, 20, 40); // about 30 frames in 1.5 s at 20 fps
    }

    [Fact]
    public void ReplaysSampleImagesInOrderAndCyclesThroughThem()
    {
        string samples = SamplesDirectory();
        int imageCount = Directory.GetFiles(samples, "*.png").Length;
        using var backend = new FakeBackend(samples, 95, NullLog);
        backend.Open(backend.Probe().Single()); // default controls: 10 fps, unmodified brightness
        var sink = new CollectingSink();

        Assert.Equal(new StreamGeometry(1280, 720, 1), backend.Geometry); // the sample images, not 1936x1216
        backend.Start(sink);
        Assert.True(sink.WaitForFrames(imageCount + 2));
        backend.Stop();

        var frames = sink.Snapshot();
        Assert.All(frames, f => Assert.Equal(new StreamGeometry(1280, 720, 1), f.Geometry));
        Assert.Equal(frames[0].Data, frames[imageCount].Data); // wrapped around to the first image again
        Assert.NotEqual(frames[0].Data, frames[1].Data);       // different images differ

        // Frame content is the sample converted to gray, within JPEG error.
        using var decoder = new WicGrayDecoder();
        var first = decoder.DecodeFile(Directory.GetFiles(samples, "*.png").Order(StringComparer.OrdinalIgnoreCase).First());
        var shown = decoder.Decode(frames[0].Data);
        Assert.True(TestImages.MeanAbsError(first.Pixels, shown.Pixels) < 2.5);
    }

    [Fact]
    public void DefaultControlsDoNotAlterTheImage()
    {
        string samples = SamplesDirectory();
        using var backend = new FakeBackend(samples, 100, NullLog);
        backend.Open(backend.Probe().Single());
        var sink = new CollectingSink();

        backend.Start(sink);
        Assert.True(sink.WaitForFrames(1));
        backend.Stop();

        using var decoder = new WicGrayDecoder();
        var source = decoder.DecodeFile(Directory.GetFiles(samples, "*.png").Order(StringComparer.OrdinalIgnoreCase).First());
        var shown = decoder.Decode(sink.Snapshot()[0].Data);
        Assert.True(TestImages.MeanAbsError(source.Pixels, shown.Pixels) < 1.5, "the solver must see the unmodified sample at default exposure/gain");
    }

    [Fact]
    public void ExposureAndGainChangeTheBrightnessOfFrames()
    {
        string samples = SamplesDirectory();
        double MeanBrightness(double exposureUs, double gain)
        {
            using var backend = new FakeBackend(samples, 95, NullLog);
            backend.Open(backend.Probe().Single());
            backend.SetControl("exposure", exposureUs);
            backend.SetControl("gain", gain);
            var sink = new CollectingSink();
            backend.Start(sink);
            Assert.True(sink.WaitForFrames(1));
            backend.Stop();
            using var decoder = new WicGrayDecoder();
            var pixels = decoder.Decode(sink.Snapshot()[0].Data).Pixels;
            return pixels.Average(p => (double)p);
        }

        double dim = MeanBrightness(10_000, 0);
        double normal = MeanBrightness(50_000, 20);
        double bright = MeanBrightness(120_000, 60);

        Assert.True(dim < normal && normal < bright, $"dim={dim:F1} normal={normal:F1} bright={bright:F1}");
    }

    [Fact]
    public void SetControlClampsAndRoundsLikeTheProtocolSays()
    {
        using var backend = OpenedSynthetic();

        Assert.True(backend.SetControl("gain", 500).Accepted);
        Assert.Equal(100, Control(backend, "gain").Cur);
        Assert.True(backend.SetControl("gain", -3).Accepted);
        Assert.Equal(0, Control(backend, "gain").Cur);
        Assert.True(backend.SetControl("exposure", 1).Accepted);
        Assert.Equal(92, Control(backend, "exposure").Cur);
        Assert.True(backend.SetControl("exposure", 99_000_000).Accepted);
        Assert.Equal(4_000_000, Control(backend, "exposure").Cur);
        Assert.True(backend.SetControl("fps", 12.344).Accepted);
        Assert.Equal(1.0, Control(backend, "fps").Cur); // exposure is 4 s: fps was forced to the minimum and cannot rise
    }

    [Fact]
    public void ABinningChangeIsRefusedForACallerThatCannotAnnounceTheRestart()
    {
        using var backend = OpenedSynthetic();

        var result = backend.SetControl("binning", 2); // no MayRestartStream: what a v1 session would send

        Assert.False(result.Accepted);
        Assert.Contains("cannot announce", result.Message);
        Assert.Equal(1, Control(backend, "binning").Cur);
        Assert.Equal(new StreamGeometry(1936, 1216, 1), backend.Geometry);
        Assert.False(((ICameraBackend)backend).WouldRestartStream(new ControlChange("binning", 2)));
        Assert.True(((ICameraBackend)backend).WouldRestartStream(new ControlChange("binning", 2, MayRestartStream: true)));
    }

    [Fact]
    public void UnknownControlsAndInvalidEnumValuesAreRejectedWithoutChange()
    {
        using var backend = OpenedSynthetic();

        Assert.False(backend.SetControl("pixel_clock", 118).Accepted);
        Assert.False(backend.SetControl("binning", 3).Accepted);
        Assert.False(backend.SetControl("gain", double.NaN).Accepted);
        Assert.Equal(1, Control(backend, "binning").Cur);
        Assert.Equal(20, Control(backend, "gain").Cur);
    }

    [Fact]
    public void ExposurePriorityLowersFpsToFitTheExposure()
    {
        using var backend = OpenedSynthetic();

        backend.SetControl("exposure", 200_000); // 0.2 s: needs at most 5 fps

        Assert.Equal(5.0, Control(backend, "fps").Cur);
        Assert.Equal(5.0, Control(backend, "fps").Max);   // range moved with the coupling
        Assert.Equal(200_000, Control(backend, "exposure").Cur); // exposure kept, not clamped
    }

    [Fact]
    public void RaisingFpsIsClampedToWhatTheExposureAllows()
    {
        using var backend = OpenedSynthetic();
        backend.SetControl("exposure", 100_000); // max 10 fps

        backend.SetControl("fps", 30);

        Assert.Equal(10.0, Control(backend, "fps").Cur);
    }

    [Fact]
    public void ShortExposureReopensTheFpsRange()
    {
        using var backend = OpenedSynthetic();
        backend.SetControl("exposure", 500_000);
        Assert.Equal(2.0, Control(backend, "fps").Max);

        backend.SetControl("exposure", 1_000);

        Assert.Equal(40.98, Control(backend, "fps").Max);
        Assert.Equal(2.0, Control(backend, "fps").Cur); // fps stays where exposure priority put it, like the real camera
        Assert.True(backend.SetControl("fps", 25).Accepted);
        Assert.Equal(25.0, Control(backend, "fps").Cur);
    }

    [Theory]
    [InlineData(10, 50_000, 100_000)]       // 10 fps, short exposure: the frame rate rules
    [InlineData(1, 500_000, 1_000_000)]     // 1 fps floor, exposure fits
    [InlineData(1, 1_200_000, 1_200_000)]   // exposure beyond the 1 fps floor stretches the frame
    [InlineData(1, 4_000_000, 4_000_000)]
    [InlineData(40.98, 92, 24_402.14)]
    public void FrameIntervalFollowsTheSlowerOfFpsAndExposure(double fps, double exposureUs, double expectedUs)
    {
        Assert.Equal(expectedUs, FakeBackend.FrameIntervalUs(fps, exposureUs), 1);
    }

    [Fact]
    public void ExposureLongerThanOneSecondStretchesTheFrameIntervalInsteadOfLyingAboutIt()
    {
        using var backend = OpenedSynthetic();
        backend.SetControl("exposure", 1_200_000);
        Assert.Equal(1.0, Control(backend, "fps").Cur); // fps is at its floor and cannot go lower
        var times = new List<long>();
        var sink = new TimestampSink(times);

        backend.Start(sink);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (times.Count < 2 && sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            Thread.Sleep(20);
        }

        backend.Stop();
        Assert.True(times.Count >= 2, "no second frame");
        double gapMs = System.Diagnostics.Stopwatch.GetElapsedTime(times[0], times[1]).TotalMilliseconds;
        Assert.InRange(gapMs, 1_150, 1_900); // 1.2 s exposure, not the 1.0 s that 1 fps alone would give
    }

    private sealed class TimestampSink(List<long> times) : IFrameSink
    {
        public void OnFrame(ReadOnlySpan<byte> data, FrameKind kind, StreamGeometry geometry)
        {
            lock (times)
            {
                times.Add(System.Diagnostics.Stopwatch.GetTimestamp());
            }
        }
    }

    [Fact]
    public void BinningChangesTheGeometryAndTheFrameSize()
    {
        using var backend = OpenedSynthetic();
        backend.SetControl("exposure", 10_000);
        backend.SetControl("fps", 40);
        var sink = new CollectingSink();
        backend.Start(sink);
        Assert.True(sink.WaitForFrames(2));

        Assert.True(backend.SetControlMayRestart("binning", 2).Accepted);
        Assert.Equal(new StreamGeometry(968, 608, 2), backend.Geometry);
        int afterSwitch = sink.Snapshot().Count;
        Assert.True(sink.WaitForFrames(afterSwitch + 3));
        backend.Stop();

        // Full-size frames first, then binned ones; once binned frames start, no full-size frame follows.
        var frames = sink.Snapshot();
        int firstBinned = frames.FindIndex(f => f.Geometry.Binning == 2);
        Assert.InRange(firstBinned, 2, afterSwitch); // the first binned frame came right after SetControl returned
        Assert.All(frames.Take(firstBinned), f => Assert.Equal(new StreamGeometry(1936, 1216, 1), f.Geometry));
        Assert.All(frames.Skip(firstBinned), f =>
        {
            Assert.Equal(new StreamGeometry(968, 608, 2), f.Geometry);
            var (w, h, _) = JpegInfo.ReadFrameHeader(f.Data);
            Assert.Equal((968, 608), (w, h));
        });
    }

    [Fact]
    public void BinningBackToOneRestoresTheFullFrame()
    {
        using var backend = OpenedSynthetic();
        backend.SetControlMayRestart("binning", 2);

        backend.SetControlMayRestart("binning", 1);

        Assert.Equal(new StreamGeometry(1936, 1216, 1), backend.Geometry);
    }

    [Fact]
    public void ControlsAreReReadAfterEveryChange()
    {
        using var backend = OpenedSynthetic();
        var before = backend.Controls;

        backend.SetControl("gain", 77);

        Assert.Equal(20, before.Single(c => c.Id == "gain").Cur); // earlier snapshot is immutable
        Assert.Equal(77, Control(backend, "gain").Cur);
    }

    [Fact]
    public void ReopeningResetsTheSimulatedCamera()
    {
        using var backend = OpenedSynthetic();
        backend.SetControl("gain", 99);
        backend.SetControlMayRestart("binning", 2);

        backend.Open(backend.Probe().Single());

        Assert.Equal(20, Control(backend, "gain").Cur);
        Assert.Equal(1, Control(backend, "binning").Cur);
    }

    [Fact]
    public void SimulatedDeviceLossRaisesFatalAndStopsFrames()
    {
        using var backend = OpenedSynthetic();
        var sink = new CollectingSink();
        string? fatal = null;
        backend.Fatal += m => fatal = m;
        backend.Start(sink);
        Assert.True(sink.WaitForFrames(1));

        backend.SimulateDeviceLoss("unplugged");
        Thread.Sleep(300);
        int count = sink.Snapshot().Count;
        Thread.Sleep(400);

        Assert.Equal("unplugged", fatal);
        Assert.Equal(count, sink.Snapshot().Count);
    }

    [Fact]
    public void MissingImageDirectoryFailsToOpenWithACameraException()
    {
        using var backend = new FakeBackend(Path.Combine(Path.GetTempPath(), "pushnav-no-such-dir-" + Guid.NewGuid().ToString("N")), 90, NullLog);

        var ex = Assert.Throws<CameraException>(() => backend.Open(backend.Probe().Single()));

        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void DirectoryWithoutImagesFailsToOpen()
    {
        string dir = Directory.CreateTempSubdirectory("pushnav-empty-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "no images here");
            File.WriteAllText(Path.Combine(dir, "broken.png"), "not really a png");
            using var backend = new FakeBackend(dir, 90, NullLog);

            var ex = Assert.Throws<CameraException>(() => backend.Open(backend.Probe().Single()));

            Assert.Contains("No usable images", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ImagesOfADifferentSizeAreSkipped()
    {
        // Two copies of a 1280x720 sample, plus a binned-size (640x360) variant written through the encoder as a JPEG.
        string samples = SamplesDirectory();
        string dir = Directory.CreateTempSubdirectory("pushnav-mixed-").FullName;
        try
        {
            var first = Directory.GetFiles(samples, "*.png").Order(StringComparer.OrdinalIgnoreCase).First();
            File.Copy(first, Path.Combine(dir, "a.png"));
            using (var decoder = new WicGrayDecoder())
            using (var encoder = new WicJpegEncoder())
            {
                var small = decoder.DecodeFile(first).Bin2x2();
                File.WriteAllBytes(Path.Combine(dir, "b.jpg"), encoder.Encode(small.Pixels, small.Width, small.Height, small.Width));
            }

            using var backend = new FakeBackend(dir, 90, NullLog);
            backend.Open(backend.Probe().Single());

            Assert.Equal(new StreamGeometry(1280, 720, 1), backend.Geometry);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void StartingBeforeOpenIsAnError()
    {
        using var backend = new FakeBackend(null, 90, NullLog);

        Assert.Throws<InvalidOperationException>(() => backend.Start(new CollectingSink()));
    }
}
