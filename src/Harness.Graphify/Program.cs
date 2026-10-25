using System.Runtime.InteropServices;
using Harness.Graphify;

using var input = Console.OpenStandardInput();
using var output = Console.OpenStandardOutput();
using var lifetime = new CancellationTokenSource();
ConsoleCancelEventHandler interrupt = (_, signal) =>
{
    signal.Cancel = true;
    lifetime.Cancel();
};
Console.CancelKeyPress += interrupt;
using var terminate = OperatingSystem.IsWindows()
    ? null
    : PosixSignalRegistration.Create(
        PosixSignal.SIGTERM,
        signal =>
        {
            signal.Cancel = true;
            lifetime.Cancel();
        }
    );
try
{
    return await GraphifyApplication.RunAsync(args, input, output, Console.Error, lifetime.Token);
}
finally
{
    Console.CancelKeyPress -= interrupt;
}
