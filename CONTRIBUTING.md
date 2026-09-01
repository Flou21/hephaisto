# Contributing

This repository has a few conventions that are load-bearing rather than stylistic. They are
short, and they are most of what a first pull request needs to know.

## The rules that are not preferences

**Run `./scripts/test.sh`, never `dotnet test`.** The wrapper exists because the bare command
misreports on this toolchain. There is a floor on the count: a change that removes tests without
saying why fails the build.

**Configuration needs a reader in `src/` in the same commit.** A setting that reads like
configuration and behaves like a comment is worse than no documentation. Two got through once and
are written up as [backlog #19](docs/backlog.md).

**Autonomy is promoted one action type at a time, never globally.** If a change makes it possible
to grant more than one at once, that is the change being reviewed, not a detail of it.

**No audit, no action.** If the audit path cannot be written, the executor must refuse. Anything
that weakens that is a design discussion before it is a patch.

**A backlog item leaves `docs/backlog.md` by being fixed, or by being reclassified as a deliberate
limitation and written down somewhere permanent. It does not leave by being ignored.**

## The documents, and which one you want

- [`docs/architecture.md`](docs/architecture.md) — how the pipeline fits together. Start here.
- [`docs/backlog.md`](docs/backlog.md) — everything known to be broken, with evidence. Check
  before reporting; the entry probably exists and probably explains why.
- [`docs/roadmap.md`](docs/roadmap.md) — what is planned, written against what is in the repo.
  Where the two disagree, the file follows the code.
- [`docs/history.md`](docs/history.md) — what was learned, including the wrong turns.
- [`docs/verification.md`](docs/verification.md) — how claims here are measured.
- [`docs/design.md`](docs/design.md) — the token set and the rules for the console's appearance.

## Commits

One commit per change, with a message that says **why**. The repository's history is used as an
explanation of itself, so a message that restates the diff has done nothing the diff did not
already do. Where a fix has a backlog entry, the fix and the entry go in the same commit.

## Before you open a pull request

```sh
dotnet build && ./scripts/test.sh          # unit tests, with a floor
charts/hephaisto/ci/negative-tests.sh      # the chart's refusals
scripts/visual-test.sh                     # if you touched the console or the site
bash -n scripts/e2e/run.sh scripts/e2e/lib/*.sh
```

CI additionally runs Postgres integration tests, chart lint and kubeconform, an image build, and
an end-to-end install into a kind cluster.

**The full end-to-end harness needs a cluster and a model**, and it is not expected of a pull
request — `scripts/e2e/README.md` has it if you want to run it. Cluster verification is serial by
nature; there is one cluster.

## Changing the console

The token set in `design/` is canonical, enforced by test rather than convention. A colour written
directly into a component is a build failure, and that is deliberate: the alternative is four
almost-identical greys and a light mode nobody checks. `scripts/visual-test.sh --update`
regenerates the baselines, and you should look at the images before committing them, not just the
diffstat.

## Reporting a vulnerability

Not here — see [`SECURITY.md`](SECURITY.md). Please do not open a public issue for anything that
could be used against a running install.

## Licence

AGPL-3.0-only. By contributing you agree your contribution is licensed under it.
