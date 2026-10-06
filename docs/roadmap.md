# Hephaisto roadmap

**What is planned lives on GitHub since 2026-10-06**: a
[milestone](https://github.com/Flou21/hephaisto/milestones) per release, an issue per piece of
work, and one tracking issue per open milestone. This page says where the project stands and
what the next milestone is, written against what is **actually in the repo**: where the plan and
the code disagree, this file follows the code.

Its companions:

- [Issues](https://github.com/Flou21/hephaisto/issues) — everything known to be broken or
  wanted, with the evidence for each. Until 2026-10-06 that was [`backlog.md`](backlog.md), which
  is frozen and kept because its numbers are cited across the code; the links into it below are
  to that record.
- [`roadmap-archive.md`](roadmap-archive.md) — the milestones that are released, as written.
- [`history.md`](history.md) — what is already done, and what was learned doing it.

---

## Where it stands

`v0.13.0` is in release candidates since 2026-10-02. **What v0.12.0 left open**: the three
items that release carried over and one production found the same day, all four built and green
on the dev cluster. See [v0.13.0](#v0130--what-v0120-left-open). It is not released until a
candidate has run on the production install.

**It grew on 2026-10-06, by the owner's choice of what to do before the full release.** The
fifth candidate adds the rest of the Teams buttons ([#124](backlog.md#124)), one registration
per metric ([#15](backlog.md#15)), and a fix for the one thing production found that day: every
code fix it had started died looking for Cait's pinned commit ([#117](backlog.md#117)). The
sixth carries the last thing chosen with them: the coder's agent and its driver run in separate
containers ([#116](backlog.md#116)), so the container the model runs in is handed no GitHub and
no NuGet token. It changes the pod of every code-fix and every investigation Job, and needs the
coder image of the same version.

Everything released is written up in [`roadmap-archive.md`](roadmap-archive.md): each milestone
from v0.1.0 to v0.12.0 as it was planned and as it turned out, the release-by-release record
that used to stand here, and the menu of later ideas, which are issues now.

---

## v0.13.0 — What v0.12.0 left open

v0.12.0 was released to draw a line, with three of its five items open. This is those three,
and a fourth that production found on the day of the release.

### What ships

| # | Item | Backlog | State |
|---|---|---|---|
| F1 | **A watcher incident ends when its fault does.** The watcher asks the database what it has open and reports a workload that has run cleanly for `incidents.healedAfter` as resolved; triage closes it as `hephaisto/watcher` | [#158](backlog.md#158) | built |
| F3 | **Pod logs from Loki first, and from a pod with a sidecar.** `grafanaMcp.podLogSelector` sends the model to Loki; `get_pod_logs` picks the application container when none is named; a grounding rejection says why in the log | [#160](backlog.md#160) | built |
| F4 | **Many at once.** `close_incidents` and the console's list close by filter: a dry run that counts, then a close that names the count, approver only, one audit row per incident | [#161](backlog.md#161) | built |
| - | **The incident list says who acknowledged a row** | [#165](backlog.md#165) | built |
| - | **The Teams card closes, re-investigates, approves and denies.** Reinvestigate for any member of the team; close for the Microsoft Entra object ids in `notifications.teamsBot.actions.approvers`, with a reason typed into the card; approve and deny for the same people, behind `actions.approvals.enabled`, which is off | [#124](backlog.md#124) | built, rc5 |
| - | **One metric name is one instrument.** Six names were registered twice; three counters read double | [#15](backlog.md#15) | built, rc5 |
| - | **A code fix for a service that pins Cait gets as far as planning.** The project file's versions are fetched in one request, not 974 | [#117](backlog.md#117) | built, rc5 |
| - | **A coder Job is three containers.** `prepare` clones with the GitHub and NuGet tokens before the model exists, `coder` runs the model, the build and the tests with the model's credential only, `publish` pushes and opens the pull request without running anything from the workspace | [#116](backlog.md#116) | built, rc6 |

F3 is wider than v0.12.0 planned it. The owner decided on 2026-10-02 that pod logs should come
from Loki first and the Kubernetes API second, not only that the Kubernetes API should cope
with a sidecar.

### Done when

v0.12.0's own sentences for F1, F3 and F4, each now a test:

- A pod the watcher opened an incident for, healthy again, closes that incident without anybody
  touching it: pager scenario P49.
- An investigation of a pod with a sidecar reads the application container's logs: P33 and P41,
  whose fixture pod has a mesh proxy listed first.
- A dry run of a bulk close names how many incidents it would close and which; the close refuses
  a reader and writes one audit row per incident: P50.

And one this milestone adds, which no test holds yet: with `grafanaMcp.podLogSelector` set, a
real model reads a crash-looping pod's logs from Loki. The prompt says so and a unit test holds
the prompt; what a model does with it is measured on the production install.

What the fifth candidate added, each a test where it can be one:

- A click re-investigates as the person the roster names; an approver's click closes with the
  reason typed into the card and writes one audit row; a member who is not an approver is told
  why and changes nothing; approve and deny answer only an approver: pager scenarios P51 to P54,
  against the Teams stand-in.
- No metric name is registered in two places: `OneRegistrationPerMetricTests`.
- The pinned Cait commit is found in a blobless clone without a request per version: three tests
  in `coder/test/workspace.test.ts`.

What the sixth candidate added:

- A process in the container the model runs in holds no git or NuGet token and sees no process
  of another container, with a token in the Secret for the probe to find; only `prepare` and
  `publish` are handed one; a fix is still planned, built, tested, pushed and opened as a draft
  pull request: the code-fix suite's c15 and the investigation suite's I12, on the dev cluster.
- Each role refuses to start beside a credential that is not its own, and `publish` pushes what
  it re-derived from a bundle, never what the workspace says: `coder/test/roles.test.ts`.
- An investigator Job is told the workload and not the pod when an alert opened the incident:
  a unit test, and the investigation suite's I4 and I11, which were red on exactly that.

And what no test holds about the split: a real GitHub token has not been in such a pod, since
the dev cluster's git server takes none; and with `codeFix.nugetCache.enabled` the cache is
read-only in the model's container, so a fix that adds a package cannot be built there. The
first code fix production runs on this candidate is the measurement of the first.

And three that no test holds from the fifth. **An approval that runs**: the pager suite's model plans nothing,
so no incident there waits for approval, and the click that approves one is unit-tested only. It
needs one click on a waiting action before `approvals.enabled` goes on anywhere that matters.
**Any of the buttons in a real tenant** ([#125](backlog.md#125)). And **Cait itself, from a coder
pod, through the egress proxy**: the next plan production starts is that measurement.

### What is explicitly not in v0.13.0

- **A Job or a deleted workload healing.** Those end by the sweeper, which now has chart values
  and is still off by default.
- **An HTTP route for the bulk close.** MCP and the console have it; nothing asked for a third.
- **Pod labels on OTLP-shipped logs.** On the production install those carry `service_name`
  only, so a pod selector finds what promtail ships. That is the shipper's to fix.
- **A gate that runs a code fix against a real service** ([#117](backlog.md#117)). The failure
  production found is fixed; the gate that would have found it first is not built.
- **Which credential the Jobs run on** ([#118](backlog.md#118)). The documentation is read and
  quoted there; the choice between the subscription token and an API key is the owner's.

---

## Next — a more direct way to talk to Hephaisto

**v0.14.0: a GitHub issue is work Hephaisto can be handed.** Decided by the owner on 2026-10-06.
Until now the only way in is an alert. An issue assigned to Hephaisto's GitHub account becomes a
work item, and the Jobs that plan and implement a code fix for an incident do the same for it:
a plan, an approval, a draft pull request that closes the issue. The plan is posted on the issue
and approved there, which is the direct way to talk to it that this section promised.

What was decided, each by the owner:

- **Assignment is the trigger.** An issue is taken when it is assigned to the bot account, in a
  repository the install lists. The account is an ordinary GitHub account: an App cannot be an
  assignee.
- **Hephaisto asks GitHub; GitHub does not call Hephaisto.** It polls. A webhook comes later and
  will only tell the same loop to look now.
- **Approval happens on the issue**, by an account on an approver list, and still works in the
  console.
- **This file and [`backlog.md`](backlog.md) moved into GitHub issues**, all of it, so the
  project's own backlog is the first thing it can be handed. Done on 2026-10-06: 165 entries and
  22 items from this file, 187 issues.

What the code says about it, so the building starts from there:

- **A code fix belongs to an incident at every layer.** `code_fix_attempts.incident_id` is not
  nullable, the coordinator loads the incident in every method, the request to the Job requires
  an incident and findings, and the prompts, the pull request's title and its body speak of one.
- **The agent has never talked to GitHub.** Only the coder Job does, with `git` and `gh`; the
  agent holds no GitHub credential, and nothing reads a pull request after it is opened.
- **No test reaches github.com.** Git is a server in the cluster, `gh` is a script, the model is
  a script. That stays the everyday tier, with a GitHub stand-in beside the Teams one; a second
  tier runs the real `gh` and a real token against a sandbox repository before a candidate, which
  [`verification.md`](verification.md) has claimed since v0.9.0 and nothing did.

It gets its "Done when" as scenarios before it gets code, as v0.10.0 and v0.11.0 did. The
milestone is [#243](https://github.com/Flou21/hephaisto/issues/243), and its six stages are the issues it lists.

---

## Not planned for now

Both were numbered milestones until 2026-09-29 and the next, high-priority items after that.
Decided by the owner on 2026-10-06: neither is planned for now. Their backlog entries stay open,
because what they describe is still true.

### SMS and voice through Twilio

**Not planned for now** (2026-10-06): the owner chose a more direct way to communicate with
Hephaisto as the next thing instead. What follows is the design as it stood, kept so it is not
argued twice.

Decided on 2026-09-28: v0.10.0 reaches people through Teams alone. The service being replaced
also sends an SMS and places a call when nobody answers, and that is the one thing v0.10.0 does
not replace ([#143](backlog.md#143)).

- **SMS and voice through Twilio**, as named HTTP channels rather than one more hard-coded
  channel: a channel is a URL template, a body template and a credential, fanned out per
  recipient, with Twilio as the shipped preset.
- **Escalation steps may name it.** The steps of v0.10.0 already take a channel.
- **Still to decide: what rings a phone that is set to silent.** A phone treats an SMS and a
  call like any other, and keeps them quiet in Do Not Disturb unless each person makes an
  exception for the number. A notification that breaks through needs an app holding the
  platform's permission for it, which is a channel of a different kind.
- **Done when** an unacknowledged critical incident on the release harness reaches a stand-in for
  Twilio's API once per recipient, and an acknowledged one does not.

### Install ergonomics

**Not planned for now** (2026-10-06): it comes later, other things come first.

The getting-started guide and the rest of [#108](backlog.md#108). Renumbered four times between
2026-09-27 and 2026-09-29, then moved out of the numbered milestones on 2026-09-29.
Scope unchanged: installing Hephaisto should not take a day of reading the chart's source.

---

## Standing constraints

- **The cluster is a single shared resource.** Code and unit tests parallelise; cluster verification
  does not.
- **Never `tilt down`** — it `helm uninstall`s the stack and takes Grafana's PVC with it.
- **Approval identity is attribution, not authentication** until OIDC lands. The risk to watch is
  habituation.
- **No audit, no action.** If Postgres is unreachable the executor must refuse.
- **Promote autonomy per action type, never globally.**
- **Config needs a reader in `src/` in the same commit.** Config that reads like configuration and
  behaves like a comment is worse than no documentation — see
  [backlog #19](backlog.md#19-maxautoscalereplicas-and-maxautoscalestep-have-no-readers) for the two
  that got through.
