// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Text;
using PushNav.CameraServer.Backends;
using PushNav.CameraServer.Protocol;
using PushNav.CameraServer.Server;

namespace PushNav.CameraServer.Tests.Server;

/// <summary>
/// The order of messages and the gating of frames in a session, without sockets: a recording channel stands in for the
/// connection, so the ordering rules of protocol 11.5 can be pinned down for any backend (the fake one here).
/// </summary>
public class V2DialectTests
{
    private static readonly Logger NullLog = new(TextWriter.Null, LogLevel.Debug);
    private static readonly StreamGeometry Full = new(1936, 1216, 1);
    private static readonly StreamGeometry Binned = new(968, 608, 2);

    /// <summary>Records what a session would write, in order, as short lines; frames count only when the dialect admits them.</summary>
    private sealed class RecordingChannel : ISessionChannel
    {
        private readonly object _lock = new();

        public List<string> Events { get; } = [];

        public void Send(uint type, byte[] payload)
        {
            lock (_lock)
            {
                Events.Add(Describe(type, payload));
            }
        }

        public void SendIfAny(uint type, Func<byte[]?> build)
        {
            lock (_lock)
            {
                if (build() is { } payload)
                {
                    Events.Add(Describe(type, payload));
                }
            }
        }

        /// <summary>What the session's frame writer does with a frame: check under the lock, write if admitted.</summary>
        public void OfferFrame(IProtocolDialect dialect, StreamGeometry geometry)
        {
            lock (_lock)
            {
                if (dialect.AdmitFrame(geometry))
                {
                    Events.Add($"FRAME {geometry.Width}x{geometry.Height}");
                }
            }
        }

        private static string Describe(uint type, byte[] payload)
        {
            if (type != MessageType.StreamInfo)
            {
                return MessageType.Name(type);
            }

            var info = ProtocolJson.ParseStreamInfo(payload)!;
            return $"STREAM_INFO {info.State} g{info.Generation} {info.Width}x{info.Height} interval={info.FrameIntervalUs}";
        }
    }

    private sealed class Rig : IDisposable
    {
        public Rig(bool v2 = true)
        {
            Backend = new ScriptedBackend();
            Backend.Open(Backend.Probe()[0]);
            Dialect = v2 ? new V2Dialect(Backend, Channel, NullLog) : new V1Dialect(Backend, Channel, NullLog);
        }

        public ScriptedBackend Backend { get; }

        public RecordingChannel Channel { get; } = new();

        public IProtocolDialect Dialect { get; }

        /// <summary>The events since the handshake.</summary>
        public List<string> Since(int index) => Channel.Events.Skip(index).ToList();

        public int StartSession()
        {
            Dialect.SendInitial();
            return Channel.Events.Count;
        }

        public void Dispose() => Backend.Dispose();
    }

    [Fact]
    public void The_handshake_sends_control_info_then_stream_info_and_only_then_lets_frames_through()
    {
        using var rig = new Rig();
        rig.Channel.OfferFrame(rig.Dialect, Full); // nothing announced yet: no frame

        rig.Dialect.SendInitial();
        rig.Channel.OfferFrame(rig.Dialect, Full);
        rig.Channel.OfferFrame(rig.Dialect, Binned);

        Assert.Equal(["CONTROL_INFO", "STREAM_INFO streaming g1 1936x1216 interval=100000", "FRAME 1936x1216"], rig.Channel.Events);
    }

    [Fact]
    public void A_change_of_the_frame_interval_is_announced_after_control_info_with_the_same_generation()
    {
        using var rig = new Rig();
        int mark = rig.StartSession();

        rig.Dialect.HandleSetControl("fps", 4);

        Assert.Equal(["CONTROL_INFO", "STREAM_INFO streaming g1 1936x1216 interval=250000"], rig.Since(mark));
    }

    [Fact]
    public void A_change_that_leaves_the_interval_alone_sends_only_control_info()
    {
        using var rig = new Rig();
        int mark = rig.StartSession();

        rig.Dialect.HandleSetControl("gain", 70);

        Assert.Equal(["CONTROL_INFO"], rig.Since(mark));
    }

    [Fact]
    public void A_binning_change_runs_reconfiguring_then_control_info_then_streaming_with_the_next_generation()
    {
        using var rig = new Rig();
        int mark = rig.StartSession();

        rig.Dialect.HandleSetControl("binning", 2);
        rig.Channel.OfferFrame(rig.Dialect, Full);
        rig.Channel.OfferFrame(rig.Dialect, Binned);

        Assert.Equal(
            [
                "STREAM_INFO reconfiguring g1 1936x1216 interval=100000",
                "CONTROL_INFO",
                "STREAM_INFO streaming g2 968x608 interval=100000",
                "FRAME 968x608",
            ],
            rig.Since(mark));
    }

    [Fact]
    public void No_frame_of_either_size_gets_through_while_the_backend_restarts_the_stream()
    {
        using var rig = new Rig();
        int mark = rig.StartSession();
        rig.Backend.OnSetControl = (_, _) =>
        {
            rig.Channel.OfferFrame(rig.Dialect, Full); // the frame in flight at the old size ...
            rig.Channel.OfferFrame(rig.Dialect, Binned); // ... and the first one of the new size
            return null;
        };

        rig.Dialect.HandleSetControl("binning", 2);

        Assert.DoesNotContain(rig.Since(mark), e => e.StartsWith("FRAME", StringComparison.Ordinal));
    }

    [Fact]
    public void A_restart_the_backend_refuses_resumes_the_same_generation()
    {
        using var rig = new Rig();
        int mark = rig.StartSession();
        rig.Backend.OnSetControl = (_, _) => ControlResult.Rejected("no");

        rig.Dialect.HandleSetControl("binning", 2);
        rig.Channel.OfferFrame(rig.Dialect, Full);

        Assert.Equal(
            [
                "STREAM_INFO reconfiguring g1 1936x1216 interval=100000",
                "CONTROL_INFO",
                "STREAM_INFO streaming g1 1936x1216 interval=100000",
                "FRAME 1936x1216",
            ],
            rig.Since(mark));
    }

    [Fact]
    public void A_restart_that_fails_throws_after_reconfiguring_and_no_frame_follows()
    {
        using var rig = new Rig();
        int mark = rig.StartSession();
        rig.Backend.OnSetControl = (_, _) => throw new CameraException("image buffers could not be allocated");

        var failure = Assert.Throws<StreamRestartFailedException>(() => rig.Dialect.HandleSetControl("binning", 2));
        rig.Channel.OfferFrame(rig.Dialect, Full);
        rig.Channel.OfferFrame(rig.Dialect, Binned);

        Assert.Contains("image buffers could not be allocated", failure.Message);
        Assert.Equal(["STREAM_INFO reconfiguring g1 1936x1216 interval=100000"], rig.Since(mark));
    }

    [Fact]
    public void A_backend_decides_which_changes_restart_the_stream()
    {
        using var rig = new Rig();
        int mark = rig.StartSession();
        // say a long-exposure exit: a plain exposure change that restarts the capture at the same size
        rig.Backend.WouldRestartStreamOverride = change => change.Id == "exposure" ? true : null;

        rig.Dialect.HandleSetControl("exposure", 20_000);
        rig.Dialect.HandleSetControl("gain", 60); // everything else keeps the default

        Assert.Equal(
            [
                "STREAM_INFO reconfiguring g1 1936x1216 interval=100000",
                "CONTROL_INFO",
                "STREAM_INFO streaming g2 1936x1216 interval=100000",
                "CONTROL_INFO",
            ],
            rig.Since(mark));
    }

    [Fact]
    public void A_v2_session_allows_a_restart_only_for_the_change_it_announced()
    {
        using var rig = new Rig();
        rig.StartSession();
        rig.Backend.WouldRestartStreamOverride = change => change.Id == "exposure" ? true : null;

        rig.Dialect.HandleSetControl("exposure", 20_000); // announced: reconfiguring, restart, streaming
        rig.Dialect.HandleSetControl("gain", 60);         // not announced: the backend must not restart for it

        Assert.Equal(
            [new ControlChange("exposure", 20_000, MayRestartStream: true), new ControlChange("gain", 60, MayRestartStream: false)],
            rig.Backend.Changes);
    }

    [Fact]
    public void A_v1_session_never_allows_a_restart()
    {
        using var rig = new Rig(v2: false);
        rig.StartSession();

        rig.Dialect.HandleSetControl("exposure", 200); // v1 units: 200 x 100 us
        rig.Dialect.HandleSetControl("gain", 60);

        Assert.Equal(
            [new ControlChange("exposure", 20_000), new ControlChange("gain", 60)],
            rig.Backend.Changes);
        Assert.DoesNotContain(rig.Backend.Changes, c => c.MayRestartStream);
    }

    [Fact]
    public void The_default_restart_rule_ignores_a_binning_that_is_not_offered_or_already_current()
    {
        using var rig = new Rig();
        int mark = rig.StartSession();

        rig.Dialect.HandleSetControl("binning", 1); // the current value
        rig.Dialect.HandleSetControl("binning", 3); // not an option

        Assert.Equal(["CONTROL_INFO", "CONTROL_INFO"], rig.Since(mark));
    }

    [Fact]
    public void A_stream_config_for_the_format_in_use_repeats_the_stream_info_and_others_are_ignored()
    {
        using var rig = new Rig();
        int mark = rig.StartSession();

        rig.Dialect.HandleSetStreamConfig(Encoding.UTF8.GetBytes("{\"frame_format\":\"GRAY8\"}"));
        rig.Dialect.HandleSetStreamConfig(Encoding.UTF8.GetBytes("{\"frame_format\":\"JPEG\"}"));

        Assert.Equal(["STREAM_INFO streaming g1 1936x1216 interval=100000"], rig.Since(mark));
    }

    [Fact]
    public void A_control_the_camera_refuses_never_ends_the_session_in_v2()
    {
        using var rig = new Rig();
        int mark = rig.StartSession();
        rig.Backend.OnSetControl = (_, _) => throw new CameraException("the camera did not take it");

        rig.Dialect.HandleSetControl("gain", 70); // no exception

        Assert.Equal(["CONTROL_INFO"], rig.Since(mark));
    }

    [Fact]
    public void A_control_the_camera_refuses_never_ends_the_session_in_v1_either()
    {
        using var rig = new Rig(v2: false);
        int mark = rig.StartSession();
        rig.Backend.OnSetControl = (_, _) => throw new CameraException("the camera did not take it");

        rig.Dialect.HandleSetControl("gain", 70);

        Assert.Equal(["CONTROL_INFO"], rig.Since(mark));
        Assert.True(rig.Dialect.AdmitFrame(default)); // v1 has no STREAM_INFO and lets every frame through
    }
}
