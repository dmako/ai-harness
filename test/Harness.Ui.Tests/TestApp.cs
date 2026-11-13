using Avalonia;
using Avalonia.Headless;
using Xunit;

namespace Harness.Ui.Tests;

/// <summary>The shared App on Avalonia's headless platform, drawn with Skia so frames can be captured.</summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Runs a test on the headless UI thread. Avalonia.Headless.XUnit 12.1.6 is built against xunit.v3 3.2 and fails
/// discovery under the 4.x this repository uses, so tests start one shared session themselves.
/// </summary>
internal static class Ui
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(
        () => HeadlessUnitTestSession.StartNew(typeof(TestApp))
    );

    /// <summary>
    /// Runs <paramref name="test"/> to its end on the UI thread. Only the Func&lt;Task&lt;T&gt;&gt; overload of Dispatch
    /// keeps the dispatcher running until the task ends; an async lambda as Func&lt;Task&gt; binds to the Func&lt;T&gt;
    /// one, which ends the test at its first await and loses its failures.
    /// </summary>
    public static Task RunAsync(Func<Task> test) =>
        Session.Value.Dispatch(
            async () =>
            {
                await test();
                return true;
            },
            TestContext.Current.CancellationToken
        );
}
