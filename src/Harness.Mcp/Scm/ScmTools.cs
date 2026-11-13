using System.Globalization;
using System.Text.Json;
using Harness.Scm;

namespace Harness.Mcp.Scm;

/// <summary>
/// The scm_ tools: the current project's merge requests on its SCM (GitLab, Forgejo, GitHub), read and reviewed through
/// Harness.Scm's providers. A failure becomes a tool error that names its kind and the fix, never the token.
/// </summary>
public static class ScmTools
{
    /// <summary>Most diff text one scm_get_merge_request_diff answer carries; the rest is marked truncated per file.</summary>
    public const int DiffBudget = 60_000;

    public static bool Handles(string name) => name.StartsWith("scm_", StringComparison.Ordinal);

    public static async Task<object> CallAsync(
        string name,
        IDictionary<string, JsonElement>? arguments,
        IScmSession? session,
        CancellationToken cancellationToken
    )
    {
        if (session?.ScmProviders is null)
        {
            throw new InvalidOperationException("SCM tools are unavailable in this session (no SCM providers).");
        }

        var args = new Arguments(arguments);
        try
        {
            return name switch
            {
                "scm_get_projects" => await ProjectsAsync(session, args, cancellationToken).ConfigureAwait(false),
                _ => await OnProjectAsync(name, session, args, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (ScmException exception)
        {
            throw new InvalidOperationException(
                $"{ScmErrors.Name(exception.Kind)}: {exception.Message} Fix: {ScmErrors.Hint(exception.Kind)}",
                exception
            );
        }
        catch (ScmConfigurationException exception)
        {
            throw new InvalidOperationException(
                exception.Message + " Run 'harness doctor' for the SCM checks.",
                exception
            );
        }
    }

    private static async Task<object> ProjectsAsync(IScmSession session, Arguments args, CancellationToken token)
    {
        var visibility = args.Choice<ScmVisibility>(
            "visibility",
            "private",
            ScmNames.TryParseVisibility,
            "private, internal or public"
        );
        var connection = await ProjectsConnectionAsync(session, args.Text("scm"), token).ConfigureAwait(false);
        using var client = session.ScmProviders!.Connect(connection);
        var projects = await client
            .ListProjectsAsync(args.Text("search") is { Length: > 0 } search ? search : null, visibility, token)
            .ConfigureAwait(false);
        return Answer(
            connection.Endpoint,
            new()
            {
                ["visibility"] = ScmNames.Name(visibility),
                ["projects"] = projects.Select(Project).ToList(),
                ["count"] = projects.Count,
            }
        );
    }

    /// <summary>The SCM to list projects on: the one named, else the project's, else the only one configured.</summary>
    private static async Task<ScmConnection> ProjectsConnectionAsync(
        IScmSession session,
        string name,
        CancellationToken token
    )
    {
        if (name.Length > 0)
        {
            return session.ScmConfiguration.Resolve(name);
        }

        var resolution = await session.ResolveScmAsync(token).ConfigureAwait(false);
        if (resolution.Match is { } match)
        {
            return session.ScmConfiguration.Resolve(match.Endpoint.Name);
        }

        var entries = session.ScmConfiguration.List();
        return entries.Count switch
        {
            0 => throw new InvalidOperationException(NoScm),
            1 => session.ScmConfiguration.Resolve(entries[0].Name),
            _ => throw new InvalidOperationException(
                "This project's SCM is not known ("
                    + resolution.Reason
                    + ") and several are configured ("
                    + string.Join(", ", entries.Select(entry => entry.Name))
                    + "): pass scm with one of them."
            ),
        };
    }

    private const string NoScm =
        "No SCM is configured on this machine. Ask the user to add one: 'harness scm add <name> --type gitlab|forgejo|github "
        + "--url <url> --token-stdin', or in harness ui under Machine › SCM connections.";

    private static async Task<object> OnProjectAsync(
        string name,
        IScmSession session,
        Arguments args,
        CancellationToken token
    )
    {
        var resolution = await session.ResolveScmAsync(token).ConfigureAwait(false);
        var match = resolution.Match ?? throw new InvalidOperationException(Unresolved(resolution));
        var connection = session.ScmConfiguration.Resolve(match.Endpoint.Name);
        using var client = session.ScmProviders!.Connect(connection);
        var project = match.Project;
        Dictionary<string, object?> body = new(StringComparer.Ordinal) { ["project"] = project };
        if (name is "scm_list_merge_requests")
        {
            var state = args.Choice<MergeRequestState>(
                "state",
                "opened",
                ScmNames.TryParseState,
                "opened, merged or closed"
            );
            var requests = await client.ListMergeRequestsAsync(project, state, token).ConfigureAwait(false);
            body["state"] = ScmNames.Name(state);
            body["merge_requests"] = requests.Select(Summary).ToList();
            body["count"] = requests.Count;
            return Answer(connection.Endpoint, body);
        }

        if (name is "scm_create_merge_request")
        {
            return Answer(
                connection.Endpoint,
                await CreateAsync(client, project, args, body, token).ConfigureAwait(false)
            );
        }

        var number = args.Number("merge_request_id");
        body["merge_request_id"] = number;
        switch (name)
        {
            case "scm_get_merge_request_details":
                body["merge_request"] = Request(
                    await client.GetMergeRequestAsync(project, number, token).ConfigureAwait(false)
                );
                break;
            case "scm_get_merge_request_comments":
                var comments = await client.ListCommentsAsync(project, number, token).ConfigureAwait(false);
                body["comments"] = comments.Select(Comment).ToList();
                body["count"] = comments.Count;
                break;
            case "scm_add_merge_request_comment":
                body["comment"] = Comment(
                    await client.AddCommentAsync(project, number, args.Required("comment"), token).ConfigureAwait(false)
                );
                break;
            case "scm_add_merge_request_diff_comment":
                body["comment"] = Comment(
                    await client
                        .AddDiffCommentAsync(
                            project,
                            number,
                            new ScmDiffComment(
                                args.Required("comment"),
                                args.Required("file_path"),
                                (int)args.Number("line_number"),
                                args.Choice<DiffLineType>("line_type", "new", ScmNames.TryParseLineType, "new or old")
                            ),
                            token
                        )
                        .ConfigureAwait(false)
                );
                break;
            case "scm_get_merge_request_diff":
                var diff = await client.GetDiffAsync(project, number, DiffBudget, token).ConfigureAwait(false);
                body["files"] = diff.Files.Select(File).ToList();
                body["truncated"] = diff.Truncated;
                body["omitted_files"] = diff.OmittedFiles;
                body["max_bytes"] = DiffBudget;
                if (diff.Truncated)
                {
                    body["hint"] =
                        "The diff was cut to fit; files marked truncated keep only their first hunks or their paths. "
                        + "Read those files in the working tree (the merge request's source branch) for the rest.";
                }

                break;
            case "scm_set_merge_request_title":
                body["merge_request"] = Edited(
                    await client.SetTitleAsync(project, number, args.Required("title"), token).ConfigureAwait(false)
                );
                break;
            case "scm_set_merge_request_description":
                body["merge_request"] = Edited(
                    await client
                        .SetDescriptionAsync(project, number, args.Text("description", allowEmpty: true), token)
                        .ConfigureAwait(false)
                );
                break;
            case "scm_approve_merge_request":
                var approval = await client.ApproveAsync(project, number, token).ConfigureAwait(false);
                body["approved"] = approval.Approved;
                body["approved_by"] = approval.ApprovedBy.Select(user => user.Username).ToList();
                break;
            default:
                throw new InvalidOperationException($"unknown SCM tool {name}");
        }

        return Answer(connection.Endpoint, body);
    }

    /// <summary>Opens a merge request; what could not be done once it existed comes back as warnings.</summary>
    private static async Task<Dictionary<string, object?>> CreateAsync(
        IScmClient client,
        string project,
        Arguments args,
        Dictionary<string, object?> body,
        CancellationToken token
    )
    {
        // checked here too, so a request into its own branch is refused before any call
        var request = ScmMergeRequestCreation.Normalize(
            new ScmNewMergeRequest(
                args.Required("from"),
                args.Required("to"),
                args.Required("title"),
                args.Text("description"),
                args.Names("assignees"),
                args.Names("reviewers")
            )
        );
        var created = await client.CreateMergeRequestAsync(project, request, token).ConfigureAwait(false);
        body["merge_request_id"] = created.MergeRequest.Number;
        body["merge_request"] = Request(created.MergeRequest);
        body["warnings"] = created.Warnings;
        return body;
    }

    private static string Unresolved(ScmResolution resolution) =>
        resolution.Status switch
        {
            ScmResolutionStatus.NoScm => NoScm,
            ScmResolutionStatus.Ambiguous => $"This project's SCM is not known: {resolution.Reason} Ask the user which "
                + "connection to use ("
                + string.Join(", ", resolution.Candidates.Select(endpoint => endpoint.Name))
                + "): they choose it with 'harness scm use <name>' or in harness ui under Agents › Tools & providers.",
            _ => $"This project's SCM is not known: {resolution.Reason} Run 'harness doctor' for the SCM checks; "
                + "the user can choose one with 'harness scm use <name>', or set [scm] name and project in "
                + ".harness/config.toml.",
        };

    private static Dictionary<string, object?> Answer(ScmEndpoint endpoint, Dictionary<string, object?> body)
    {
        body["scm"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = endpoint.Name,
            ["type"] = ScmKinds.Name(endpoint.Kind),
            ["url"] = endpoint.BaseUrl.AbsoluteUri.TrimEnd('/'),
        };
        return body;
    }

    private static string? Time(DateTimeOffset? at) => at?.ToString("O", CultureInfo.InvariantCulture);

    private static Dictionary<string, object?> Project(ScmProject project) =>
        new(StringComparer.Ordinal)
        {
            ["id"] = project.Id,
            ["path"] = project.Path,
            ["name"] = project.Name,
            ["description"] = project.Description,
            ["visibility"] = ScmNames.Name(project.Visibility),
            ["web_url"] = project.WebUrl?.AbsoluteUri,
            ["default_branch"] = project.DefaultBranch,
        };

    private static Dictionary<string, object?> Summary(MergeRequest request) =>
        new(StringComparer.Ordinal)
        {
            ["id"] = request.Number,
            ["title"] = request.Title,
            ["state"] = ScmNames.Name(request.State),
            ["author"] = request.Author?.Username,
            ["source_branch"] = request.SourceBranch,
            ["target_branch"] = request.TargetBranch,
            ["draft"] = request.Draft,
            ["updated_at"] = Time(request.UpdatedAt),
            ["web_url"] = request.WebUrl?.AbsoluteUri,
        };

    private static Dictionary<string, object?> Request(MergeRequest request)
    {
        var wire = Summary(request);
        wire["global_id"] = request.Id;
        wire["description"] = request.Description;
        wire["head_sha"] = request.HeadSha;
        wire["base_sha"] = request.BaseSha;
        wire["start_sha"] = request.StartSha;
        wire["created_at"] = Time(request.CreatedAt);
        wire["merged_at"] = Time(request.MergedAt);
        wire["approved"] = request.Approved;
        wire["approved_by"] = request.ApprovedBy.Select(user => user.Username).ToList();
        wire["reviewers"] = request.Reviewers.Select(user => user.Username).ToList();
        wire["labels"] = request.Labels;
        wire["merge_status"] = request.MergeStatus;
        return wire;
    }

    private static Dictionary<string, object?> Edited(MergeRequest request) =>
        new(StringComparer.Ordinal)
        {
            ["id"] = request.Number,
            ["title"] = request.Title,
            ["description"] = request.Description,
            ["web_url"] = request.WebUrl?.AbsoluteUri,
        };

    private static Dictionary<string, object?> Comment(ScmComment comment)
    {
        Dictionary<string, object?> wire = new(StringComparer.Ordinal)
        {
            ["id"] = comment.Id,
            ["kind"] = comment.Position is null ? "general" : "diff",
            ["body"] = comment.Body,
            ["author"] = comment.Author?.Username,
            ["created_at"] = Time(comment.CreatedAt),
            ["system"] = comment.System,
            ["thread_id"] = comment.ThreadId,
            ["web_url"] = comment.WebUrl?.AbsoluteUri,
        };
        if (comment.Position is { } position)
        {
            wire["position"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["file_path"] = position.NewPath ?? position.OldPath,
                ["old_path"] = position.OldPath,
                ["new_line"] = position.NewLine,
                ["old_line"] = position.OldLine,
            };
        }

        return wire;
    }

    private static Dictionary<string, object?> File(ScmFileDiff file) =>
        new(StringComparer.Ordinal)
        {
            ["old_path"] = file.OldPath,
            ["new_path"] = file.NewPath,
            ["new_file"] = file.NewFile,
            ["deleted_file"] = file.DeletedFile,
            ["renamed_file"] = file.RenamedFile,
            ["binary"] = file.Binary,
            ["diff"] = file.Diff,
            ["truncated"] = file.Truncated,
        };

    private delegate bool Parse<T>(string? text, out T value);

    /// <summary>The call's arguments, checked beyond what the schema validator does (values, ranges).</summary>
    private sealed class Arguments(IDictionary<string, JsonElement>? values)
    {
        public string Text(string name, bool allowEmpty = true)
        {
            if (values is null || !values.TryGetValue(name, out var value) || value.ValueKind is JsonValueKind.Null)
            {
                return "";
            }

            var text = value.GetString() ?? "";
            return allowEmpty ? text : text.Trim();
        }

        public string Required(string name)
        {
            var text = Text(name);
            return text.Trim().Length > 0 ? text : throw new ArgumentException($"{name} must not be empty");
        }

        /// <summary>A list of usernames; empty when not given.</summary>
        public List<string> Names(string name)
        {
            if (values is null || !values.TryGetValue(name, out var value) || value.ValueKind is JsonValueKind.Null)
            {
                return [];
            }

            return value.ValueKind is JsonValueKind.Array
                ? value
                    .EnumerateArray()
                    .Select(item =>
                        item.ValueKind is JsonValueKind.String
                            ? item.GetString() ?? ""
                            : throw new ArgumentException($"{name} must be a list of usernames")
                    )
                    .ToList()
                : throw new ArgumentException($"{name} must be a list of usernames");
        }

        public long Number(string name)
        {
            if (values is null || !values.TryGetValue(name, out var value) || !value.TryGetInt64(out var number))
            {
                throw new ArgumentException($"{name} is required: a whole number");
            }

            if (number is <= 0 or > int.MaxValue)
            {
                throw new ArgumentException($"{name} must be a positive whole number");
            }

            return number;
        }

        public T Choice<T>(string name, string fallback, Parse<T> parse, string allowed)
        {
            var text = Text(name);
            var given = text.Length is 0 ? fallback : text;
            return parse(given, out var choice)
                ? choice
                : throw new ArgumentException($"{name} must be {allowed}; got '{text}'");
        }
    }
}
