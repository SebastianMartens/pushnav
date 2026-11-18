// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Globalization;

namespace PushNav.CameraServer.Cli;

/// <summary>Parsed command line (spec 4.4). Only flags the spec lists; runtime tuning goes through protocol controls.</summary>
internal sealed record CommandLineOptions
{
    public const int DefaultPort = 8764;
    public const int DefaultJpegQuality = 90;

    /// <summary>TCP port on 127.0.0.1; 0 asks the OS for a free port (announced on stdout).</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary><c>ids</c>, <c>uvc</c> or <c>fake</c>; null = probe the registered backends in order.</summary>
    public string? Backend { get; init; }

    /// <summary>Directory of images the Fake backend replays; null = synthetic star field.</summary>
    public string? FakeImagesDirectory { get; init; }

    /// <summary>Print detected devices + capabilities as JSON and exit 0.</summary>
    public bool List { get; init; }

    public int JpegQuality { get; init; } = DefaultJpegQuality;

    public LogLevel LogLevel { get; init; } = LogLevel.Info;

    public bool ShowHelp { get; init; }
}

internal sealed record CommandLineParseResult(CommandLineOptions? Options, string? Error)
{
    public bool Success => Error == null && Options != null;
}

internal static class CommandLine
{
    public static readonly IReadOnlyList<string> BackendIds = ["ids", "uvc", "fake"];

    public const string Usage =
        """
        Usage: camera_server_dotnet [options]

          --port <n>               TCP port on 127.0.0.1 (default 8764; 0 = pick a free port)
          --backend ids|uvc|fake   force one backend (default: probe ids, then uvc)
          --fake-images <dir>      images for the fake backend (default: synthetic star field)
          --list                   print detected devices and capabilities as JSON, then exit 0
          --jpeg-quality <1-100>   JPEG quality for encoded frames (default 90)
          --log-level debug|info   stderr logging (default info)
          -h, --help               show this help

        Exit codes: 0 ok, 1 fatal (device lost, capture failure), 2 bad command line,
        3 no supported camera found.
        """;

    public static CommandLineParseResult Parse(IReadOnlyList<string> args)
    {
        var options = new CommandLineOptions();

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            string name = arg;
            string? inlineValue = null;
            int eq = arg.IndexOf('=', StringComparison.Ordinal);
            if (arg.StartsWith("--", StringComparison.Ordinal) && eq > 0)
            {
                name = arg[..eq];
                inlineValue = arg[(eq + 1)..];
            }

            switch (name)
            {
                case "-h":
                case "--help":
                    return new(options with { ShowHelp = true }, null);

                case "--list":
                    if (inlineValue != null)
                    {
                        return Fail("--list does not take a value");
                    }

                    options = options with { List = true };
                    break;

                case "--port":
                {
                    if (!TryGetValue(args, ref i, name, inlineValue, out string? v, out string? error))
                    {
                        return Fail(error!);
                    }

                    if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 0 or > 65535)
                    {
                        return Fail($"--port must be 0-65535, got '{v}'");
                    }

                    options = options with { Port = port };
                    break;
                }

                case "--backend":
                {
                    if (!TryGetValue(args, ref i, name, inlineValue, out string? v, out string? error))
                    {
                        return Fail(error!);
                    }

                    string id = v!.ToLowerInvariant();
                    if (!BackendIds.Contains(id))
                    {
                        return Fail($"--backend must be one of {string.Join(", ", BackendIds)}, got '{v}'");
                    }

                    options = options with { Backend = id };
                    break;
                }

                case "--fake-images":
                {
                    if (!TryGetValue(args, ref i, name, inlineValue, out string? v, out string? error))
                    {
                        return Fail(error!);
                    }

                    options = options with { FakeImagesDirectory = v };
                    break;
                }

                case "--jpeg-quality":
                {
                    if (!TryGetValue(args, ref i, name, inlineValue, out string? v, out string? error))
                    {
                        return Fail(error!);
                    }

                    if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int q) || q is < 1 or > 100)
                    {
                        return Fail($"--jpeg-quality must be 1-100, got '{v}'");
                    }

                    options = options with { JpegQuality = q };
                    break;
                }

                case "--log-level":
                {
                    if (!TryGetValue(args, ref i, name, inlineValue, out string? v, out string? error))
                    {
                        return Fail(error!);
                    }

                    LogLevel? level = v!.ToLowerInvariant() switch
                    {
                        "debug" => LogLevel.Debug,
                        "info" => LogLevel.Info,
                        _ => null,
                    };
                    if (level == null)
                    {
                        return Fail($"--log-level must be debug or info, got '{v}'");
                    }

                    options = options with { LogLevel = level.Value };
                    break;
                }

                default:
                    return Fail($"unknown argument '{arg}'");
            }
        }

        if (options.FakeImagesDirectory != null && options.Backend is not null and not "fake")
        {
            return Fail("--fake-images only applies to --backend fake");
        }

        return new(options, null);
    }

    private static CommandLineParseResult Fail(string error) => new(null, error);

    private static bool TryGetValue(IReadOnlyList<string> args, ref int index, string name, string? inlineValue, out string? value, out string? error)
    {
        error = null;
        if (inlineValue != null)
        {
            value = inlineValue;
            return true;
        }

        if (index + 1 >= args.Count)
        {
            value = null;
            error = $"{name} needs a value";
            return false;
        }

        value = args[++index];
        return true;
    }
}
