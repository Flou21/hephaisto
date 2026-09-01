---
name: Bug report
about: Something behaves differently from what is documented
labels: bug
---

**What happened, and what you expected instead.**

**Version.** The image tag or chart version, and `GET /api/version` if the agent is running.

**Mode.** `Off`, `Observe`, `DryRun` or `Auto` — and which kill-switch arm was binding, from
`/status`.

**Provider and model.** `Llm:Provider`, and the model id.

**Is it already in [docs/backlog.md](../../docs/backlog.md)?** Several behaviours that look like
bugs are deliberate limitations with the reasoning written down. Linking the entry you checked is
useful either way.

**Logs, if you have them.** Please redact: tool *results* are not redacted by the agent, so pod
descriptions and log excerpts can carry environment variables and hostnames verbatim.
