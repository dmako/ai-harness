using System.Runtime.InteropServices;
using System.Text;
using Harness.Agents;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
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
    return await AgentHost.RunAsync(args, Console.In, Console.Out, Console.Error, lifetime.Token);
}
finally
{
    Console.CancelKeyPress -= interrupt;
}
