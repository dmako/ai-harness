using Harness.Core.Architecture;
using Harness.Core.Providers;
using Harness.Infrastructure.Architecture;

namespace Harness.Infrastructure.Projects;

public sealed partial class HarnessStateSession : IChangeCouplingSession
{
    public async Task<ChangeCouplingReport> ChangeCouplingAsync(ChangeCouplingRequest request, IProviderRegistry? registry, CancellationToken token)
    {
        var limit = request.Limit <= 0 ? 10 : request.Limit;
        List<string> notes = [];
        if (registry is null || !registry.Capabilities().Any(capability => capability.Capability == Capabilities.ArchChangeCoupling))
        {
            notes.Add("no provider offers arch.change_coupling (ripwire does): partners are unknown");
        }

        var report = new ChangeCouplingReport();
        IReadOnlyList<string> errors;
        if (request.File.Length > 0)
        {
            var file = request.File.StartsWith("./", StringComparison.Ordinal) ? request.File[2..] : request.File;
            var result = await ChangeCoupling.PartnersAsync(registry, file, limit, token).ConfigureAwait(false);
            report = report with { Partners = result.Partners.Count is 0 ? null : result.Partners };
            errors = result.Errors;
        }
        else if (request.Check)
        {
            var changed = request.Files;
            if (changed is null || changed.Count is 0)
            {
                if (location.Repository is null)
                {
                    throw new InvalidOperationException("not a Git repository: name the changed files");
                }

                changed = await location.Repository.DirtyPathsAsync(token).ConfigureAwait(false);
            }
            var result = await ChangeCoupling.CheckAsync(registry, location.Root, changed, token).ConfigureAwait(false);
            report = report with { Changed = changed.Count is 0 ? null : changed, Missing = result.Missing.Count is 0 ? null : result.Missing };
            errors = result.Errors;
            if (result.Missing.Count is 0 && changed.Count > 0)
            {
                notes.Add("no usual partner is left out");
            }
        }
        else
        {
            if (location.Repository is null)
            {
                throw new InvalidOperationException("hotspots need a Git repository");
            }

            var result = await ChangeCoupling.HotspotsAsync(location.Repository, registry, limit, 3, token).ConfigureAwait(false);
            report = report with { Hotspots = result.Hotspots.Count is 0 ? null : result.Hotspots, Commits = result.Commits };
            errors = result.Errors;
        }
        var note = string.Join("; ", notes.Concat(errors));
        return report with { Note = note.Length is 0 ? null : note };
    }
}
