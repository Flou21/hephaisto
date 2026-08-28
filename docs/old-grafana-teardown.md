# Decommissioning the pre-Hephaisto Grafana

A record of what was removed on 2026-08-28, and the evidence that nothing was lost with it.

> **On the name.** The project was called *Watchtower* at the time and was renamed to
> *Hephaisto* shortly afterwards, because `watchtower` collides with containrrr/watchtower.
> `watchtower-obs` below is the namespace that existed then; today it is `hephaisto-obs`.

## What was removed

`helm uninstall prometheus -n prometheus` removed a hand-installed kube-prometheus-stack
(81.1.0) from `studio-rancher-desktop`. Grafana stores everything in a SQLite database on its
PVC, so anything made by hand exists **only** there and `helm uninstall` destroys it in
silence. That is the risk this teardown had to answer for.

## Why nothing was lost

The database was audited before the uninstall, not after:

| Check | Result |
|---|---|
| Dashboards total | 22 |
| Of those, provisioned by the chart | **22** |
| Dashboards edited after creation (`updated != created`) | **0** |
| Dashboards with more than one `dashboard_version` row | **0** |
| Grafana-managed alert rules | **0** |
| Annotations | **0** |
| Datasources | 2, both provisioned at install |

Every dashboard carried the identical creation timestamp `2026-08-10 10:19:33` — the moment
the chart installed them. Reinstalling the same chart reproduces all 22 exactly, so the
backup taken at the time was insurance rather than a migration input, and it was never
needed.

The one genuinely hand-made object was the **`grafana-mcp` service account**
(`sa-1-grafana-mcp`) and its single API key. It was deliberately not carried over: the
Hephaisto stack mints its own token into `grafana-mcp-caller-token`, and `litellm-config` was
repointed at that.

## Where the backup went

A byte copy of `grafana.db` and the 22 dashboards as individual JSON were committed to this
repository under `backup/old-grafana/` and later **purged from git history**, before the repo
was made public.

That directory could not stay. `grafana.db` is a complete Grafana database — it carries the
admin password hash, API key hashes, service accounts and the previous cluster's internal
datasource URLs. Hashes rather than plaintext, but it is another system's credential store
and it has no place in a public repository's history. The 22 dashboards beside it were
redistributed upstream Apache-2.0 files carried without attribution, so removing the
directory resolved that too.

The audit above is the part worth keeping, and it is kept here. If the bytes are ever wanted
they exist only in the local pre-purge tarball in `~`, not in this repository and not in any
clone of it.
