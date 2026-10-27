using System.Globalization;
using System.Text.RegularExpressions;
using Harness.Core.Graphify;
using Harness.Infrastructure.Graphify;
using Harness.Providers.Graphify;
using Harness.Providers.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.Graphify;

public static partial class GraphifyApplication
{
    public static async Task<int> RunAsync(
        string[] args,
        Stream input,
        Stream output,
        TextWriter error,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var (options, clean, help) = Parse(args);
            if (help)
            {
                await error.WriteLineAsync(Usage);
                return 0;
            }
            await using var sessions = new GraphifySessionManager(options);
            await using var provider = new GraphifyProvider(options, sessions);
            if (clean)
            {
                var count = await sessions.CleanAsync(cancellationToken).ConfigureAwait(false);
                await using var writer = new StreamWriter(output, leaveOpen: true);
                await writer
                    .WriteLineAsync(
                        "removed " + count.ToString(CultureInfo.InvariantCulture) + " stale session record(s)"
                    )
                    .ConfigureAwait(false);
                return 0;
            }
            provider.Warm();
            await ProtocolServer
                .RunAsync(provider, "0.1.0", input, output, NullLoggerFactory.Instance, cancellationToken)
                .ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or InvalidDataException
                        or ArgumentException
                        or System.ComponentModel.Win32Exception
                        or UnauthorizedAccessException
            )
        {
            await error.WriteLineAsync(
                "harness-graphify: " + Harness.Core.Memory.CredentialRedactor.Redact(exception.Message)
            );
            return 1;
        }
    }

    public static (GraphifyOptions Options, bool Clean, bool Help) Parse(IReadOnlyList<string> args)
    {
        var options = new GraphifyOptions(Environment.CurrentDirectory, GraphifyDiscovery.Command())
        {
            StartedBy =
                "harness (harness-graphify pid " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ")",
        };
        var clean = false;
        var i = 0;
        while (i < args.Count)
        {
            var argument = args[i++];
            if (argument is "--")
            {
                return (options with { WatchArguments = args.Skip(i).ToArray() }, clean, false);
            }

            var split = argument.IndexOf('=', StringComparison.Ordinal);
            var name = split >= 0 ? argument[..split] : argument;
            string Value()
            {
                if (split >= 0)
                {
                    return argument[(split + 1)..];
                }

                if (i >= args.Count)
                {
                    throw new ArgumentException("missing value for " + name);
                }

                return args[i++];
            }
            switch (name)
            {
                case "-h" or "--help" or "-help":
                    return (options, clean, true);
                case "--root" or "-root":
                    options = options with { Root = Value() };
                    break;
                case "--graphify" or "-graphify":
                    options = options with { Command = Value() };
                    break;
                case "--idle" or "-idle":
                    options = options with { Idle = Duration(Value()) };
                    break;
                case "--stop-on-exit" or "-stop-on-exit":
                    options = options with { StopOnExit = split < 0 || Boolean(Value()) };
                    break;
                case "--clean" or "-clean":
                    clean = split < 0 || Boolean(Value());
                    break;
                default:
                    throw new ArgumentException("unknown option " + name);
            }
        }
        return (options, clean, false);
    }

    [GeneratedRegex(@"^(\d+(?:\.\d*)?|\.\d+)(ns|us|µs|μs|ms|s|m|h)", RegexOptions.NonBacktracking, 1000)]
    private static partial Regex DurationPart { get; }

    private static TimeSpan Duration(string value)
    {
        var sign = value.StartsWith('-') ? -1 : 1;
        var remaining = value.StartsWith('-') || value.StartsWith('+') ? value[1..] : value;
        if (remaining is "0")
        {
            return TimeSpan.Zero;
        }

        if (remaining.Length is 0)
        {
            throw new ArgumentException("invalid idle duration: " + value);
        }

        decimal nanoseconds = 0;
        while (remaining.Length > 0)
        {
            var match = DurationPart.Match(remaining);
            if (
                !match.Success
                || !decimal.TryParse(
                    match.Groups[1].Value,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var amount
                )
            )
            {
                throw new ArgumentException("invalid idle duration: " + value);
            }

            var unit = match.Groups[2].Value switch
            {
                "h" => 3600000000000m,
                "m" => 60000000000m,
                "s" => 1000000000m,
                "ms" => 1000000m,
                "ns" => 1m,
                _ => 1000m,
            };
            if (amount > long.MaxValue / unit)
            {
                throw new ArgumentException("idle duration is too large");
            }

            nanoseconds += amount * unit;
            if (nanoseconds > long.MaxValue)
            {
                throw new ArgumentException("idle duration is too large");
            }

            remaining = remaining[match.Length..];
        }
        return TimeSpan.FromTicks((long)(sign * nanoseconds / 100));
    }

    private static bool Boolean(string value) =>
        value switch
        {
            "1" or "t" or "T" or "true" or "TRUE" or "True" => true,
            "0" or "f" or "F" or "false" or "FALSE" or "False" => false,
            _ => throw new ArgumentException("invalid boolean: " + value),
        };

    private const string Usage =
        "Usage: harness-graphify [--root DIR] [--graphify PATH] [--clean] [--stop-on-exit] [--idle 2h] [-- watch args...]\n\nServes graphify-csharp over the harness provider protocol on stdin/stdout.\nArguments after -- are passed to 'graphify-csharp watch' (e.g. --input App.slnx).";
}
