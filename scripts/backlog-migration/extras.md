<!--
Issues that were never backlog entries: the milestones' own, and what docs/roadmap.md kept as a
menu. Read by migrate.py after the 165 entries. One block per issue:

    ## Title
    <!-- id: slug | state: open | labels: a,b | milestone: v0.14.0 -->
    body

`{{slug}}` in a body becomes that block's issue number; `#N` is a backlog id, renumbered as in
the entries. Left out on purpose, because they shipped: OIDC for approvals (#110), approving from
the Teams card (#124), the MCP endpoint (#157), cheaper providers (v0.5.0), the repository's
description and topics, the Artifact Hub file, the chart's maintainers and screenshots.
-->

## v0.13.0 — what v0.12.0 left open
<!-- id: v013 | state: open | labels: | milestone: v0.13.0 -->
In release candidates since 2026-10-02. It is released when a candidate has run on the production
install; the write-up is in [`docs/roadmap-archive.md`](https://github.com/Flou21/hephaisto/blob/main/docs/roadmap-archive.md).

- [x] A watcher incident ends when its fault does (#158)
- [x] Pod logs from Loki first, and from a pod with a sidecar (#160)
- [x] Many incidents closed at once (#161)
- [x] An acknowledgement shows on the list (#165)
- [x] The Teams card closes, re-investigates, approves and denies (#124) — open for a real tenant (#125) and for an approval that runs
- [x] One metric name is one instrument (#15)
- [x] A code fix for a service that pins Cait gets as far as planning (#117) — open for the gate against a real service
- [x] A coder Job is three containers, and the model's holds no git or NuGet token (#116)
- [ ] A candidate has run on the production install
- [ ] Which credential the Jobs run on (#118), the owner's decision

## v0.14.0 — a GitHub issue is work Hephaisto can be handed
<!-- id: v014 | state: open | labels: | milestone: v0.14.0 -->
Until now the only way in is an alert. An issue assigned to Hephaisto's GitHub account becomes a
work item, and the Jobs that plan and implement a code fix for an incident do the same for it: a
plan, an approval, a draft pull request that closes the issue. The plan is posted on the issue
and approved there.

Decided by the owner on 2026-10-06: assignment to a bot account is the trigger; Hephaisto polls
GitHub, and a webhook comes later; approval happens on the issue; the roadmap and the backlog move
into GitHub issues.

- [ ] {{v014-standin}}
- [ ] {{v014-poller}}
- [ ] {{v014-workitem}}
- [ ] {{v014-approval}}
- [ ] {{v014-surfaces}}
- [ ] {{v014-live}}

Not in it, each its own issue: {{v014-webhook}}, {{v014-ci}}, {{v014-replan}}.

## A GitHub stand-in and an issues suite, red before any code
<!-- id: v014-standin | state: open | labels: area:measurement,size:M | milestone: v0.14.0 -->
No test reaches github.com: git is a server in the cluster, `gh` is a script, the model is a
script, and nothing stands in for the issue side.

**What to do.** `GitHubStandIn.cs` beside the Teams stand-in in `infra/e2e/notification-receiver/`:
the REST calls the agent will make (issues by assignee with an ETag, comments, a pull request),
and controls to create, assign and comment as a named account, merge a pull request, answer 500
or 403, and list what was asked. A Tilt toggle `github: off | stand-in | live`. A suite
`scripts/e2e/issues-local.sh` in the pager suite's shape, one file per scenario, all twelve on
`KNOWN_RED` for v0.14.0:

G01 an assigned issue is planned by one Job and the plan is a comment · G02 an unassigned issue
and a repository that is not listed are ignored · G03 `/approve` from an approver starts one
implementing Job, and the pull request says `Closes` · G04 `/approve` from anybody else changes
nothing · G05 `/reject` · G06 unassigning cancels the Job · G07 an issue body that gives orders
stays data, and an edit after the snapshot is not picked up · G08 an agent restart leaves one
attempt · G09 GitHub down or rate-limited is degraded, then recovers · G10 comments are capped ·
G11 a merged pull request ends the work item · G12 with the mode off or at plan, approving is
refused.

Also here: the version floor goes to 0.14.

Read in GitHub's documentation on 2026-10-06, for whoever creates the bot's tokens: a fine-grained
token cannot be used on a repository where its owner is a repository or outside collaborator. So
for a repository owned by another user the bot needs a classic token, and in an organisation it
has to be a member for a fine-grained one to work.

## Hephaisto asks GitHub which issues are assigned to it
<!-- id: v014-poller | state: open | labels: area:correctness,size:M | milestone: v0.14.0 -->
The agent has never talked to GitHub: only the coder Job does, and the agent holds no GitHub
credential.

**What to do.** A small typed client with a configurable base URL, no SDK. A Secret of the
agent's own (`secrets.github`), never the coder's. `GitHubIssuePoller`: every minute, per listed
repository, the open issues assigned to the bot, with an ETag. Each pass states what should be
true and makes it so - a new assignment is a work item, an unassignment or a closed issue is a
cancel - so a restart loses nothing and there is no queue to drain. A `work_items` table. GitHub
joins the dependencies `get_status` reports. Egress through the proxy that already allows
`api.github.com`. Turns G02 and G09 green.

## A code fix without an incident: a work item is planned, and the plan is posted on its issue
<!-- id: v014-workitem | state: open | labels: area:correctness,size:L | milestone: v0.14.0 -->
A code fix belongs to an incident at every layer: `code_fix_attempts.incident_id` is not nullable,
`CodeFixCoordinator` loads the incident in every method, the request to the Job requires an
incident and findings, and the prompts, the pull request's title and its body speak of one.

**What to do.** `CodeFixAttempt.IncidentId` nullable beside a `WorkItemId`, exactly one of them
set, one open attempt per work item. An entry into the coordinator for a work item; its collect,
cancel, expire and relaunch paths stop assuming an incident. Eligibility is the switches, the host
allowlist and the caps that exist, plus the repository being listed. Contract version 2 with a
`work_item` in place of `incident` and `findings`, in all three copies of the schema. The issue's
body is snapshotted when the work item is created and travels as untrusted text. Runner templates
for an issue; the title's type from the issue's type or label. One status comment edited in place
and one plan comment per attempt. Turns G01, G07 and G08 green.

## A plan is approved on the issue, and the pull request closes it
<!-- id: v014-approval | state: open | labels: area:correctness,size:M | milestone: v0.14.0 -->
**What to do.** `/approve` and `/reject <reason>` in a comment, read only from the accounts in
`github.approvers` (numeric ids, as the Teams approver map uses object ids). What is approved is
the plan in the database, never the text of a comment. `ApprovalSource.GitHub`; the console keeps
working. The implementing Job's pull request says `Closes owner/repo#n` and is linked from the
issue. The pull request is read afterwards: merged or closed ends the work item. Unassigning the
bot cancels. Turns G03 to G06 and G10 to G12 green.

## Work items in the console, the MCP endpoint and notifications
<!-- id: v014-surfaces | state: open | labels: area:pager,size:M | milestone: v0.14.0 -->
Every surface that lists a code fix links through its incident, and the only place a plan is shown
in full is the incident's page.

**What to do.** `/codefixes` and `list_code_fixes` show the issue where there is no incident; a
page per attempt with the plan and the decision; plan-ready and pull-request-opened notifications
for a work item; metrics and audit rows that do not need an incident id.

## The first run against real GitHub: a sandbox repository, the real gh, real tokens
<!-- id: v014-live | state: open | labels: area:measurement,size:M | milestone: v0.14.0 -->
`docs/verification.md` has said since v0.9.0 that a nightly tier runs the real `gh` against a
sandbox repository. No such tier exists, and no automated test has run `gh` against github.com.

**What to do.** `scripts/e2e/github-live.sh`, from the dev cluster: create an issue, assign it to
the bot, wait for the plan comment, `/approve`, wait for the draft pull request, check that GitHub
itself names the issue as closed by it, clean up. The model stays scripted. Run before each
candidate. Needs the owner: the bot account, its invitations, its tokens, a sandbox repository.

## A webhook that tells the poller to look now
<!-- id: v014-webhook | state: open | labels: idea,area:correctness,size:M | milestone: -->
Polling costs up to a minute. A webhook would carry no state of its own: a signed delivery names
a repository, and the poll for it runs at once. It needs a public route outside sign-in, a signing
secret and replay handling; the stand-in would sign deliveries as the Teams one signs clicks.

## The issues suite in GitHub Actions
<!-- id: v014-ci | state: open | labels: idea,area:measurement,size:M | milestone: -->
Neither the code-fix suite nor the issues suite runs in CI: the in-cluster git server is seeded
from two local checkouts, one of them private. An in-repo dev-context fixture would let both run
on kind beside the pager suite.

## Replanning from a comment on the issue
<!-- id: v014-replan | state: open | labels: idea,area:correctness,size:M | milestone: -->
`/approve` and `/reject` are the only things a person can say. "Not like that, do this instead"
would be a new attempt planned with the comment in hand.

## More notification channels: Slack, e-mail, PagerDuty or Opsgenie
<!-- id: idea-channels | state: open | labels: idea,area:pager | milestone: -->
From the roadmap's menu. A channel is a `Name`, a `Describe()` and a `SendAsync` returning a
`DeliveryResult`, with the outbox, routing, retry and rate limiting already behind it. Slack's
incoming webhooks are the cheapest of the three. SMS and voice are #143.

## Change correlation: "this started four minutes after the rollout of x"
<!-- id: idea-change | state: open | labels: idea,area:correctness | milestone: -->
From the roadmap's menu.

## Postmortem generation
<!-- id: idea-postmortem | state: open | labels: idea,area:pager | milestone: -->
From the roadmap's menu: drawing on the digest index for "this has happened N times".

## Leading indicators
<!-- id: idea-leading | state: open | labels: idea,area:correctness | milestone: -->
From the roadmap's menu: PVC fill projection, memory trending to its limit, certificate expiry, an
HPA pinned at its maximum.

## Widen autonomy to rollout_restart and rollback_deployment, and to more namespaces
<!-- id: idea-autonomy | state: open | labels: idea,area:correctness | milestone: -->
From the roadmap's menu. See #39 for the action types the executor refuses today.

## Alert-noise reduction: find rules that flap, propose the change as a pull request
<!-- id: idea-noise | state: open | labels: idea,area:pager | milestone: -->
From the roadmap's menu. The only other idea there whose output is a pull request.

## Topology and blast-radius reasoning from the service graph
<!-- id: idea-topology | state: open | labels: idea,area:correctness | milestone: -->
From the roadmap's menu.

## Chaos self-testing, natural-language history queries, Pyroscope, more than one cluster
<!-- id: idea-misc | state: open | labels: idea | milestone: -->
One line of the roadmap's menu, kept as one issue until any of the four is wanted.

## A stronger model for production investigations
<!-- id: idea-model | state: open | labels: idea,area:config | milestone: -->
From the roadmap's menu: Gemini Flash is solid, and a production install wants a model of the
class of Opus. A provider is `Llm:Endpoint`, `Llm:Model` and a price entry, not code. See #164 for
the model the subscription pays for.

## CI quality gates: formatting, coverage, CodeQL, an SBOM, image scanning
<!-- id: idea-ci-gates | state: open | labels: idea,area:chart | milestone: -->
From the roadmap's project track. There is no `.editorconfig`, no `dotnet format`, no coverage,
no spell check or markdown lint, no CodeQL, no SBOM and no image scanning; build provenance is the
only supply-chain signal. The dead-link check exists since the docs site.

## A code of conduct and a support file
<!-- id: idea-community | state: open | labels: idea,area:docs,size:S | milestone: -->
From the roadmap's project track. `CONTRIBUTING.md`, `SECURITY.md`, the templates and `CODEOWNERS`
exist; `CODE_OF_CONDUCT.md` and `SUPPORT.md` do not.
