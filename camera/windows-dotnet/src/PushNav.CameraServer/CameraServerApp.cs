// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Backends;
using PushNav.CameraServer.Cli;
using PushNav.CameraServer.Server;

namespace PushNav.CameraServer;

/// <summary>
/// The server's main flow (spec 4.3 / 4.5): parse arguments, pick a backend and device, open it, serve one client,
/// return the exit code. <see cref="Program"/> is a thin wrapper so tests can drive this with their own writers,
/// backends and clock.
/// </summary>
internal static class CameraServerApp
{
    /// <summary>Test seams; production uses the defaults.</summary>
    internal sealed class Seams
    {
        public Func<CommandLineOptions, Logger, IReadOnlyList<ICameraBackend>> BackendFactory { get; init; } = BackendRegistry.Create;

        public Action<TimeSpan> Delay { get; init; } = static d => Thread.Sleep(d);

        /// <summary>Pause between bind attempts when the port is taken (default 500 ms, like the C server).</summary>
        public TimeSpan? BindRetryDelay { get; init; }

        /// <summary>Raised once the server listens (port is known); tests use it to connect.</summary>
        public Action<int>? OnListening { get; init; }
    }

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken stop, Seams? seams = null)
    {
        seams ??= new Seams();

        var parsed = CommandLine.Parse(args);
        if (!parsed.Success)
        {
            stderr.WriteLine($"camera_server_dotnet: {parsed.Error}");
            stderr.WriteLine();
            stderr.WriteLine(CommandLine.Usage);
            return ExitCodes.Usage;
        }

        var options = parsed.Options!;
        if (options.ShowHelp)
        {
            stdout.WriteLine(CommandLine.Usage);
            return ExitCodes.Ok;
        }

        var log = new Logger(stderr, options.LogLevel);
        var backends = seams.BackendFactory(options, log);
        try
        {
            return options.List
                ? List(options, backends, stdout, log)
                : Serve(options, backends, stdout, log, stop, seams);
        }
        finally
        {
            foreach (var backend in backends)
            {
                backend.Dispose();
            }

            // Queued log lines (a fatal error, "no camera found") get a bounded chance to reach stderr before exit.
            log.Flush(TimeSpan.FromSeconds(1));
        }
    }

    private static int List(CommandLineOptions options, IReadOnlyList<ICameraBackend> backends, TextWriter stdout, Logger log)
    {
        if (options.Backend != null && !backends.Any(b => b.Id == options.Backend))
        {
            log.Warn($"Backend '{options.Backend}' is not available in this build");
        }

        var output = DeviceLister.Collect(backends, log);
        stdout.WriteLine(DeviceLister.ToJson(output));
        stdout.Flush();
        return ExitCodes.Ok;
    }

    private static int Serve(
        CommandLineOptions options,
        IReadOnlyList<ICameraBackend> backends,
        TextWriter stdout,
        Logger log,
        CancellationToken stop,
        Seams seams)
    {
        if (options.Backend != null && !backends.Any(b => b.Id == options.Backend))
        {
            log.Error($"Backend '{options.Backend}' is not available in this build.");
            return ExitCodes.NoSupportedCamera;
        }

        var selected = BackendSelector.Select(
            backends, BackendSelector.DefaultWait, BackendSelector.DefaultStep, seams.Delay, log, stop);
        if (selected == null)
        {
            if (stop.IsCancellationRequested)
            {
                return ExitCodes.Ok;
            }

            string probed = backends.Count == 0 ? "none registered" : string.Join(", ", backends.Select(b => b.Id));
            log.Error(
                "No supported camera found (backends probed: " + probed + "). Check the USB connection and that the " +
                "camera is recognized by Windows. For IDS cameras the IDS Software Suite must be installed.");
            return ExitCodes.NoSupportedCamera;
        }

        var backend = selected.Backend;
        log.Info($"Using backend '{backend.Id}', device {selected.Device.Model} ({selected.Device.CameraKey})");

        try
        {
            backend.Open(selected.Device);
        }
        catch (CameraException ex)
        {
            log.Error($"Failed to open camera: {ex.Message}");
            return ExitCodes.Fatal;
        }

        var slot = new LatestFrameSlot();
        using var server = new CameraTcpServer(backend, slot, log, options.Port, bindRetryDelay: seams.BindRetryDelay);
        try
        {
            server.Start(stop);
        }
        catch (IOException ex)
        {
            log.Error(ex.Message);
            return ExitCodes.Fatal;
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Ok;
        }

        backend.Fatal += server.ReportFatal;
        try
        {
            backend.Start(new LatestFrameSink(slot));
            stdout.WriteLine($"Listening on 127.0.0.1:{server.Port}");
            stdout.Flush();
            seams.OnListening?.Invoke(server.Port);

            int code = server.Run(stop);
            log.Info(code == ExitCodes.Ok ? "Camera server exiting" : $"Camera server exiting with code {code}");
            return code;
        }
        finally
        {
            backend.Fatal -= server.ReportFatal;
            slot.Complete();
            backend.Stop();
        }
    }
}
