# Using harness

A short guide for the owner of a project that uses harness: what it keeps
for you, how it gets filled, and how to give agents work. The reference
(`harness docs` lists its sections) has the details of every command.

## What harness is for

Agents forget everything between sessions and see code only through what
they search for. Harness keeps, per repository, what should not be
forgotten and answers agents' questions about the code in one place:

- **Code**: `get_context` answers a task in words with the code that
  matters, its callers and impact, from the providers configured
  (codebase-memory, graphify for C#, ripwire) and harness's built-in text
  and file search merged into one answer. `search_project`, `package_api`
  and `architecture_findings` drill down.
- **Knowledge**: goals, constraints, decisions, conventions and warnings,
  each a short statement with where it came from. This is the project's
  retrieval ("RAG") of decisions and constraints: `get_context` adds the
  ones that apply to the code it returns, and `decisions_for` lists them
  for an area. You do not maintain a separate document for it.
- **Work**: tasks with goals, acceptance criteria and dependencies, which
  agents take one at a time and complete with evidence.

Everything lives in the repository's `.harness/` folder; harness ui
(`harness ui`) shows it all.

## How knowledge gets into memory

You rarely write memories yourself. Agents record them while they work:

- **What you settle in conversation** (a decision you made, a constraint
  you named, a convention you corrected) the agent records with
  `memory_put`, citing where it came from.
- **What the agent believes but you have not confirmed** it proposes with
  `memory_propose`. Proposals wait in harness ui under the project's
  Knowledge › Inbox: approve, edit or reject them. Nothing proposed is used
  until you approve it.
- **When something changes**, the old statement is superseded, not
  deleted: its history stays.

Worth recording: decisions and their reasons ("we use X because Y"), rules
the code must follow ("the DAL never references WebApi"), conventions an
agent would get wrong, warnings about traps. Not worth recording: what the
code or Git history already says, temporary notes, transcripts.

To start a project's memory, work with an agent for a while and review the
Inbox; or tell an agent the five things every newcomer should know and ask
it to record them. You can also add knowledge yourself: a project's
Knowledge › Add knowledge records it as yours, at the project or a path,
subsystem or branch.

Each statement has one of seven types (goal, constraint, decision,
convention, warning, fact, open question), and the type decides where it
reaches agents: `decisions_for` returns an area's constraints, decisions,
conventions and warnings; `get_context` brings those with the code it
returns, and the facts and open questions that apply there too. An open
question is yours to settle: agents are told to ask, not to decide it. The
reference's Knowledge types section has each type's meaning and an example.

## Sharing knowledge with the team

Each clone keeps its own knowledge. To give your teammates' agents the
same decisions, constraints and conventions, share them through Git: the
shared ones live in `.harness/knowledge.toml`, a file in the repository
that is committed, reviewed and merged like code.

- **What belongs there**: what holds for everyone working on the project,
  such as the rules the code follows, the decisions behind it and the traps
  in it. Personal preferences, notes about your own setup and anything you
  have not settled stay local.
- **Share** a memory with its card's Share with project (Knowledge) or
  `harness memory share <id>`. Harness writes it to the file at once and
  marks it PROJECT; commit and push the file when you are ready. Only you
  share: agents read shared knowledge but never share or withdraw it.
- **Withdraw** it with Withdraw from project or `harness memory withdraw
  <id>`. It leaves the file and stays as your local memory.
- **Correcting** a PROJECT memory rewrites its entry with the new
  statement; **retiring** it removes the entry. Commit the file to pass the
  change on.

After a pull, checkout or merge, the Git hooks run `harness sync`, which
reads the file when it changed. (Without the hooks, the next agent
knowledge read or `harness sync` does it.) Then:

- New entries become memories with the same ids, marked PROJECT. A local
  memory that says the same thing is linked to the entry, not duplicated.
- A changed entry supersedes your copy; the old statement stays in its
  history.
- When an entry is gone from the file, its memory stays current but is
  marked removed from the project. Knowledge › Inbox asks you to keep it as
  local or retire it.

Git must be able to see the file. A root `.gitignore` that ignores the
whole `.harness/` folder hides it, and Git cannot re-include a file from an
ignored folder. Sharing, `harness doctor` and Health say so and how to fix
the rule: ignore `.harness/*` and add `!.harness/knowledge.toml`, or drop
the rule, because `.harness/.gitignore` already keeps the database out.

## How to define work

Work is a list of tasks, grouped into blueprints, kept in a backlog file
(`docs/backlog.yaml` by default) and loaded with `harness tasks import`.
`harness tasks import --help` shows the format with an example.

A good task names its goal (the outcome and why), its scope (the paths it
may touch), its dependencies, and acceptance criteria that can be checked:
the agent completes a task only by reporting every criterion with
evidence. Blueprints say what a group of tasks is for and when it is done.
They were called milestones: backlog files with `milestones:` and
`milestone:` still import, and a project's database is migrated on first
use.

Agents take the next ready task (`task_next`), work on it and complete it
(`task_complete`). A claimed task stays claimed while the agent analyses it
and asks you what is unclear; the agent moves it to in progress when it
starts changing code and to review while it checks the acceptance criteria
and validation, then completes it. A task completed without a review step
says so in its audit trail (review skipped). You follow the queue in harness
ui (Work) or with `harness tasks list`. With the Claude Code or Codex hooks
installed, a task also lists the agent sessions that worked on it (Work ›
Tasks, `task_get`): which client, when each was first and last seen, and what
its last call did.

You can also start Claude Code or Codex on a task from harness ui (a task's
Agent sessions › Start an agent). It runs beside harness ui in its own
process, `harness-agents`, and goes on when the page or harness ui closes.
Pick its permission mode next to the model: Auto (the default) lets it do
what it knows to be safe and asks you for the rest; Manual asks for more, and
the modes marked unsafe ask for nothing. Its panel follows it live: send it a message, stop the turn it is running,
allow or deny a tool call it asks permission for, close it, and resume it
later with a new prompt. A session started elsewhere shows the end of its
transcript, read-only, with the command to resume it in a terminal.

Work can also start without the backlog file:

- **By hand:** `harness tasks add --title … --goal … --accept …`, or
  harness ui's Work › Tasks › New task. The task gets the next free id of
  your backlog's prefix and lives in the project's database;
  `harness tasks export` writes it into the backlog file, so the file stays
  the full list.
- **Proposed by agents:** an agent that finds work you have not planned
  proposes it (`task_propose`); it never creates tasks itself. Proposals
  wait in harness ui's Work › Inbox (or `harness tasks proposals`): approve
  one, restating it first if you want, and the task is made with the agent
  named as its proposer; reject it and nothing changes. A proposal may name
  its blueprint by id or by title; it keeps that blueprint when approved,
  even one not described yet.
- **Approved outside harness:** when the work was already approved
  elsewhere (a change board, a ticket, you in the conversation), the agent
  names that approver in `task_propose`'s or `task_blueprint_propose`'s
  `approved_by`. The proposal is then approved at once in their name, as an
  Inbox approval would do it, and never waits in the Inbox. The task's
  history and `harness tasks proposals --status approved` show it as
  "approved externally by …". Agents still propose by default and use
  `approved_by` only for an approval that really happened.

Until an agent claims a task, you can restate it in harness ui: Work ›
Tasks, the pencil in the task's row (disabled once an agent claims it)
opens the same form as New task; the id, validation and notes stay. A task
that is also in the backlog file is restated from the file again at its
next import. Drag a task onto another blueprint's group to move it there;
the move is in its audit trail.

To put the blueprints in the order you work through them, drag a blueprint
before or after another (a bar shows where it lands), or use the up and
down arrows beside it: by its header in Work › Tasks while all blueprints
are shown, or as a whole card in Work › Blueprints under any filter (a move
among the cards shown keeps the hidden ones in their places). The order is
saved with the project, so every listing of the blueprints (both views,
`harness tasks blueprints`, the agents' `task_blueprints`) follows it, in
any browser and after a restart; importing the backlog does not change it.
A blueprint you have not placed yet, such as a new one, comes after the
ordered ones.

To delete a task, use the bin at the end of its row: a dialog says what
goes (the task, its criteria, its dependencies and its audit trail) before
anything does. Deleting is refused while an agent holds the task or while
other tasks depend on it, naming them. Only harness ui deletes a task; no
command or agent tool does.

Blueprints are kept in harness ui too: Work › Blueprints › New blueprint
starts with the next free id (M17 after M16, or BP-001); the pencil on a
blueprint's card changes its name, goal, exit criteria, notes and colour,
not its id. The bin removes it after you confirm; for a blueprint with tasks it
also asks what happens to them: moved to another blueprint (or to none), or
deleted with their audit trail. Deleting is refused while an agent holds
one of the tasks, and a task elsewhere that depended on a deleted one loses
that dependency. A blueprint or task still in the backlog file comes back
at its next import: remove it there as well.

A task can wait for a task of another project on this machine, written
`<project>:<task id>` (the project by its short name, folder name,
parent/folder or id): in a backlog file, with `harness tasks add
--depends`, from an agent's `task_propose`, or in harness ui's New task,
whose dependency search offers the open tasks of the workspace's other
projects. The task stays in the backlog until the other one is done, and
goes back if it reopens; the other project is only read, never changed.

The same is done on the command line: `harness tasks blueprints add`,
`edit <id>` (what you do not give stays) and `remove <id>` (with
`--tasks move [--to <blueprint>]` or `--tasks delete` when it has tasks).
Agents read the blueprints (`task_blueprints`) but do not change them: they
propose a new blueprint or a change to one (`task_blueprint_propose`), and
the proposal waits in Work › Inbox, or `harness tasks proposals`, beside the
proposed tasks.

## What harness ui shows

Each project opens on its Overview: rings for the tasks done and how well
`get_context` answered, each with its trend over the window; what is in
flight or next; and what needs a person, most urgent first. Then:

- **Architecture:** Structure (projects or packages and their smells),
  Layers (a dashed line is a project only built first, through
  `ReferenceOutputAssembly=false`; a NuGet package with a newer stable
  version or a known vulnerability is marked, see Package upgrades and
  vulnerabilities), Complexity (every function by cyclomatic complexity, the most
  complex named) and Change coupling.
- **Knowledge:** the memories by type (the team's marked PROJECT, with
  Share with project or Withdraw from project on each card), what applies
  to a path, and the Inbox: proposals to approve and memories a Git change
  removed from project knowledge.
- **Work:** Blueprints, Tasks and the Inbox of proposed tasks, blueprints
  and architecture work.
- **Board:** the tasks as a read-only board, a card per task in a column
  per status: backlog, ready, claimed, in progress, review, done and blocked.
  Deferred tasks are not on the Board: only `harness tasks defer` sets a task
  aside, and Work › Tasks lists them. A card shows the task's type, id and
  priority, its title, and its blueprint's title in the blueprint's colour.
  The task's id opens it as Work › Tasks does (with Edit and Delete where
  Work allows them).
  Sort the cards by priority, id, name, created or last changed, hide done
  tasks finished more than 1, 3, 7 or 14 days ago, and show one blueprint's
  tasks; the choices are kept for the project on this machine.
- **Agents:** how harness serves your agents and what you can change so it
  serves them better.
- **Health:** the checks `harness doctor` runs; a check harness can fix
  itself has Fix it, and a stale index has Sync now.

Each blueprint has a colour, one of 16: its id and title are framed in it on
the Board and in Work › Tasks. A new blueprint gets the first colour no other
uses (once all 16 are, they start again from the first); pick another in its
edit dialog, with `harness tasks blueprints edit <id> --color <name>`, or as
`color:` in the backlog file.

A workspace has the same views over its members. Machine holds this page's
settings, the Board's defaults (the sort and done-age of a project that has
not chosen its own), the SCM connections (add, edit, remove and test them),
the C# sessions, and the switch for the opt-in developer telemetry. How harness itself performs (relevance per provider, latency)
is for the harness developer: it is not shown here, only sent in that
telemetry if you turn it on.

## Several checkouts of the same code

- **Worktrees** (`git worktree add`) of one repository share one project:
  one `.harness/` database, one memory, one task queue. This is the way to
  work on several branches at once.
- **Separate clones** are separate projects, each with its own `.harness/`.
  Their knowledge does not mix, except what you share through
  `.harness/knowledge.toml` (see Sharing knowledge with the team). To share knowledge between related
  repositories (a service and its library), put them in a workspace
  (`harness workspace`): workspace-level knowledge applies to all members,
  and searches can span them.

## Merge requests on GitLab, Forgejo and GitHub

harness lets agents read and review the merge requests of a project hosted on
GitLab (REST API v4), Forgejo (REST API v1, also Gitea) or GitHub (github.com
or GitHub Enterprise Server; there they are pull requests): list them, read the
details, diff and comments, comment on the whole request or on one changed
line, change the title or description, approve, and open new ones.

**Connect a server once per machine.** Only access tokens are supported: a
GitLab personal, group or project access token, a Forgejo access token, or a
GitHub fine-grained or classic personal access token. Give it only what the
agents should do; [Token permissions](#guide-token-permissions) lists the
scopes and permissions each harness operation needs, per SCM. A GitHub
connection's URL is `https://github.com`, or your Enterprise Server's address
(harness calls its `/api/v3`).

```
harness scm add work --type gitlab --url https://gitlab.example.com --token-stdin
```

and paste the token (`--token-env NAME` reads it from an environment variable
on every call instead of storing it). The type is always given. `harness scm
list` shows the connections with tokens masked, `harness scm set` changes one
and `harness scm remove` drops it. harness ui has the same under Machine › SCM
connections, where the token is a password field that is never shown again.
If your repositories are cloned from another name for the same server (an alias
host name, a proxy path, an SSH host alias from `~/.ssh/config`), add it as an
alias so their remotes still find the connection: `harness scm alias add work
ssh-alias` (or `https://git-alias.example.com`), or in the SCM's dialog in
harness ui. Aliases only match remotes; harness always calls the URL.
Both store them in `scm.toml` beside the global `config.toml`, written so that
only you can read it, with each stored token encrypted by keys in `scm-keys/`
next to it: keep the two together in backups and on other machines (see the
reference's SCM connections for what the encryption does and does not protect).

**Check it.** `harness scm test` (or Test connection in harness ui) shows the
server's version and the token's user, or what failed: unreachable, TLS,
token rejected, not permitted, wrong server type, each with its fix.
`harness doctor` does the same for every connection, warns if `scm.toml` can
be read by others, and says which SCM and project the current repository is
on and whether the token reaches it.

**Which project.** A project needs no setup: harness takes the repository's
git remotes (`origin` first), finds the connection whose host (and path, for
an instance under one) the remote's URL is on, and takes the rest of the URL as
the project: `git@gitlab.example.com:group/sub/repo.git` is `group/sub/repo`.
`harness status` shows it. When the remotes do not say (a mirror, an SSH host
alias), set it in the project's `.harness/config.toml`:

```toml
[scm]
name = "work"
project = "group/sub/repo"
```

**Several connections for one server.** You may add the same server more than
once, with different tokens (your own and a review bot's, say). harness then
cannot tell which one a project should use, and says so. Choose it for this
machine on the project's Agents › Tools & providers page, where the SCM card
under Used here lists the connections serving the project (Automatic goes back
to detecting it), or with:

```
harness scm use bot          # this repository uses SCM bot on this machine
harness scm use --auto       # detect it from the remotes again
harness scm which            # which SCM and project, and how it was chosen
```

The choice is kept in `scm.toml` on your machine, not in the repository:
teammates name their connections as they like. A `[scm] name` in the project's
`.harness/config.toml`, which is shared, still wins over it. The same card tests
the connection and whether the project is reachable with its token, and shows
how often agents called the `scm_` tools and how many calls failed.

**Agentic code review.** Ask your agent to review a merge request, for example
"review merge request 42". It reads the request (`scm_get_merge_request_details`),
its diff (`scm_get_merge_request_diff`) and the discussion so far
(`scm_get_merge_request_comments`), reads the code around the changes in the
working tree, then leaves one comment per finding on the line it is about
(`scm_add_merge_request_diff_comment`) and a summary
(`scm_add_merge_request_comment`). Agents change a title or description,
approve, or open a merge request only when you ask: those act as the token's
user. If an agent says no SCM matches the project, `harness doctor` tells why.

**Opening a merge request.** Ask your agent to open one once its branch is
pushed, for example "open a merge request from this branch into main and ask
bob to review it". It calls `scm_create_merge_request` with the source and
target branch, a title and description, and optionally assignees and reviewers
(usernames as the SCM shows them). harness refuses, without creating anything,
when a branch is missing, both branches are the same, a username is unknown, or
an open merge request already merges the same branches (it names that one's
number and link). If the merge request is created but the SCM refuses an
assignee or reviewer (no access to the project, say), the agent gets the new
merge request with a warning naming who was not added and why.

## Token permissions

harness acts as the token's user, so the token decides what agents can do.
Give it the least that covers what you want them to do: a read-only token lets
agents read and review privately (they report findings to you), a writing one
lets them comment, edit, approve and open merge requests. The tables below say
what each harness operation calls and needs; the token's user also needs the
role or access in the project that the SCM itself requires for that action.
They were checked against each provider's documentation in November 2026
(sources under each table).

The operations: **test** is the connection test (`harness scm test`, harness
ui, `harness doctor`); **projects** is `scm_get_projects`; **read** is listing
and reading merge requests, their diffs and comments
(`scm_list_merge_requests`, `scm_get_merge_request_details`,
`scm_get_merge_request_diff`, `scm_get_merge_request_comments`, and finding the
project's SCM project); **comment** is `scm_add_merge_request_comment` and
`scm_add_merge_request_diff_comment`; **edit** is
`scm_set_merge_request_title` and `scm_set_merge_request_description`;
**approve** is `scm_approve_merge_request`; **create** is
`scm_create_merge_request` with its assignees and reviewers.

**GitLab** (personal, group and project access tokens):

| Operation | Calls | Scope | Role in the project |
|---|---|---|---|
| test | `GET /version`, `GET /user` | `read_api` | none |
| projects | `GET /projects?membership=true` | `read_api` | member |
| read | `GET /projects/:id/merge_requests`, `…/:iid`, `…/approvals`, `…/diffs` (or `…/changes`), `…/discussions` | `read_api` | Reporter (Guest cannot see the code of a private project) |
| comment | `POST …/notes`, `POST …/discussions` | `api` | Reporter (Planner and up can comment) |
| edit | `PUT /projects/:id/merge_requests/:iid` | `api` | Developer (or the merge request's author) |
| approve | `POST …/approve` | `api` | Developer by default; with approval rules (Premium, Ultimate), an eligible approver |
| create | `GET /users?username=`, `POST /projects/:id/merge_requests` (`assignee_ids`, `reviewer_ids`); on refusal `GET …/merge_requests?state=opened`, `GET …/repository/branches/:branch` | `api` | Developer (to push the source branch and set assignees and reviewers) |

- `read_api` is read-only: it covers test, projects and read; everything that
  writes needs `api`, which also grants all other API writes the user may do.
- A personal access token acts as you with your roles. Group and project
  access tokens act as a bot user, created with the token, that has the role
  chosen for the token (pick Reporter for reading, Developer to comment, edit,
  approve and create); a project token reaches only its project, a group token
  the group's projects. `read_user` (personal tokens only) covers just the
  token's user, not the rest of the API: use `read_api` or `api`.
- On GitLab.com, group and project access tokens need Premium or Ultimate; on
  GitLab Self-Managed and Dedicated they come with any license.
- GitLab drops assignees and reviewers it cannot add without an error (users
  outside the project; on the Free tier only one assignee is kept): harness
  reports them as warnings.

Sources: [token scopes](https://docs.gitlab.com/security/tokens/access_token_scopes/),
[project access tokens](https://docs.gitlab.com/user/project/settings/project_access_tokens/),
[group access tokens](https://docs.gitlab.com/user/group/settings/group_access_tokens/),
[roles and permissions](https://docs.gitlab.com/user/permissions/),
[merge request approvals](https://docs.gitlab.com/user/project/merge_requests/approvals/),
[merge requests API](https://docs.gitlab.com/api/merge_requests/).

**Forgejo** (and Gitea) access tokens: each scope is `read:` (GET only) or
`write:` (also POST, PATCH and DELETE, and includes read).

| Operation | Calls | Scopes |
|---|---|---|
| test | `GET /version`, `GET /user` | `read:user` |
| projects | `GET /repos/search`, `GET /user/repos` | `read:repository`, `read:user` |
| read | `GET /repos/{owner}/{repo}/pulls…` (pulls, reviews, review comments, `.diff`), `GET /repos/{owner}/{repo}/issues/{index}/comments` | `read:repository`, `read:issue` |
| comment | `POST /repos/…/issues/{index}/comments` (general), `POST /repos/…/pulls/{index}/reviews` (diff line) | `write:issue`, `write:repository` |
| edit | `PATCH /repos/…/pulls/{index}` | `write:repository` |
| approve | `POST /repos/…/pulls/{index}/reviews` | `write:repository` |
| create | `GET /users/{username}`, `POST /repos/…/pulls` (`assignees`), `POST /repos/…/pulls/{index}/requested_reviewers`; on refusal `GET /repos/…/pulls`, `GET /repos/…/branches/{branch}` | `write:repository`, `read:user` |

- So a read-only token is `read:user`, `read:repository` and `read:issue`; a
  full one is `read:user`, `write:repository` and `write:issue`.
  `read:organization` is not needed: harness reads repositories through the
  repository routes and requests reviews from users, not teams.
- The token's user needs read access to the repository to read and comment,
  and write access to edit others' pull requests, assign, request reviews and
  open pull requests from the repository's branches. Branch protection decides
  whose approvals count.
- Forgejo refuses the whole pull request when an assignee cannot be assigned;
  harness then opens it without assignees and reports them as a warning.

Source: [Forgejo token scopes](https://forgejo.org/docs/latest/user/token-scope/)
(Gitea uses the same scopes).

**GitHub fine-grained personal access tokens** (repository permissions; choose
the repositories, and the organization as resource owner for its
repositories):

| Operation | Calls | Permissions |
|---|---|---|
| test | `GET /meta`, `GET /user` | none |
| projects | `GET /user/repos`, `GET /search/repositories` | Metadata: read |
| read | `GET /repos/{owner}/{repo}`, `…/pulls`, `…/pulls/{n}` (also as diff), `…/pulls/{n}/reviews`, `…/pulls/{n}/comments`, `…/issues/{n}/comments` | Metadata: read, Pull requests: read, Contents: read (the diff) |
| comment | `POST …/issues/{n}/comments` (general), `POST …/pulls/{n}/comments` (diff line) | Pull requests: read and write |
| edit | `PATCH …/pulls/{n}` | Pull requests: read and write |
| approve | `POST …/pulls/{n}/reviews` | Pull requests: read and write |
| create | `GET /users/{username}`, `POST …/pulls`, `POST …/issues/{n}/assignees`, `POST …/pulls/{n}/requested_reviewers`; on refusal `GET …/pulls?head=&base=`, `GET …/branches/{branch}` | Pull requests: read and write, Contents: read; Issues: read and write is an alternative for the assignees |

- Read-only: Metadata, Contents and Pull requests read. To write, raise Pull
  requests to read and write. Metadata read is always part of a fine-grained
  token. GitHub accepts Pull requests or Issues permission for the issue
  routes of a pull request (general comments, assignees).
- An organization can require approval of fine-grained tokens that reach its
  repositories: until an owner approves it, the token is pending and reads
  only public resources (an owner's own token is approved at once). An
  organization can also block fine-grained tokens; then use a classic token.
  Outside collaborators of an organization's repositories must use classic
  tokens.
- Assignees need the token's user to have push access; GitHub ignores others
  without an error, and harness reports them as warnings. Reviewers must be
  collaborators.
- GitHub Enterprise Server has fine-grained tokens from 3.10 (a beta there);
  older versions take only classic tokens, and an enterprise or organization
  may require approval or block them as above.

**GitHub classic personal access tokens:** `repo` for private repositories
(everything above), or `public_repo` when every repository is public; no
scope at all reads only public information. A classic token reaches every
repository its user can; in an organization with SAML single sign-on,
authorize the token for it.

Sources: [permissions required for fine-grained tokens](https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens),
[managing personal access tokens](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens)
(and its [Enterprise Server 3.10 version](https://docs.github.com/en/enterprise-server@3.10/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens)),
[assignees API](https://docs.github.com/en/rest/issues/assignees),
[pull requests API](https://docs.github.com/en/rest/pulls/pulls),
[OAuth scopes](https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/scopes-for-oauth-apps).

## Updating harness

After replacing the binaries, run `harness migrate` once, from anywhere. It
brings the machine up to date first: the global `config.toml`, the user and
workspace knowledge databases, and the MCP entries and client hooks harness
installed for you. Then it brings every registered project up to date
(`harness projects list`), several at a time (`--jobs`). Each project gets
what `harness init` would do: its config and its database, migrated after a
backup. The harness section in AGENTS.md / CLAUDE.md, the Git and client hooks
and the MCP entries it already has are restated as this version writes them.
Finally every worktree that still exists is synced. Nothing is installed that
was not there. A project whose folder is gone is skipped, never forgotten, and
the summary says how to restore or forget it. `--dry-run` shows what would
change, and `--json` gives the result per project. You no longer need to run
`harness init` and `harness sync` in every project by hand.

## Text and file search

Harness searches the working tree itself whenever ripgrep (`rg`) and `fd`
are installed: agents get live text and file search with nothing to
configure, and no separate rg-fd-mcp server is needed. Install them once
per machine.

**Windows** (x64), with winget:

```powershell
winget install BurntSushi.ripgrep.MSVC
winget install sharkdp.fd
```

or with Scoop (`scoop install ripgrep fd`) or Chocolatey
(`choco install ripgrep fd`). winget puts the commands on `PATH` through
`%LOCALAPPDATA%\Microsoft\WinGet\Links`, which only new terminals see.

**Linux** (x64 and arm64):

| Distribution | Command |
|---|---|
| Debian, Ubuntu | `sudo apt install ripgrep fd-find` (fd is installed as `fdfind`; harness finds it) |
| Fedora, RHEL (EPEL) | `sudo dnf install ripgrep fd-find` |
| Arch | `sudo pacman -S ripgrep fd` |
| Alpine | `sudo apk add ripgrep fd` |

**macOS** (Apple silicon), with Homebrew:

```sh
brew install ripgrep fd
```

Anywhere with a Rust toolchain, `cargo install ripgrep fd-find` works too.
Check the install with `rg --version` and `fd --version` (`fdfind
--version` on Debian and Ubuntu), then run `harness doctor`: its `search`
line says which tools it found. Harness looks for the tools once per
process, so restart harness ui and your agent sessions (which start
`harness mcp`) after installing.

Without fd, file search still works through `rg --files`. Without either,
`harness doctor`, `harness report` and `provider_status` say so with the
command above. Searches honour `.gitignore` and `.ignore` as rg and fd do.
The reference (Providers) has the settings and how to turn it off.

## Projects with their own nuget.config

The nuget provider (`package_api`, NugetMcpServer started with `dnx`) uses
NuGet's own configuration, so a repository with a `nuget.config` that
clears nuget.org, maps packages to private feeds or needs credentials can
stop it from starting or from finding the project's packages. `harness
doctor` reports that as `nuget-config`, naming the file, with the project
override that fixes it:

```toml
# .harness/config.toml
[providers.nuget]
command = "dnx"
args = ["DimonSmart.NugetMcpServer@1.1.10", "--yes", "--source", "https://api.nuget.org/v3/index.json"]
dir = "project"
```

`--source` makes dnx fetch the server from nuget.org whatever the
repository's configuration says, and `dir = "project"` starts the server in
the repository, so it reads the repository's feeds. `harness doctor --fix
nuget-config` writes it for you (`--dry-run` first shows it), and so does
Fix on the nuget card in harness ui (Agents › Tools & providers) or in
Health, after showing the change. The project table replaces the global
one whole and is undone by removing it. Keep feed credentials in the NuGet
configuration or in environment variables, never in `config.toml`. The
reference (Providers › Projects with their own nuget.config) explains what
dnx and the server do, with more examples.

## Package upgrades and vulnerabilities

In a .NET repository, Architecture marks each NuGet package a project
references directly (in the Layers side panel's package list, and in a
workspace's Version drift table for the members' own packages):

- **ⓘ** (the info colour): a newer stable version is published, e.g.
  "Newer stable version 9.0.3 available (you use 8.0.1)". Pre-releases are
  never offered; a package on a pre-release is compared with the stable
  versions above it.
- **⚠** (gold for low and moderate, red for high and critical): the version
  in use falls into a known advisory's range. The tooltip names the
  severity, the advisory's link, the affected versions and the first fixed
  version when known.

Both marks can be reached with Tab and read by screen readers. Versions
managed centrally (`Directory.Packages.props`) count; a version harness
cannot resolve (`$(Property)`, `1.*`, an open range) gets no mark, and
transitive packages are not looked at (`dotnet list package --vulnerable
--include-transitive` covers those).

The data comes from the package sources your NuGet configuration resolves
to (nuget.config from the repository up, then the user-level NuGet.Config,
with `<clear/>`, disabled sources and package source mapping), nuget.org
when nothing else is configured. A private feed is read with the
credentials of its `packageSourceCredentials` (a `ClearTextPassword` may
name an environment variable, `%FEED_TOKEN%`; an encrypted `Password` is
read on Windows only). Answers are cached on this machine for a day, so the
view draws at once and the marks follow; offline, or when a source cannot
be read, the view stays as it is with a short note, and `harness doctor`
reports the source as `package-sources` with how to fix it.

A vulnerable package is also an architecture finding (`vulnerable-package`:
a warning for low and moderate, a problem for high and critical), with the
usual marks (by design, review). Turn on `outdated = true` under
`[architecture.packages]` to get an info finding per outdated package too,
or `enabled = false` to read no package source at all.

## When something seems off

- `harness doctor` checks the installation, the database, the clients'
  configuration and every provider, and says how to fix what it finds.
- `harness status` shows the project's state; harness ui shows the same
  per project, with the agents' tool calls and how well `get_context`
  answered. Health's Fix it runs a check's fix when harness can do it
  itself.
- `harness report` lists what you can change so harness serves your agents
  better (providers, hooks, instructions, knowledge), each with what to
  do.
- If agents do not use harness, check that CLAUDE.md (or AGENTS.md for
  Codex) holds or imports the harness section (`harness instructions`
  adds it) and that the session-start hook is installed
  (`harness hooks install --kind claude-code`).
