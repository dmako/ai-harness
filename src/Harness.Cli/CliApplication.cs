using System.CommandLine;
using Harness.Cli.Hooks;
using Harness.Cli.Memory;
using Harness.Cli.Setup;
using Harness.Cli.Tasks;
using Harness.Cli.Telemetry;
using Harness.Cli.Ui;
using Harness.Cli.Workspaces;
using Harness.Infrastructure.Completion;
using Harness.Infrastructure.Projects;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harness.Cli;

public static class CliApplication
{
    public static Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken = default
    ) => RunConfiguredAsync(args, stdout, stderr, null, null, cancellationToken);

    internal static async Task<int> RunConfiguredAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        Action<HostApplicationBuilder>? configureHost,
        Action<RootCommand, IServiceProvider>? configureCommands,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        if (args.Length >= 2 && args[0] is "hook" or "hooks" && args[1] is "event" or "session")
        {
            using var input = Console.OpenStandardInput();
            return await HookExecution.RunAsync(args, input, stdout, stderr, cancellationToken);
        }
        if (
            args.Length is 2
            && args[0] is "completion"
            && CompletionManager.Shells.Contains(args[1], StringComparer.Ordinal)
        )
        {
            await stdout.WriteAsync(CompletionScripts.For(args[1]));
            return 0;
        }
        var completing = args.Length > 0 && args[0] is "__complete";
        if (completing && args.Length < 2)
        {
            return 2;
        }
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Logging.ClearProviders();
        using var loggerProvider = new WriterLoggerProvider(TextWriter.Synchronized(stderr));
        builder.Logging.AddProvider(loggerProvider);
        builder.Services.AddSingleton(BuildInformation.Current());
        builder.Services.AddSingleton(services => new ProjectLifecycle(
            version: services.GetRequiredService<BuildInformation>().Version
        ));
        builder.Services.AddSingleton(stdout);
        builder.Services.AddSingleton(new CliErrorOutput(stderr));
        builder.Services.AddSingleton<MemoryCommandRunner>();
        builder.Services.AddSingleton<TaskCommandRunner>();
        builder.Services.AddSingleton<WorkspaceCommandRunner>();
        builder.Services.AddSingleton<ReportCommandRunner>();
        builder.Services.AddSingleton<ExportCommandRunner>();
        builder.Services.AddSingleton<SessionCommandRunner>();
        builder.Services.AddSingleton(new SetupInvocation(args));
        builder.Services.AddSingleton<SetupTerminal>();
        builder.Services.AddSingleton<SetupCommandRunner>();
        builder.Services.AddSingleton<HookCommandRunner>();
        builder.Services.AddSingleton<UiProjectCatalog>();
        builder.Services.AddSingleton<UiSessions>();
        configureHost?.Invoke(builder);
        var host = builder.Build();
        await using var lifetime = new HostLifetime(host);
        var root = CliCommands.Create(host.Services, stdout);
        configureCommands?.Invoke(root, host.Services);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (completing)
            {
                CompletionProvider.Write(root, args[1..], stdout);
                return 0;
            }
            var parsed = root.Parse(Normalize(args));
            if (parsed.Errors.Count > 0)
            {
                foreach (var failure in parsed.Errors)
                {
                    await stderr.WriteLineAsync("harness: " + failure.Message);
                }
                return 2;
            }
            var result = await parsed.InvokeAsync(
                new InvocationConfiguration
                {
                    Output = stdout,
                    Error = stderr,
                    EnableDefaultExceptionHandler = false,
                    ProcessTerminationTimeout = null,
                },
                cancellationToken
            );
            return result < 0 ? 2 : result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (ArgumentException exception)
        {
            await stderr.WriteLineAsync($"harness: {exception.Message}");
            return 2;
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or InvalidDataException
                        or InvalidOperationException
                        or UnauthorizedAccessException
                        or SqliteException
                        or System.Text.Json.JsonException
            )
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

        var normalized = NormalizeLegacyFlags(args);
        if (
            normalized[0] is "hooks" or "hook" or "completion"
            && (normalized.Length is 1 || normalized[1].StartsWith('-') && normalized[1] is not ("--help" or "-h"))
        )
        {
            normalized = [normalized[0], normalized[0] is "completion" ? "status" : "choose", .. normalized[1..]];
        }
        if (
            normalized[0] is "workspace" or "workspaces" or "projects" or "telemetry"
            && (normalized.Length is 1 || normalized[1].StartsWith('-') && normalized[1] is not ("--help" or "-h"))
        )
        {
            normalized = [normalized[0], normalized[0] is "telemetry" ? "status" : "list", .. normalized[1..]];
        }

        if (normalized[0] is "providers")
        {
            if (normalized.Length is 1 || normalized[1].StartsWith('-') && normalized[1] is not ("--help" or "-h"))
            {
                normalized = [normalized[0], "sessions", .. normalized[1..]];
            }
            if (
                normalized.Length >= 2
                && normalized[1] is "sessions"
                && (normalized.Length is 2 || normalized[2].StartsWith('-') && normalized[2] is not ("--help" or "-h"))
            )
            {
                normalized = [normalized[0], normalized[1], "list", .. normalized[2..]];
            }
        }

        return normalized[0] switch
        {
            "-v" or "--version" => ["version", .. normalized[1..]],
            "help" => [.. normalized[1..], "--help"],
            _ => normalized,
        };
    }

    private static string[] NormalizeLegacyFlags(string[] args)
    {
        var normalized = (string[])args.Clone();
        var index = 0;
        while (index < normalized.Length)
        {
            var argument = normalized[index];
            if (argument == "--")
            {
                break;
            }

            var equals = argument.IndexOf('=', StringComparison.Ordinal);
            var name = equals < 0 ? argument : argument[..equals];
            var replacement = name switch
            {
                "-help" => "--help",
                "-endpoint" => "--endpoint",
                "-token" => "--token",
                "-auto" => "--auto",
                "-addr" => "--addr",
                "-no-open" => "--no-open",
                "-project" => "--project",
                "-dry-run" => "--dry-run",
                "-json" => "--json",
                "-syncs" => "--syncs",
                "-full" => "--full",
                "-since" => "--since",
                "-all" => "--all",
                "-days" => "--days",
                "-client" => "--client",
                "-command" => "--command",
                "-remove" => "--remove",
                "-delete-data" => "--delete-data",
                "-reason" => "--reason",
                "-to" => "--to",
                "-force" => "--force",
                "-status" => "--status",
                "-content" => "--content",
                "-note" => "--note",
                "-supersede" => "--supersede",
                "-keep-both" => "--keep-both",
                "-milestone" => "--milestone",
                "-events" => "--events",
                "-id" => "--id",
                "-prefix" => "--prefix",
                "-title" => "--title",
                "-discover" => "--discover",
                "-clear-title" => "--clear-title",
                "-reset-name" => "--reset-name",
                "-goal" => "--goal",
                "-type" => "--type",
                "-priority" => "--priority",
                "-depends" => "--depends",
                "-accept" => "--accept",
                "-scope" => "--scope",
                "-kind" => "--kind",
                "-shell" => "--shell",
                "-lease" => "--lease",
                "-summary" => "--summary",
                "-commit" => "--commit",
                "-acc" => "--acc",
                "-acc-all" => "--acc-all",
                "-check" => "--check",
                _ => name,
            };
            normalized[index] = replacement + (equals < 0 ? "" : argument[equals..]);
            if (
                equals < 0
                && replacement
                    is "--endpoint"
                        or "--token"
                        or "--addr"
                        or "--kind"
                        or "--shell"
                        or "--client"
                        or "--command"
                        or "--discover"
                        or "--project"
                        or "--syncs"
                        or "--reason"
                        or "--to"
                        or "--status"
                        or "--content"
                        or "--note"
                        or "--supersede"
                        or "--milestone"
                        or "--id"
                        or "--prefix"
                        or "--title"
                        or "--goal"
                        or "--type"
                        or "--priority"
                        or "--depends"
                        or "--accept"
                        or "--scope"
                        or "--lease"
                        or "--summary"
                        or "--commit"
                        or "--acc"
                        or "--acc-all"
                        or "--check"
            )
            {
                index++; // A value is data, even when it resembles another option.
            }

            index++;
        }

        return normalized;
    }

    private sealed class HostLifetime(IHost host) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (host is IAsyncDisposable asynchronous)
            {
                await asynchronous.DisposeAsync();
            }
            else
            {
                host.Dispose();
            }
        }
    }
}
