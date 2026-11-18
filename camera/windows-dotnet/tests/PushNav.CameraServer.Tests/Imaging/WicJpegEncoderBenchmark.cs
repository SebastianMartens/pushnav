// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Diagnostics;
using PushNav.CameraServer.Imaging;
using Xunit.Abstractions;

namespace PushNav.CameraServer.Tests.Imaging;

/// <summary>
/// Task 4.3: encoder benchmark against the budget of spec 3.3 (1936x1216 GRAY8 within 15 ms, 968x608 within 5 ms).
/// The numbers are reported through the test output (run with <c>--logger "console;verbosity=detailed"</c>) and the
/// test does not fail on a missed budget unless <c>PUSHNAV_BENCH_ENFORCE=1</c> is set. Use Release for real numbers.
/// </summary>
public class WicJpegEncoderBenchmark
{
    private const int Warmup = 10;
    private const int Iterations = 200;

    private readonly ITestOutputHelper _output;

    public WicJpegEncoderBenchmark(ITestOutputHelper output)
    {
        _output = output;
    }

    private static bool Enforce => Environment.GetEnvironmentVariable("PUSHNAV_BENCH_ENFORCE") == "1";

    [Theory]
    [InlineData(1936, 1216, 15.0)]
    [InlineData(968, 608, 5.0)]
    public void EncodeTimeAgainstTheBudget(int width, int height, double budgetMs)
    {
        var pixels = TestImages.SkyLike(width, height);
        using var encoder = new WicJpegEncoder();
        var destination = new byte[WicJpegEncoder.MaxEncodedSize(width, height)];
        int size = 0;

        for (int i = 0; i < Warmup; i++)
        {
            size = encoder.Encode(pixels, width, height, width, 90, destination);
        }

        var samples = new double[Iterations];
        for (int i = 0; i < Iterations; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            size = encoder.Encode(pixels, width, height, width, 90, destination);
            samples[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }

        Array.Sort(samples);
        double median = samples[Iterations / 2];
        double p95 = samples[(int)(Iterations * 0.95)];
        double mean = samples.Average();
        _output.WriteLine(
            $"WIC JPEG q90 GRAY8 {width}x{height}: median {median:F2} ms, mean {mean:F2} ms, p95 {p95:F2} ms, " +
            $"min {samples[0]:F2} ms, max {samples[^1]:F2} ms (budget {budgetMs:F0} ms), {size / 1024.0:F0} KiB encoded, " +
            $"{1000.0 / median:F0} fps possible [{(median <= budgetMs ? "WITHIN BUDGET" : "OVER BUDGET")}]");

        Assert.InRange(size, 1000, destination.Length); // sanity only
        if (Enforce)
        {
            Assert.True(median <= budgetMs, $"median {median:F2} ms exceeds the {budgetMs:F0} ms budget (spec 3.3, Q9)");
        }
    }
}
