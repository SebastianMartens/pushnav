// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Controls;

namespace PushNav.CameraServer.Tests.Controls;

public class ControlDescriptorTests
{
    private static ControlDescriptor Int(double min, double max, double step, double cur = 0) =>
        new() { Id = "x", Label = "X", Kind = ControlKind.Int, Min = min, Max = max, Step = step, Cur = cur };

    private static ControlDescriptor Float(double min, double max, double step, double cur = 0) =>
        new() { Id = "x", Label = "X", Kind = ControlKind.Float, Min = min, Max = max, Step = step, Cur = cur };

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(250, 100)]
    public void IntClampsToTheRange(double requested, double expected)
    {
        Assert.True(Int(0, 100, 1).TryCoerce(requested, out double result));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(92, 92)]
    [InlineData(100, 92)]   // 8 us over min is a tenth of a step: stays on min
    [InlineData(130, 92)]   // just below half a step (38/78)
    [InlineData(131, 170)]  // exactly half a step (39/78) rounds up, away from zero
    [InlineData(170, 170)]
    [InlineData(2_000_000, 999_974)] // clamped to max, then rounded to the nearest step (92 + 12819 * 78)
    public void StepRoundsToTheNearestStepCountedFromMin(double requested, double expected)
    {
        var c = Int(92, 1_000_000, 78);

        Assert.True(c.TryCoerce(requested, out double result));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void RoundingPastTheLastWholeStepFallsBackToTheStepBelow()
    {
        var c = Int(0, 10, 4); // steps: 0, 4, 8 (12 would exceed the max)

        Assert.True(c.TryCoerce(10, out double result));
        Assert.Equal(8, result);
    }

    [Fact]
    public void IntControlsEndUpWhole()
    {
        Assert.True(Int(0, 100, 1).TryCoerce(41.6, out double result));
        Assert.Equal(42, result);
    }

    [Theory]
    [InlineData(0.5, 1.0)]
    [InlineData(4.004, 4.0)]
    [InlineData(4.006, 4.01)]
    [InlineData(20, 10.25)]
    public void FloatKeepsFractionsAndRoundsToStep(double requested, double expected)
    {
        var c = Float(1.0, 10.25, 0.01);

        Assert.True(c.TryCoerce(requested, out double result));
        Assert.Equal(expected, result, 9);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteValuesAreRejected(double requested)
    {
        Assert.False(Int(0, 10, 1, cur: 3).TryCoerce(requested, out double result));
        Assert.Equal(3, result); // unchanged
    }

    private static ControlDescriptor Binning(double cur = 1) => new()
    {
        Id = "binning",
        Label = "Binning",
        Kind = ControlKind.Enum,
        Cur = cur,
        Options = [new ControlOption(1, "1x1"), new ControlOption(2, "2x2")],
        RestartsStream = true,
    };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void EnumAcceptsOnlyListedValues(double requested)
    {
        Assert.True(Binning().TryCoerce(requested, out double result));
        Assert.Equal(requested, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(1.5)]
    public void EnumRejectsOtherValuesWithoutChange(double requested)
    {
        Assert.False(Binning(cur: 2).TryCoerce(requested, out double result));
        Assert.Equal(2, result);
    }

    [Fact]
    public void ControlMapperWritesTheDescriptorFields()
    {
        var json = ControlMapper.ToJson(Binning(2));

        Assert.Equal("enum", json.Type);
        Assert.Null(json.Min);
        Assert.Equal(2, json.Cur);
        Assert.True(json.RestartsStream);
        Assert.Equal([1d, 2d], json.Options!.Select(o => o.Value));
        Assert.Equal(["1x1", "2x2"], json.Options!.Select(o => o.Label));
    }
}
