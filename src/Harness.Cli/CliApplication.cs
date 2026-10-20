using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Harness.Cli;

public static class CliApplication
{
    public static Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default) =>
        RunConfiguredAsync(args, stdout, stderr, null, null, cancellationToken);

    internal static async Task<int> RunConfiguredAsync(string[] args, TextWriter stdout, TextWriter stderr,
        Action<HostApplicationBuilder>? configureHost, Action<IConfigurator>? configureCommands, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Logging.ClearProviders();
        using var loggerProvider = new WriterLoggerProvider(TextWriter.Synchronized(stderr));
        builder.Logging.AddProvider(loggerProvider);
        builder.Services.AddSingleton(BuildInformation.Current());
        builder.Services.AddSingleton(stdout);
        configureHost?.Invoke(builder);
        await using var registrar = new HostTypeRegistrar(builder);
        var app = new CommandApp(registrar);
        app.Configure(configuration =>
        {
            configuration.SetApplicationName("harness");
            configuration.UseStrictParsing();
            configuration.PropagateExceptions();
            configuration.Settings.Console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(stdout),
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Interactive = InteractionSupport.No,
            });
            configuration.AddCommand<VersionCommand>("version").WithDescription("Print version and build information");
            configureCommands?.Invoke(configuration);
        });

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await app.RunAsync(Normalize(args), cancellationToken);
            return result < 0 ? 2 : result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (CommandParseException exception)
        {
            await stderr.WriteLineAsync($"harness: {exception.Message}");
            return 2;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or CommandRuntimeException)
        {
            await stderr.WriteLineAsync($"harness: {exception.Message}");
            return 1;
        }
    }

    private static string[] Normalize(string[] args)
    {
        if (args.Length == 0)
        {
            return ["--help"];
        }

        var normalized = args.Select(value => value == "-help" ? "--help" : value).ToArray();
        return normalized[0] switch
        {
            "-v" or "--version" => ["version", .. normalized[1..]],
            "help" => [.. normalized[1..], "--help"],
            _ => normalized,
        };
    }
}
