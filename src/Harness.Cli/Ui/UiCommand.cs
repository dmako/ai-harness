using System.ComponentModel;
using System.Diagnostics;
using Harness.Cli.Memory;
using Harness.Infrastructure.Projects;
using Harness.Web;
using Microsoft.Extensions.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Harness.Cli.Ui;

internal sealed class UiSettings : CommandSettings
{
    [CommandOption("--addr <ADDRESS>")]
    [Description("Loopback listen address (default 127.0.0.1:17477)")]
    public string Address { get; init; } = UiHost.DefaultAddress;
    [CommandOption("--no-open")]
    [Description("Do not open the browser")]
    public bool NoOpen { get; init; }
    public override ValidationResult Validate()
    {
        try
        { UiHost.ParseAddress(Address); return ValidationResult.Success(); }
        catch (ArgumentException exception) { return ValidationResult.Error(exception.Message); }
    }
}

internal sealed class UiCommand(ProjectLifecycle lifecycle, UiProjectCatalog catalog, UiSessions sessions, TextWriter output, CliErrorOutput error) : AsyncCommand<UiSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, UiSettings settings, CancellationToken cancellationToken)
    {
        await using var app = UiHost.Create(settings.Address, _ => new UiReadService(lifecycle, catalog, sessions),
            _ => new UiReadService(lifecycle, catalog, sessions), () => new UiChangeSource(lifecycle, catalog));
        await app.StartAsync(cancellationToken);
        var address = UiHost.Address(app);
        await output.WriteLineAsync("harness ui on " + address + " (Ctrl-C to stop)");
        await output.FlushAsync(cancellationToken);
        if (!settings.NoOpen)
        {
            try
            { OpenBrowser(address); }
            catch (Exception exception) when (exception is IOException or Win32Exception or InvalidOperationException)
            {
                await error.Writer.WriteLineAsync("harness ui: cannot open browser: " + exception.Message);
            }
        }
        try
        { await app.WaitForShutdownAsync(cancellationToken); }
        finally
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await app.StopAsync(deadline.Token);
        }
        return 0;
    }
    private static void OpenBrowser(string address)
    {
        var command = (OperatingSystem.IsWindows(), OperatingSystem.IsMacOS()) switch { (true, _) => "rundll32", (_, true) => "open", _ => "xdg-open" };
        var start = new ProcessStartInfo(command) { UseShellExecute = false, CreateNoWindow = true };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("url.dll,FileProtocolHandler");
        }
        start.ArgumentList.Add(address);
        using var process = Process.Start(start);
    }
}
