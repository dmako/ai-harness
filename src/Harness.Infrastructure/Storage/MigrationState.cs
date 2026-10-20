namespace Harness.Infrastructure.Storage;

public sealed record MigrationState(int Applied, int Latest, Exception? Error)
{
    public bool Pending => Error is null && Applied < Latest;
}
