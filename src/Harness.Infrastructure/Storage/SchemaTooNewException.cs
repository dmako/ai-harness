using System.Diagnostics.CodeAnalysis;

namespace Harness.Infrastructure.Storage;

[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "A schema compatibility error requires both database and supported versions."
)]
public sealed class SchemaTooNewException(int applied, int latest)
    : InvalidOperationException(
        $"Database schema is newer than this harness binary supports (database at version {applied}, binary knows up to {latest}): upgrade harness, or restore a backup from before the upgrade (.harness/backups/schema-{latest:00}-*.db or older; changes made since are lost)."
    )
{
    public int Applied { get; } = applied;

    public int Latest { get; } = latest;
}
