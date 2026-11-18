// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Backends.Ids;

namespace PushNav.CameraServer.Tests.Backends.Ids;

public class IdsTimingPolicyTests
{
    // Frame times measured on a UI-306xCP-M (min frame time per pixel clock).
    private static readonly PixelClockTiming[] Clocks =
    [
        new(30, 0.097609, 1.0, 0.0916),
        new(59, 0.048804, 1.0, 0.0526),
        new(118, 0.024402, 1.0, 0.0332),
        new(237, 0.012201, 1.0, 0.0235),
        new(474, 0.006185, 1.0, 0.0187),
    ];

    [Theory]
    [InlineData(1, 30)]
    [InlineData(10, 30)]     // 30 MHz reaches 10.24 fps
    [InlineData(10.25, 59)]  // just above: next clock
    [InlineData(20, 59)]
    [InlineData(25, 118)]
    [InlineData(40, 118)]
    [InlineData(41, 237)]
    [InlineData(100, 474)]
    public void TheLowestClockThatReachesTheFrameRateIsChosen(double fps, uint expectedMhz)
    {
        Assert.Equal(expectedMhz, IdsTimingPolicy.ChooseClock(Clocks, fps).Megahertz);
    }

    [Fact]
    public void AFrameRateNoClockReachesFallsBackToTheFastestClock()
    {
        Assert.Equal(474u, IdsTimingPolicy.ChooseClock(Clocks, 500).Megahertz);
    }

    [Fact]
    public void NoClockAtAllIsAnError()
    {
        Assert.Throws<ArgumentException>(() => IdsTimingPolicy.ChooseClock([], 10));
    }

    [Theory]
    [InlineData(10, 50_000, 10)]             // fits: preferred rate stays
    [InlineData(10, 99_000, 10)]             // 99 ms + 1 ms headroom = 100 ms = one frame
    [InlineData(10, 200_000, 4.975124)]      // longer than the frame: fps drops so the exposure fits
    [InlineData(10, 500_000, 1.996008)]
    [InlineData(10, 990_000, 1.009082)]      // 1e6 / 991 000: still above the floor
    [InlineData(10, 3_000_000, 1.0)]         // never below the floor
    [InlineData(40, 5_000, 40)]
    [InlineData(40, 30_000, 32.258065)]
    public void ExposureHasPriorityOverTheFrameRate(double preferred, double exposureUs, double expectedFps)
    {
        Assert.Equal(expectedFps, IdsTimingPolicy.TargetFps(preferred, exposureUs), precision: 5);
    }

    [Theory]
    [InlineData(100_000, 161.67, 9.9)]       // 1e6 / 101 000 = 9.90
    [InlineData(5_000, 60, 60)]              // the clock limit caps a short exposure
    [InlineData(5_000_000, 60, 1.0)]         // the floor
    public void TheFpsControlMaximumFollowsTheExposure(double exposureUs, double clockMaxFps, double expectedMax)
    {
        Assert.Equal(expectedMax, IdsTimingPolicy.MaxFpsFor(exposureUs, clockMaxFps), precision: 2);
    }

    [Theory]
    [InlineData(999.0, 999.25, false)]
    [InlineData(999.25, 999.25, false)]
    [InlineData(1000.0, 999.25, true)]
    [InlineData(1500.0, 999.25, true)]
    public void ExposureBeyondTheNormalRangeAtTheFloorNeedsLongMode(double exposureMs, double normalMax, bool expected)
    {
        Assert.Equal(expected, IdsTimingPolicy.NeedsLongExposure(exposureMs, normalMax));
    }

    [Theory]
    [InlineData(10, 50, false, 1000, 1000)]  // 2 * 100 + 50 = 250 ms: the 1 s minimum applies
    [InlineData(1, 900, false, 1000, 2900)]  // 2 * 1000 + 900
    [InlineData(1, 1200, true, 1000, 3600)]  // long mode: the frame is the exposure: 2 * 1200 + 1200
    [InlineData(10, 50, false, 20, 250)]     // tests shorten the minimum
    public void TheFrameTimeoutIsTwoIntervalsPlusTheExposure(double fps, double exposureMs, bool longMode, int minimumMs, int expectedMs)
    {
        Assert.Equal(expectedMs, IdsTimingPolicy.FrameTimeoutMilliseconds(fps, exposureMs, longMode, minimumMs));
    }

    [Fact]
    public void AMinimumLongerThanTheComputedTimeoutWins()
    {
        Assert.Equal(1000, IdsTimingPolicy.FrameTimeoutMilliseconds(40, 1, false));
    }
}
