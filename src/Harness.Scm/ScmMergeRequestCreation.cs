namespace Harness.Scm;

/// <summary>
/// What every provider does the same way when it opens a merge request: checking the request before any call,
/// explaining a refused create (an open merge request for the same branches, a missing branch) and wording the
/// warnings of assignees and reviewers that could not be added.
/// </summary>
public static class ScmMergeRequestCreation
{
    /// <summary>
    /// The request with branches and title trimmed and usernames trimmed, without a leading @, blanks and duplicates.
    /// Throws an invalid <see cref="ScmException" /> for a missing branch or title, or a merge request into itself.
    /// </summary>
    public static ScmNewMergeRequest Normalize(ScmNewMergeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = (request.SourceBranch ?? "").Trim();
        var target = (request.TargetBranch ?? "").Trim();
        var title = (request.Title ?? "").Trim();
        var problem = (source, target, title) switch
        {
            ({ Length: 0 }, _, _) => "No source branch was given.",
            (_, { Length: 0 }, _) => "No target branch was given.",
            (_, _, { Length: 0 }) => "No title was given.",
            _ when string.Equals(source, target, StringComparison.Ordinal) =>
                $"The source and target branch are both '{source}'; a merge request needs two different branches.",
            _ => null,
        };
        if (problem is not null)
        {
            throw new ScmException(ScmErrorKind.Invalid, problem);
        }

        return new ScmNewMergeRequest(
            source,
            target,
            title,
            request.Description ?? "",
            Usernames(request.Assignees),
            Usernames(request.Reviewers)
        );
    }

    /// <summary>The usernames the server does not know, as one invalid error naming each of them.</summary>
    public static ScmException UnknownUsers(IReadOnlyCollection<string> usernames) =>
        new(
            ScmErrorKind.Invalid,
            (usernames.Count is 1 ? "Unknown user " : "Unknown users ")
                + string.Join(", ", usernames.Select(name => "'" + name + "'"))
                + ": no merge request was created. Check the usernames (as the SCM shows them, without @)."
        );

    /// <summary>A warning that <paramref name="usernames" /> could not be added in <paramref name="role" />.</summary>
    /// <param name="role">assignee or reviewer.</param>
    public static string Warning(string role, IReadOnlyCollection<string> usernames, string reason) =>
        $"Could not add {string.Join(", ", usernames)} as {role}{(usernames.Count is 1 ? "" : "s")}: {reason}";

    /// <summary>The requested usernames <paramref name="added" /> does not contain, compared ignoring case.</summary>
    public static List<string> Missing(IEnumerable<string> requested, IEnumerable<string> added)
    {
        var present = added.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return requested.Where(name => !present.Contains(name)).ToList();
    }

    /// <summary>
    /// Why the server refused to create a merge request, when the refusal was about its branches: an already open
    /// merge request for the same branches (named with its number and URL), or a branch that does not exist; else the
    /// server's own error. Lookups that fail leave the server's error as it was.
    /// </summary>
    /// <param name="noun">How the SCM names the request with its number prefix ("merge request !", "pull request #").</param>
    /// <param name="existing">The open merge request from the source into the target branch, if any.</param>
    /// <param name="branchExists">Whether a branch exists; null when that cannot be told (a fork's branch).</param>
    public static async Task<ScmException> ExplainAsync(
        ScmException failure,
        ScmNewMergeRequest request,
        string noun,
        Func<CancellationToken, Task<MergeRequest?>> existing,
        Func<string, CancellationToken, Task<bool?>> branchExists,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(branchExists);
        if (failure.Kind is not (ScmErrorKind.Invalid or ScmErrorKind.NotFound))
        {
            return failure;
        }

        try
        {
            if (await existing(cancellationToken).ConfigureAwait(false) is { } open)
            {
                return new ScmException(
                    ScmErrorKind.Invalid,
                    $"An open {noun}{open.Number} already merges '{request.SourceBranch}' into '{request.TargetBranch}'"
                        + (open.WebUrl is { } url ? $" ({url.AbsoluteUri})" : "")
                        + ": update that one instead of opening another.",
                    failure.Status,
                    failure
                );
            }

            foreach (var (role, branch) in new[] { ("source", request.SourceBranch), ("target", request.TargetBranch) })
            {
                if (await branchExists(branch, cancellationToken).ConfigureAwait(false) is false)
                {
                    return new ScmException(
                        ScmErrorKind.Invalid,
                        $"The {role} branch '{branch}' does not exist on the server"
                            + (role is "source" ? "; push it first." : "."),
                        failure.Status,
                        failure
                    );
                }
            }
        }
        catch (ScmException)
        {
            // the lookups failed too (e.g. the project is not visible): the create's own error says more
        }

        return failure;
    }

    private static List<string> Usernames(IReadOnlyList<string>? names) =>
        (names ?? [])
            .Select(name => (name ?? "").Trim().TrimStart('@').Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
