# The project record

Most projects keep their engineering record private and publish a polished subset. This one
publishes both, and the reason is in the backlog's own header: it is written against **what is
actually in the repo**, not against what was planned.

These documents are in the repository rather than on this site, because they are written for
somebody about to change the code and they change with it.

| Document | What it is |
|---|---|
| [Backlog](https://github.com/Flou21/hephaisto/blob/main/docs/backlog.md) | Everything known to be broken, half-built, or lying. Numbered, evidenced, sized. |
| [Roadmap](https://github.com/Flou21/hephaisto/blob/main/docs/roadmap.md) | What each milestone shipped, and what it deliberately did not. |
| [History](https://github.com/Flou21/hephaisto/blob/main/docs/history.md) | Why the code is shaped the way it is. Several entries record a hypothesis that turned out to be wrong. |
| [Verification](https://github.com/Flou21/hephaisto/blob/main/docs/verification.md) | The hand-run acceptance checklist, per release. |
| [Design](https://github.com/Flou21/hephaisto/blob/main/docs/design.md) | The design language, and what a contributor reads before touching CSS. |
| [Changelog](/project/changelog) | What shipped, per release, for somebody upgrading. |

## Why the backlog is public

An agent that can delete pods asks for a particular kind of trust. The most useful thing this
project can offer in exchange is a complete, specific list of the ways it is currently wrong —
including the ones that block sentences it would like to put on its own landing page.

Two entries have carried a `Blocks:` field pointing at exactly that: one blocked claiming an
action rate, and one blocked claiming the agent resolves incidents. The second was closed by
confirming it on a cluster, once. The first
[was corrected twice, and the second correction retired the p-value the first one produced](/internals/evaluation#why-there-is-no-such-thing-as-the-action-rate),
and it was closed on 2026-09-03 — this page went on calling it open afterwards, which is the
failure mode a public backlog is supposed to make impossible.

## The distinction between history and changelog

They are different documents on purpose. A changelog says what shipped, per release, for someone
upgrading. The history says *why the code is shaped the way it is*, for someone about to change
it — and it keeps the entries where the hypothesis was wrong, because those are the expensive ones
to re-learn.

## Contributing

`CONTRIBUTING.md` in the repository has the short version. The one rule worth stating here:
**config needs a reader in `src/` in the same commit.** An option nothing reads is config that
behaves like a comment, and the backlog has a whole section named after that failure.
