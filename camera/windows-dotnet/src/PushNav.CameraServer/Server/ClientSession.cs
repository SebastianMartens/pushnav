// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Net.Sockets;
using System.Text;
using PushNav.CameraServer.Backends;
using PushNav.CameraServer.Controls;
using PushNav.CameraServer.Protocol;

namespace PushNav.CameraServer.Server;

/// <summary>How a client session ended.</summary>
internal enum SessionOutcome
{
    /// <summary>The client went away (also during the handshake): normal end, exit code 0.</summary>
    Disconnected,

    /// <summary>The client broke the protocol (bad HELLO, version mismatch, oversize message): exit code 1.</summary>
    ProtocolViolation,

    /// <summary>The backend could not restart the stream for a v2 reconfiguration: the client got an ERROR, exit code 1.</summary>
    BackendFailure,

    /// <summary>The server stopped the session (Ctrl+C, fatal backend error).</summary>
    Stopped,
}

/// <summary>
/// One client connection: HELLO exchange (which picks protocol v1 or v2, see <see cref="IProtocolDialect"/>), CONTROL_INFO,
/// then FRAMEs from the <see cref="LatestFrameSlot"/> while SET_CONTROL / GET_CONTROLS are answered on the reading thread.
/// Unknown message types after the handshake are ignored. All socket writes share one lock.
/// </summary>
internal sealed class ClientSession : ISessionChannel
{
    /// <summary>How long a client may take to answer HELLO before the session is dropped.</summary>
    public static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly Socket _socket;
    private readonly ICameraBackend _backend;
    private readonly LatestFrameSlot _slot;
    private readonly Logger _log;
    private readonly TimeSpan _handshakeTimeout;
    private readonly object _writeLock = new();
    private NetworkStream? _stream;
    private IProtocolDialect? _dialect; // chosen by the handshake
    private bool _ended; // guarded by _writeLock (written after the final ERROR, also at session end)

    public ClientSession(Socket socket, ICameraBackend backend, LatestFrameSlot slot, Logger log, TimeSpan? handshakeTimeout = null)
    {
        _socket = socket;
        _backend = backend;
        _slot = slot;
        _log = log;
        _handshakeTimeout = handshakeTimeout ?? DefaultHandshakeTimeout;
    }

    /// <summary>Runs the session to its end; blocks. <paramref name="stop"/> aborts it (the connection is closed).</summary>
    public SessionOutcome Run(CancellationToken stop)
    {
        _socket.NoDelay = true;
        using var stream = new NetworkStream(_socket, ownsSocket: false);
        _stream = stream;
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var stopRegistration = sessionCts.Token.Register(static state => CloseQuietly((Socket)state!), _socket);
        Thread? writer = null;
        try
        {
            var handshake = Handshake(stream);
            if (handshake != null)
            {
                return handshake.Value;
            }

            try
            {
                Dialect.SendInitial();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                _log.Info($"Client went away right after the handshake: {ex.Message}");
                return SessionOutcome.Disconnected;
            }

            _slot.Clear(); // never start with a frame from before the client connected

            writer = new Thread(() => WriterLoop(sessionCts.Token))
            {
                Name = "camera-writer",
                IsBackground = true,
            };
            writer.Start();

            return ReadLoop(stream, stop);
        }
        finally
        {
            lock (_writeLock)
            {
                _ended = true;
            }

            // Cancelling closes the socket (see the registration above), which also unblocks a writer stuck in a
            // send. A final ERROR has already shut down the sending side in SendFinalError, so it is not cut off.
            sessionCts.Cancel();
            CloseQuietly(_socket); // idempotent; covers sessions that ended without the token firing
            writer?.Join(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>
    /// Sends a last ERROR message and closes the sending side, so the client reads the error and then sees the end of
    /// the stream. Holds the write lock while doing so: no frame is in flight when the connection goes down, which
    /// would otherwise reset the connection and lose the error. Best effort; never throws.
    /// </summary>
    public void SendFinalError(string message)
    {
        lock (_writeLock)
        {
            if (_ended || _stream == null)
            {
                return;
            }

            _ended = true;
            try
            {
                Envelope.Write(_stream, MessageType.Error, Encoding.UTF8.GetBytes(message));
                ShutdownSendQuietly(_socket);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or InvalidOperationException)
            {
                _log.Debug($"Could not deliver ERROR to the client: {ex.Message}");
            }
        }
    }

    // -- handshake -----------------------------------------------------------

    /// <summary>Returns null when the handshake succeeded, otherwise how the session ended.</summary>
    private SessionOutcome? Handshake(NetworkStream stream)
    {
        try
        {
            Send(MessageType.Hello, ProtocolJson.Serialize(BuildHello(_backend)));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            _log.Info($"Client went away before HELLO could be sent: {ex.Message}");
            return SessionOutcome.Disconnected;
        }

        Message hello;
        try
        {
            _socket.ReceiveTimeout = (int)_handshakeTimeout.TotalMilliseconds;
            hello = Envelope.Read(stream);
            _socket.ReceiveTimeout = 0;
        }
        catch (DisconnectException ex)
        {
            _log.Info($"Client did not complete the handshake: {ex.Message}");
            return SessionOutcome.Disconnected;
        }
        catch (ProtocolException ex)
        {
            return Violation(ex.Message);
        }

        if (hello.Type != MessageType.Hello)
        {
            return Violation($"Expected HELLO from client, got {MessageType.Name(hello.Type)}");
        }

        var clientHello = ProtocolJson.ParseClientHello(hello.Payload);
        if (clientHello == null)
        {
            return Violation("Malformed HELLO from client");
        }

        // A missing protocol_version is read as 1 (the C server never looked at the payload at all).
        int clientVersion = clientHello.ProtocolVersion ?? ProtocolInfo.Version;
        if (!ProtocolInfo.IsSupported(clientVersion))
        {
            return Violation($"Protocol version mismatch: client={clientVersion}, server={ProtocolInfo.MinVersion}..{ProtocolInfo.MaxVersion}");
        }

        _dialect = clientVersion == 1 ? new V1Dialect(_backend, this, _log) : new V2Dialect(_backend, this, _log);
        _log.Info($"Client HELLO ok (app={clientHello.App ?? "?"} {clientHello.AppVersion ?? string.Empty}, protocol {clientVersion})");
        return null;
    }

    private SessionOutcome Violation(string message)
    {
        _log.Error(message);
        SendFinalError(message);
        return SessionOutcome.ProtocolViolation;
    }

    internal static HelloMessage BuildHello(ICameraBackend backend)
    {
        var geometry = backend.Geometry;
        return new HelloMessage
        {
            Backend = backend.HelloName,
            BackendVersion = BackendVersion,
            MaxProtocolVersion = ProtocolInfo.MaxVersion,
            CameraModel = backend.Device.Model,
            // Extras for every session (spec 11.2 / task 7.0): a v1 client ignores unknown HELLO keys, and the
            // engine keys its per-camera profile on camera_key from the first connect on.
            CameraKey = backend.Device.CameraKey,
            CameraSerial = backend.Device.Serial,
            StreamFormat = "MJPEG",
            DefaultWidth = geometry.Width,
            DefaultHeight = geometry.Height,
            DefaultFps = backend.DefaultFps,
            Capabilities = new HelloCapabilities
            {
                FrameFormats = [FrameFormats.Jpeg], // GRAY8 / FRAME_RAW is optional (task 7.3) and not offered
                StreamInfo = true,
                Sensor = SensorOf(backend.Sensor),
            },
        };
    }

    private static HelloSensor? SensorOf(SensorInfo? sensor) =>
        sensor == null
            ? null
            : new HelloSensor { Width = sensor.Width, Height = sensor.Height, PixelSizeUm = sensor.PixelSizeUm, Mono = sensor.Mono };

    /// <summary>Assembly version as <c>major.minor.build</c> (HELLO <c>backend_version</c>).</summary>
    internal static string BackendVersion { get; } = ReadBackendVersion();

    private static string ReadBackendVersion()
    {
        var v = typeof(ClientSession).Assembly.GetName().Version;
        return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
    }

    // -- steady state ----------------------------------------------------------

    private SessionOutcome ReadLoop(NetworkStream stream, CancellationToken stop)
    {
        while (true)
        {
            Message message;
            try
            {
                message = Envelope.Read(stream);
            }
            catch (DisconnectException ex)
            {
                if (stop.IsCancellationRequested)
                {
                    return SessionOutcome.Stopped;
                }

                _log.Info($"Client disconnected: {ex.Message}");
                return SessionOutcome.Disconnected;
            }
            catch (ProtocolException ex)
            {
                return Violation(ex.Message);
            }

            try
            {
                Dispatch(message);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                if (stop.IsCancellationRequested)
                {
                    return SessionOutcome.Stopped;
                }

                _log.Info($"Client disconnected while sending: {ex.Message}");
                return SessionOutcome.Disconnected;
            }
            catch (CameraException ex)
            {
                // The stream could not be restarted: nobody knows what state the camera is in (protocol 11.5: ERROR, exit 1).
                _log.Error(ex.Message);
                SendFinalError(ex.Message);
                return SessionOutcome.BackendFailure;
            }
        }
    }

    private void Dispatch(Message message)
    {
        switch (message.Type)
        {
            case MessageType.SetControl:
                HandleSetControl(message.Payload);
                break;

            case MessageType.GetControls:
                Dialect.SendControlInfo();
                break;

            case MessageType.SetStreamConfig:
                Dialect.HandleSetStreamConfig(message.Payload);
                break;

            case MessageType.Hello:
                _log.Debug("Ignoring repeated HELLO");
                break;

            default:
                // Forward compatibility: unknown types are skipped, never a reason to disconnect (spec 3).
                _log.Debug($"Ignoring message type {MessageType.Name(message.Type)}");
                break;
        }
    }

    private void HandleSetControl(byte[] payload)
    {
        var command = ProtocolJson.ParseSetControl(payload);
        if (command?.Id == null || command.Value == null)
        {
            _log.Debug("Ignoring SET_CONTROL without a usable id/value");
            return; // like the C server: an unparseable command gets no reply
        }

        Dialect.HandleSetControl(command.Id, command.Value.Value);
    }

    private void WriterLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var frame = _slot.Take(token);
                if (frame == null)
                {
                    return;
                }

                try
                {
                    Envelope.WriteHeader(frame.Data, MessageType.Frame, frame.PayloadLength);
                    lock (_writeLock)
                    {
                        if (_ended)
                        {
                            return; // a final ERROR went out; nothing may follow it
                        }

                        // A v2 stream only carries frames that match what STREAM_INFO announced last; others are dropped.
                        if (Dialect.AdmitFrame(frame.Geometry))
                        {
                            _stream!.Write(frame.Data, 0, frame.WireLength);
                        }
                    }
                }
                finally
                {
                    _slot.Return(frame);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            if (!token.IsCancellationRequested)
            {
                _log.Info($"Frame send failed, closing the session: {ex.Message}");
            }

            CloseQuietly(_socket); // wakes the reader, which ends the session
        }
    }

    object ISessionChannel.WriteLock => _writeLock;

    private IProtocolDialect Dialect => _dialect ?? throw new InvalidOperationException("Handshake not finished");

    void ISessionChannel.Send(uint type, byte[] payload) => Send(type, payload);

    private void Send(uint type, byte[] payload)
    {
        lock (_writeLock)
        {
            if (_ended)
            {
                return; // the session is over (final ERROR sent or closing)
            }

            Envelope.Write(_stream ?? throw new InvalidOperationException("Session not started"), type, payload);
        }
    }

    private static void ShutdownSendQuietly(Socket socket)
    {
        try
        {
            socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // already closed
        }
    }

    private static void CloseQuietly(Socket socket)
    {
        try
        {
            socket.Close();
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // already closed
        }
    }
}
