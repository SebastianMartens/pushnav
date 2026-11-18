// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Net;
using System.Net.Sockets;
using PushNav.CameraServer.Backends;

namespace PushNav.CameraServer.Server;

/// <summary>
/// Single-client TCP server on 127.0.0.1 (spec 4.5): accepts one client, runs its <see cref="ClientSession"/>,
/// and returns when that session ends. The engine respawns the process, exactly as with the C and Swift servers.
/// </summary>
internal sealed class CameraTcpServer : IDisposable
{
    private const int BindAttempts = 10;
    public static readonly TimeSpan DefaultBindRetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly ICameraBackend _backend;
    private readonly LatestFrameSlot _slot;
    private readonly Logger _log;
    private readonly int _requestedPort;
    private readonly TimeSpan? _handshakeTimeout;
    private readonly TimeSpan _bindRetryDelay;
    private readonly CancellationTokenSource _fatalCts = new();
    private TcpListener? _listener;
    private volatile ClientSession? _session;
    private string? _fatalMessage;

    public CameraTcpServer(ICameraBackend backend, LatestFrameSlot slot, Logger log, int port, TimeSpan? handshakeTimeout = null, TimeSpan? bindRetryDelay = null)
    {
        _backend = backend;
        _slot = slot;
        _log = log;
        _requestedPort = port;
        _handshakeTimeout = handshakeTimeout;
        _bindRetryDelay = bindRetryDelay ?? DefaultBindRetryDelay;
    }

    /// <summary>The bound port (differs from the requested one when 0 was requested). Valid after <see cref="Start"/>.</summary>
    public int Port => ((IPEndPoint)(_listener ?? throw new InvalidOperationException("Not started")).LocalEndpoint).Port;

    /// <summary>Binds and listens. The port may linger from a previous run, so binding is retried like the C server does.</summary>
    public void Start(CancellationToken cancellation = default)
    {
        for (int attempt = 1; ; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, _requestedPort);
            try
            {
                listener.Start(1);
                _listener = listener;
                return;
            }
            catch (SocketException ex)
            {
                listener.Stop();
                if (attempt >= BindAttempts || _requestedPort == 0)
                {
                    throw new IOException($"Cannot listen on 127.0.0.1:{_requestedPort}: {ex.Message}", ex);
                }

                _log.Warn($"bind() failed ({ex.SocketErrorCode}), retrying in {_bindRetryDelay.TotalMilliseconds:0} ms ({attempt}/{BindAttempts})");
                if (cancellation.WaitHandle.WaitOne(_bindRetryDelay))
                {
                    throw new OperationCanceledException(cancellation);
                }
            }
        }
    }

    /// <summary>
    /// Accepts one client and serves it. Returns the process exit code: 0 when the client left (or
    /// <paramref name="stop"/> fired), 1 after a fatal backend error or a protocol violation.
    /// </summary>
    public int Run(CancellationToken stop)
    {
        var listener = _listener ?? throw new InvalidOperationException("Not started");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, _fatalCts.Token);

        Socket client;
        try
        {
            client = listener.AcceptSocketAsync(linked.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return ExitCodeAfterStop();
        }

        using (client)
        {
            _log.Info($"Client connected from {client.RemoteEndPoint}");
            var session = new ClientSession(client, _backend, _slot, _log, _handshakeTimeout);
            _session = session;
            try
            {
                var outcome = session.Run(linked.Token);
                return outcome switch
                {
                    SessionOutcome.ProtocolViolation => ExitCodes.Fatal,
                    SessionOutcome.Stopped => ExitCodeAfterStop(),
                    _ => FatalMessage != null ? ExitCodes.Fatal : ExitCodes.Ok,
                };
            }
            finally
            {
                _session = null;
            }
        }
    }

    /// <summary>
    /// Reports an unrecoverable backend error (device lost, capture failure): tells the connected client
    /// (best effort) and makes <see cref="Run"/> return exit code 1. Safe to call from any thread, repeatedly.
    /// </summary>
    public void ReportFatal(string message)
    {
        if (Interlocked.CompareExchange(ref _fatalMessage, message, null) != null)
        {
            return;
        }

        _log.Error($"Fatal: {message}");
        _session?.SendFinalError(message);
        _fatalCts.Cancel();
    }

    public void Dispose()
    {
        _fatalCts.Cancel();
        _listener?.Stop();
        _fatalCts.Dispose();
    }

    private string? FatalMessage => Volatile.Read(ref _fatalMessage);

    private int ExitCodeAfterStop() => FatalMessage != null ? ExitCodes.Fatal : ExitCodes.Ok;
}
