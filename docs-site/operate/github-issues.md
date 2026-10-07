# GitHub issues as work

Until v0.14.0 the only thing Hephaisto could be handed was an alert. With `github.enabled`, a
GitHub issue **assigned to Hephaisto's account** is work too: the Job that plans a code fix for an
escalated incident plans one for the issue, the plan is posted on the issue, an approver answers
it there, and the pull request that follows closes the issue.

**It ships off.** Without `github.enabled` the agent holds no GitHub credential and asks GitHub
nothing.

## What it does, and what it does not

| It does | It does not |
|---|---|
| Ask GitHub, every `github.pollInterval`, which open issues are assigned to its account in the repositories you listed | Take an issue from a repository that is not listed, however it is assigned |
| Plan a taken issue with a read-only coder Job, under `codeFix.mode` and every `codeFix` cap | Investigate anything: there is no incident, no alert and no running image behind an issue |
| Post the plan on the issue, and say there how to answer it | Act on a comment's text. What is approved is the plan as Hephaisto stored it |
| After an approval, push **one** branch and open a **draft** pull request whose description says `Closes owner/repo#n` | Merge, review, deploy, or open anything but a draft |
| Follow that pull request until it is merged (the work item is done) or closed | Plan the same issue twice. To have it planned again, hand it over again |
| Stop — and delete a running Job — when the account is unassigned or the issue is closed | Read an edit of the issue made after it was taken: the text travels as it was then |

Hephaisto only asks. Nothing has to reach in, and there is no webhook to expose.

## The bot account

An **ordinary GitHub account** — a machine user — and not a GitHub App: an App cannot be an
assignee. Give it its own e-mail address and two-factor authentication.

- Make it a **member of the organisation** that owns the repositories.
- Give it **write access** to each repository it may work in. That is what makes it assignable,
  and what lets its token push a branch. GitHub answers an assignment it ignores with success, so
  an account that is not assignable looks exactly like one nobody assigned.

Pull requests are then opened by the bot, so a person can review them as themselves.

## Two tokens, and they must stay two

Signed in as the bot, create two **fine-grained** personal access tokens. For both: **resource
owner = the organisation**, and repository access limited to **the repositories you list** — not
"all repositories".

| Token | Repository permissions | Who holds it |
|---|---|---|
| The **agent's** | Issues: read and write · Pull requests: read | the agent's pod |
| The **coder's** | Contents: read and write · Pull requests: read and write | the `prepare` and `publish` containers of a coder Job — never the container the model runs in |

Metadata: read is added by GitHub to both.

**The agent's token must not be able to push.** The agent reads every issue anybody can open; the
process that does that should hold nothing that writes to a branch. The chart refuses one Secret
name for both tokens, for that reason.

These permissions were measured against github.com, not read from its documentation: the agent's
token is also enough to ask whose token it is and what a repository's default branch is; the
coder's clones, pushes the one branch, opens the draft pull request and assigns it.

::: warning A repository a user owns
A fine-grained token only reaches repositories owned by its own account or by an organisation it
belongs to. For a repository owned by a *user* the bot is a collaborator, and that needs a classic
token (`repo`) — which reaches every repository the bot can see. Prefer an organisation.
:::

### The two Secrets

```sh
# The agent's, in the release namespace.
kubectl -n hephaisto create secret generic hephaisto-github \
  --from-literal=GITHUB_TOKEN=github_pat_...

# The coder's, in the coder namespace: the Secret code fixes already use. The agent cannot read it.
kubectl -n hephaisto-coder create secret generic hephaisto-codefix \
  --from-literal=GITHUB_TOKEN=github_pat_... \
  --from-literal=CLAUDE_CODE_OAUTH_TOKEN=...
```

## Chart values

```yaml
github:
  enabled: true
  issues:
    # THE AUTHORIZATION LIST. An issue anywhere else is never asked about.
    repositories:
      - example/shop
  # Who may answer a plan ON THE ISSUE, by account NUMBER.
  approvers:
    - 1234567
  # pollInterval: "00:01:00"   # GitHub's own advice for a poller
  # botLogin: ""               # empty: whoever the token belongs to
  # apiBaseUrl: https://api.github.com
  # useEgressProxy: true       # through codeFix.egressProxy, when that is rendered

secrets:
  github: hephaisto-github     # key GITHUB_TOKEN; never the same name as secrets.codeFix

codeFix:
  enabled: true                # an issue is planned by a coder Job
  mode: "plan"                 # "off" | "plan" | "pr" - see below. Quote it: YAML reads a bare off as false
```

Every key is in the [Helm values](/reference/helm-values) reference with the reason for it.

### Approvers are numbers

`github.approvers` holds account **ids**, not logins: a login can be given up and registered by
somebody else, a number cannot. A login in that list is refused at render and at start.

```sh
gh api users/<login> --jq .id
```

**Whoever is listed can make Hephaisto push a branch** to every listed repository. List
maintainers, not everybody who may comment.

With the list empty, no comment is read at all; the plan says that it is answered in the console.

## Answering a plan

On the issue, an approver writes a comment whose **first line** is exactly

```text
/approve
```

or

```text
/reject the null is the caller's to handle
```

Nothing else is a command — not `/approve please`, not `LGTM /approve`, not a quotation or a
code block of either, not `/Approve`. Strict on purpose: a comment wrongly read as an approval
pushes a branch. The first such comment after the plan decides, once; a comment that was read is
never read again, whatever it is edited into.

The same plan can be answered in the **console**: *work items* lists what was taken, and each
attempt has a page — `/codefixes/<attempt id>` — with the plan in full, its history, and approve
and deny for whoever holds the approver role. Or through the API,
`POST /api/workitems/{id}/codefix/{attemptId}/approve` and `.../deny`.

## What Hephaisto writes on an issue

- **One status comment**, edited in place as the work moves: planning, plan ready, implementing,
  the pull request, how it ended.
- **One comment with the plan** — summary, what will change, files, steps, verification — and how
  to answer it. Never edited.
- **At most one answer** to people who are not approvers, per plan, however many of them write
  `/approve`: it names the first in a code span, mentions nobody and does not say who the
  approvers are.
- **At most one answer per cause** when an approver's `/approve` is refused — the mode is `plan`
  or `off`, the emergency stop is engaged, the plan needs a change in a second repository.

**Never more than six comments** for one issue it was handed, whatever anybody writes there.
Beyond that it only edits its status comment.

Nothing of the issue's own text is repeated in a comment, and what a model wrote is posted with a
zero-width space wherever GitHub would act on it — after `@`, `#` and `GH-`, inside `://`, after
every `/` before a digit — so that it mentions nobody and closes nothing. A path or an address
copied out of a plan comment or a pull request's description carries that character.

## The mode decides what an issue gets

`codeFix.mode` is the same switch incidents' code fixes obey, and the agent's kill switch
overrides it.

| `codeFix.mode` | A taken issue | `/approve` |
|---|---|---|
| `off` | No Job starts. The status comment says so, and the issue is asked about again by itself once the mode changes | refused, in one sentence |
| `plan` | A read-only Job writes a plan; it is posted on the issue | refused, in one sentence. The plan is not used up: after the mode is `pr`, a **new** `/approve` is taken |
| `pr` | As `plan` | starts the implementing Job: one branch, one draft pull request |

Setting the mode to `off` cancels every open attempt, as it does for incidents.

## Two things the context repository has to say

Code fixes read a context repository (`codeFix.contextRepository`, the one with `repos.yaml`).
For an issue's repository:

- **`repos.yaml` must enable it** — its entry with `coderEnabled: true`, and its build and test
  commands. The chart's list is the operator's authorization and this is the runner's own
  opt-in; a repository needs both. For one that is listed only in the chart the Job refuses
  before any model runs, and the attempt fails with `repository not enabled in dev-context
  repos.yaml`.
- **The label named in `defaults.pr.labels` must exist in the repository.** `gh` refuses a label
  that does not; the pull request is then opened without it and the attempt's deviations say so.

## Who else is told

A work item's code fix announces the three moments an incident's does — `CodeFixPlanReady`,
`CodeFixPrOpened`, `CodeFixFailed` — through the [notification](/operate/notifications) outbox.
The message names the issue by its reference and links the attempt's page.

There is no incident behind it, so there is no severity, namespace, cluster, kind or label to
route by. **Give work items a route of their own**: one without `namespaces`, `clusters`, `kinds`
or `matchers`, and without a `minSeverity` above `Info`.

```yaml
notifications:
  routes:
    - channel: teamsBot
      events: [CodeFixPlanReady, CodeFixPrOpened, CodeFixFailed]
```

A route scoped to a namespace or a label does not match a work item, and neither does a fallback
route — that one is for incidents nobody else owns. The Teams board stays a board of incidents.

## Seeing it work

```sh
curl -s https://hephaisto.example.com/api/status | jq '.connections[] | select(.name == "github")'
curl -s 'https://hephaisto.example.com/api/workitems?state=any' | jq '.[] | {repository, number, state, stateReason}'
```

`github` is `Healthy` while every listed repository answers, and `Degraded` with one line of why
— a refused token, a rate limit and until when, a 5xx. The row follows the poller by up to a
minute. An MCP client asks the same with `list_work_items` and `get_work_item`.

Four counters: `hephaisto.github.polls` by outcome, `hephaisto.workitems.taken`,
`hephaisto.workitems.closed` by state and reason, and `hephaisto.workitems.commands` by verb and
by what was done with the command (`accepted`, `not_approver`, `refused:<cause>`).

## Handing an issue back

Unassign the account, or close the issue: the work item is cancelled, a running Job is deleted,
and nothing is pushed afterwards.

An issue whose pull request was merged or closed is **not started over** while it simply stays
assigned. Unassign the account and assign it again — or close and reopen the issue — to hand it
over once more; that is a new work item, with the issue's text as it is then.

## Limits, and what is not built

- **Polling only.** A new assignment is seen within `github.pollInterval`. A webhook that tells
  the poller to look now is [not built](https://github.com/Flou21/hephaisto/issues/250).
- **No replanning from a comment.** A plan cannot be asked to change; reject it, edit the issue
  and hand it over again. [Not built](https://github.com/Flou21/hephaisto/issues/252).
- **"Merged is done" was never run against github.com.** The test that runs against GitHub
  itself never merges, so that a merge ends the work item is held against a stand-in only.
- **No real model has planned an issue in a test.** Every automated run used a scripted model;
  what a model makes of an issue's text is yours to watch on the first few.
- **One attempt per work item.** A failed, denied or expired plan is not followed by another.
- **A commit message is not made inert.** The implementing prompt forbids closing keywords
  there; nothing checks it.
