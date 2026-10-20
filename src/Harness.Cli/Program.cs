using Harness.Cli;

using var cancellation = new CancellationTokenSource();
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
