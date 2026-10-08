# HTTP surface

| Route | What it does |
|---|---|
| `POST /webhooks/alertmanager` | Alertmanager receiver |
| `POST /webhooks/watchdog` | Dead-man's-switch receiver |
| `GET /api/incidents`, `/{id}` | List and read incidents |
| `GET /api/incidents/search?q=` | Semantic search over incidents |
| `POST /api/incidents/{id}/reinvestigate` | Re-drive an incident's investigation |
| `POST /api/incidents/{id}/feedback` | Mark a diagnosis right or wrong |
| `GET /api/codefixes`, `/{attemptId}` | Code-fix attempts, for incidents and for issues, each with the planner's `questions` beside its `notes`; one attempt with what it is for, and for an issue every attempt of its work item |
| `GET /api/workitems`, `/{id}` | [GitHub issues taken as work](/operate/github-issues): `?state=Taken\|Done\|Cancelled\|any` |
| `POST /api/workitems/{id}/codefix/{attemptId}/approve`, `/deny` | Decide on the plan for an issue (approver policy) |
| `GET /api/status` | Mode, budgets, kill-switch arms |
| `GET /api/version` | The running version and commit; touches no database |
| `GET /healthz`, `/readyz`, `/metrics` | Health and Prometheus metrics |
| `/` | Blazor Server UI |

This table is **unchanged since v0.3.0**, which is worth stating rather than leaving implied: that
milestone added outbound delivery and no new inbound route, and none has been added since.

That is the property that makes linking out of a Teams card cheap and approving inside one
expensive — see [Notifications](/operate/notifications#why-the-card-carries-a-link).

## The webhook takes a bearer token

::: danger Without a token, the NetworkPolicy is the authentication
`POST /webhooks/alertmanager` and `/webhooks/watchdog` are outside OIDC - Alertmanager cannot
sign in. With `secrets.webhookToken` set they require `Authorization: Bearer <token>` and answer
anything else with 401; Alertmanager sends it with `http_config.authorization.credentials_file`.
Without one they accept unauthenticated calls, and the NetworkPolicy is their **entire**
protection.

Anything that can reach the port can forge an alert to an incident-creating endpoint. Every CIDR
you add to `networkPolicy.extraIngressCIDRs` widens that. If you deploy this, get that policy
right — and verify it rather than assuming it.
:::

`GET /api/version` deliberately touches no database, so it stays answerable when Postgres is down —
which is exactly when you want to know what is running.
