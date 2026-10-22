namespace Harness.Providers.Graphify;

public sealed class GraphifyQueryException(string verb, int bytes, Exception innerException)
    : Exception(innerException.Message, innerException)
{
    public GraphifyQueryException()
        : this("", 0, new IOException("graphify query failed")) { }

    public GraphifyQueryException(string message)
        : this("", 0, new IOException(message)) { }

    public GraphifyQueryException(string message, Exception innerException)
        : this("", 0, new IOException(message, innerException)) { }

    public string Verb { get; } = verb;
    public int Bytes { get; } = bytes;
}
