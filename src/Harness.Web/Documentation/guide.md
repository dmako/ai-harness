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
  (codebase-memory, graphify for C#, ripwire, rg-fd) merged into one
  answer. `search_project`, `package_api` and `architecture_findings` drill
  down.
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
ui (Work) or with `harness tasks list`.

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

Until an agent claims a task, you can restate it in harness ui: Work ›
Tasks, the pencil in the task's row (disabled once an agent claims it)
opens the same form as New task; the id, validation and notes stay. A task
that is also in the backlog file is restated from the file again at its
next import. Drag a task onto another blueprint's group to move it there;
the move is in its audit trail.

To delete a task, use the bin at the end of its row: a dialog says what
goes (the task, its criteria, its dependencies and its audit trail) before
anything does. Deleting is refused while an agent holds the task or while
other tasks depend on it, naming them. Only harness ui deletes a task; no
command or agent tool does.

Blueprints are kept in harness ui too: Work › Blueprints › New blueprint
starts with the next free id (M17 after M16, or BP-001); the pencil on a
blueprint's card changes its name, goal, exit criteria and notes, not its
id. The bin removes it after you confirm; for a blueprint with tasks it
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
  Layers, Complexity (every function by cyclomatic complexity, the most
  complex named) and Change coupling.
- **Knowledge:** the memories by type, what applies to a path, and the
  Inbox of proposals to approve.
- **Work:** Blueprints, Tasks and the Inbox of proposed tasks, blueprints
  and architecture work.
- **Agents:** how harness serves your agents and what you can change so it
  serves them better.
- **Health:** the checks `harness doctor` runs; a check harness can fix
  itself has Fix it, and a stale index has Sync now.

A workspace has the same views over its members. Machine holds this page's
settings, the C# sessions, and the switch for the opt-in developer
telemetry. How harness itself performs (relevance per provider, latency)
is for the harness developer: it is not shown here, only sent in that
telemetry if you turn it on.

## Several checkouts of the same code

- **Worktrees** (`git worktree add`) of one repository share one project:
  one `.harness/` database, one memory, one task queue. This is the way to
  work on several branches at once.
- **Separate clones** are separate projects, each with its own `.harness/`.
  Their knowledge does not mix. To share knowledge between related
  repositories (a service and its library), put them in a workspace
  (`harness workspace`): workspace-level knowledge applies to all members,
  and searches can span them.

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
