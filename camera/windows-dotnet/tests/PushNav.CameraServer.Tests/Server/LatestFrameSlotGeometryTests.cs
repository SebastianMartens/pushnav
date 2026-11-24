// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Backends;
using PushNav.CameraServer.Server;

namespace PushNav.CameraServer.Tests.Server;

/// <summary>The slot carries the geometry of the image a frame holds.</summary>
public class LatestFrameSlotGeometryTests
{
    [Fact]
    public void A_published_frame_keeps_its_geometry()
    {
        var slot = new LatestFrameSlot();

        slot.Publish([1, 2, 3], new StreamGeometry(968, 608, 2));

        var frame = slot.TryTake();
        Assert.NotNull(frame);
        Assert.Equal(new StreamGeometry(968, 608, 2), frame.Geometry);
    }

    [Fact]
    public void A_recycled_buffer_does_not_keep_the_geometry_of_its_previous_frame()
    {
        var slot = new LatestFrameSlot();
        slot.Publish([1], new StreamGeometry(968, 608, 2));
        slot.Return(slot.TryTake()!); // safe: a frame was just published

        slot.Publish([2], default);

        Assert.Equal(default, slot.TryTake()!.Geometry); // safe: a frame was just published
    }
}
