// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Server;

namespace PushNav.CameraServer.Tests.Server;

/// <summary>Runs a <see cref="CameraTcpServer"/> over a <see cref="StubBackend"/> on a free loopback port.</summary>
internal sealed class ServerHarness : IDisposable
{
    private readonly CancellationTokenSource _stop = new();

    private ServerHarness(StubBackend backend, TimeSpan? handshakeTimeout)
    {
        Backend = backend;
        Log = new Logger(TextWriter.Null, LogLevel.Debug);
        Slot = new LatestFrameSlot();
        Server = new CameraTcpServer(backend, Slot, Log, port: 0, handshakeTimeout: handshakeTimeout);
        Server.Start();
        backend.Fatal += Server.ReportFatal;
        backend.Start(new LatestFrameSink(Slot));
        Exit = Task.Run(() => Server.Run(_stop.Token));
    }

    public StubBackend Backend { get; }

    public Logger Log { get; }

    public LatestFrameSlot Slot { get; }

    public CameraTcpServer Server { get; }

    /// <summary>Completes with the process exit code the server would return.</summary>
    public Task<int> Exit { get; }

    public static ServerHarness Start(StubBackend? backend = null, TimeSpan? handshakeTimeout = null) =>
        new(backend ?? new StubBackend(), handshakeTimeout);

    public LoopbackClient Connect(int receiveTimeoutMs = 10_000) => LoopbackClient.Connect(Server.Port, receiveTimeoutMs);

    public int WaitForExit(int timeoutSeconds = 10)
    {
        Assert.True(Exit.Wait(TimeSpan.FromSeconds(timeoutSeconds)), "server did not exit");
        return Exit.Result;
    }

    public void StopServer() => _stop.Cancel();

    public void Dispose()
    {
        _stop.Cancel();
        Exit.Wait(TimeSpan.FromSeconds(5));
        Backend.Dispose();
        Server.Dispose();
        _stop.Dispose();
    }
}
