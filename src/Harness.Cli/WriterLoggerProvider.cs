using Microsoft.Extensions.Logging;

namespace Harness.Cli;

internal sealed class WriterLoggerProvider(TextWriter writer) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new WriterLogger(writer);

    public void Dispose()
    {
        // The caller owns the writer.
    }

    private sealed class WriterLogger(TextWriter output) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel is not LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                output.WriteLine($"{logLevel}: {formatter(state, exception)}");
            }
        }
    }
}
