// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Backends.Ids;
using PushNav.CameraServer.Controls;

namespace PushNav.CameraServer.Tests.Backends.Ids;

public class IdsBinningTests
{
    [Fact]
    public void TheModeBitsAreTheOnesTheUEyeInterfaceDefines()
    {
        // Read from the installed uEye .NET interface (BinningMode): vertical 2x = 1, horizontal 2x = 2, vertical 4x = 4, ...
        Assert.Equal(0x3, IdsBinning.ModeFor(2));
        Assert.Equal(0x30, IdsBinning.ModeFor(3));
        Assert.Equal(0xC, IdsBinning.ModeFor(4));
        Assert.Equal(0xC0, IdsBinning.ModeFor(5));
        Assert.Equal(0x300, IdsBinning.ModeFor(6));
        Assert.Equal(0xC00, IdsBinning.ModeFor(8));
        Assert.Equal(0x3000, IdsBinning.ModeFor(16));
        Assert.Equal(UEyeConst.BinningDisable, IdsBinning.ModeFor(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdsBinning.ModeFor(7));
    }

    [Theory]
    [InlineData(0x0, new int[0])]
    [InlineData(0x3, new[] { 2 })]
    [InlineData(0x1, new int[0])]                 // vertical only: not an NxN mode
    [InlineData(0x2, new int[0])]                 // horizontal only
    [InlineData(0xF, new[] { 2, 4 })]
    [InlineData(0x3033, new[] { 2, 3, 16 })]
    public void AFactorIsOfferedOnlyWhenBothAxesAreSupported(int supported, int[] expected) =>
        Assert.Equal(expected, IdsBinning.FactorsIn(supported));

    [Fact]
    public void TheDescriptorListsNoBinningFirstAndIsMissingWhenThereIsNoFactor()
    {
        var descriptor = IdsBinning.Descriptor([2, 4], current: 2)!;

        Assert.Equal(ControlIds.Binning, descriptor.Id);
        Assert.Equal(ControlKind.Enum, descriptor.Kind);
        Assert.True(descriptor.RestartsStream);
        Assert.Equal(2, descriptor.Cur);
        Assert.Equal([(1d, "1x1"), (2d, "2x2"), (4d, "4x4")], descriptor.Options!.Select(o => (o.Value, o.Label)));
        Assert.Null(IdsBinning.Descriptor([], current: 1));
    }
}
