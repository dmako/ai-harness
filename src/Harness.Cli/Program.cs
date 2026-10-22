using System.Runtime.InteropServices;
using Harness.Cli;

using var cancellation = new CancellationTokenSource();
using var terminate = OperatingSystem.IsWindows()
    ? null
    : PosixSignalRegistration.Create(
        PosixSignal.SIGTERM,
        context =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        }
    );
ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += onCancel;
try
{
    return await CliApplication.RunAsync(args, Console.Out, Console.Error, cancellation.Token);
}
finally
{
    Console.CancelKeyPress -= onCancel;
}
