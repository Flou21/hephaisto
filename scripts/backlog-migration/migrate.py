#!/usr/bin/env python3
"""docs/backlog.md -> GitHub issues, once. extras.md adds the issues the roadmap held.

    migrate.py parse                 what the file holds: one line per entry
    migrate.py dry-run [--out DIR]   render every issue to DIR and print the table; touches nothing
    migrate.py setup                 create the labels and milestones the issues name (needs --yes)
    migrate.py execute               create them (needs --yes); resumable through map.json
    migrate.py table                 the old id -> issue table for the frozen docs/backlog.md

Numbering. GitHub hands issues and pull requests one sequence. Backlog ids run 1..165 and the
ids below the next free number are taken by pull requests for good, so an entry gets its own
number only when that number is the next one GitHub would hand out: the ids from the next free
number upward are created first and in order, each after asking what the next number is. An id
that was taken in between, and every id below, goes to the tail and says which entry it was.

State, labels and milestone come from entries.tsv, never from the prose: the backlog words a
status twenty ways and leaves it out of two dozen entries. Every id needs a row or nothing runs.
"""

from __future__ import annotations

import argparse
import csv
import json
import re
import subprocess
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
BACKLOG = ROOT / "docs" / "backlog.md"
ENTRIES = HERE / "entries.tsv"
EXTRAS = HERE / "extras.md"
MAP = HERE / "map.json"
REPO = "Flou21/hephaisto"
BLOB = f"https://github.com/{REPO}/blob/main"

HEADING = re.compile(r"^### (\d+)\. (.+?)\s*$")
SIZE = re.compile(r"\*\*Size\.?\*\*\.?\s*\**\s*(S|M|L|XL)\b")
STATES = {"open", "completed", "not_planned"}

# The backlog's seventeen sections, as the labels an issue list can be filtered by.
AREAS = {
    "Measurement integrity": "area:measurement",
    "Correctness and safety": "area:correctness",
    "Telemetry drift": "area:telemetry",
    "Config that behaves like a comment": "area:config",
    "Documentation asserting things that do not exist": "area:docs",
    "Chart and deployment": "area:chart",
    "As the only incident system": "area:pager",
    "Found in production": "area:production",
    "Dead or unreachable code": "area:hygiene",
    "Hygiene": "area:hygiene",
}


@dataclass
class Entry:
    id: int
    title: str
    section: str
    line: int
    body: str
    size: str | None = None
    state: str = ""
    labels: list[str] = field(default_factory=list)
    milestone: str = ""


@dataclass
class Extra:
    slug: str
    title: str
    state: str
    labels: list[str]
    milestone: str
    body: str


def parse_extras() -> list[Extra]:
    """extras.md: the issues that were never backlog entries. Its own header says the format."""
    text = EXTRAS.read_text().split("\n-->\n", 1)[1]
    extras = []
    for block in re.split(r"^## ", text, flags=re.M)[1:]:
        title, meta, body = block.split("\n", 2)
        m = re.fullmatch(r"<!-- (.+) -->", meta.strip())
        if not m:
            sys.exit(f"extras.md: {title!r} has no line of its own saying id, state, labels and milestone")
        f = {k.strip(): v.strip() for k, v in (part.split(":", 1) for part in m.group(1).split("|"))}
        if f["state"] not in STATES:
            sys.exit(f"extras.md: {title!r}: state {f['state']!r}")
        extras.append(Extra(f["id"], title.strip(), f["state"], [l for l in f["labels"].split(",") if l], f["milestone"], body.strip("\n")))
    if len({x.slug for x in extras}) != len(extras):
        sys.exit("extras.md: an id appears twice")
    return extras


def render_extra(x: Extra, number_of, slug_of) -> str:
    body = re.sub(r"\{\{([\w.-]+)\}\}", lambda m: slug_of(m.group(1)), x.body)
    return rewrite(body, number_of, 0) + "\n"


def parse() -> list[Entry]:
    entries: list[Entry] = []
    section = ""
    current: Entry | None = None
    lines: list[str] = []
    fenced = False

    def close() -> None:
        nonlocal current, lines
        if current is not None:
            current.body = "\n".join(lines).strip("\n")
            m = SIZE.search(current.body)
            current.size = m.group(1) if m else None
            entries.append(current)
        current, lines = None, []

    for n, raw in enumerate(BACKLOG.read_text().split("\n"), start=1):
        if raw.lstrip().startswith("```"):
            fenced = not fenced
        if not fenced and raw.startswith("## "):
            close()
            section = raw[3:].strip()
            continue
        if not fenced and raw.startswith("### "):
            close()
            m = HEADING.match(raw)
            if m:  # an unnumbered ### is a review note: it ends the entry before it and is not one
                current = Entry(int(m.group(1)), m.group(2), section, n, "")
            continue
        if current is not None:
            lines.append(raw)
    close()

    ids = [e.id for e in entries]
    if len(ids) != len(set(ids)):
        sys.exit("an id appears twice in docs/backlog.md")
    return sorted(entries, key=lambda e: e.id)


def area_of(section: str) -> str | None:
    for prefix, label in AREAS.items():
        if section.startswith(prefix):
            return label
    return None  # "Opened by vX" and the carry reviews say when, not what about


def load_rows(entries: list[Entry], path: Path = ENTRIES) -> None:
    if not path.exists():
        sys.exit(f"{path} is missing")
    rows = {}
    with path.open() as f:
        for row in csv.DictReader((l for l in f if not l.startswith("#")), delimiter="\t"):
            rows[int(row["id"])] = row
    missing = [e.id for e in entries if e.id not in rows]
    if missing:
        sys.exit(f"entries.tsv has no row for {missing}")
    for e in entries:
        row = rows[e.id]
        if row["state"] not in STATES:
            sys.exit(f"#{e.id}: state {row['state']!r} is not one of {sorted(STATES)}")
        e.state = row["state"]
        e.milestone = row.get("milestone", "").strip()
        e.labels = [l for l in (row.get("labels") or "").split(",") if l]
        if (a := area_of(e.section)) and a not in e.labels:
            e.labels.append(a)
        if e.size and f"size:{e.size}" not in e.labels:
            e.labels.append(f"size:{e.size}")


def plain(title: str) -> str:
    """A heading as an issue title: links flattened, since a title renders none."""
    return re.sub(r"\[([^\]]+)\]\([^)]*\)", r"\1", title).strip()


LINK = re.compile(r"\[(?P<text>[^\]]*)\]\((?P<href>[^)\s]+)\)")
BARE = re.compile(r"(?<![\w/&#\[`])#(\d{1,3})\b(?!\]\()")
PR_BEFORE = re.compile(r"(?:PR|PRs|pull request|pull requests|and|,)\s*$", re.I)


def rewrite(body: str, number_of, own: int) -> str:
    """Links that only work inside docs/backlog.md, as links that work from an issue."""

    def ref(n: int) -> str:
        return number_of(n)

    def link(m: re.Match) -> str:
        text, href = m.group("text"), m.group("href")
        if (t := re.fullmatch(r"#(\d+)(-[^)]*)?", href)) or (t := re.fullmatch(r"backlog\.md#(\d+)(-[^)]*)?", href)):
            n = int(t.group(1))
            return ref(n) if re.fullmatch(r"(backlog )?#\d+", text) else f"{text} ({ref(n)})"
        if href.startswith(("http://", "https://", "mailto:")):
            return m.group(0)
        if href.startswith("#"):
            return f"[{text}]({BLOB}/docs/backlog.md{href})"
        path = href[3:] if href.startswith("../") else f"docs/{href}"
        return f"[{text}]({BLOB}/{path})"

    def bare(m: re.Match, line: str) -> str:
        n = int(m.group(1))
        if not 1 <= n <= 165 or PR_BEFORE.search(line[: m.start()]) and "PR" in line[: m.start()][-24:]:
            return m.group(0)
        return ref(n)

    out, fenced = [], False
    for line in body.split("\n"):
        if line.lstrip().startswith("```"):
            fenced = not fenced
        if fenced or line.lstrip().startswith("```"):
            out.append(line)
            continue
        # code spans are left alone: split on backticks and rewrite the even pieces
        pieces = line.split("`")
        for i in range(0, len(pieces), 2):
            p = LINK.sub(link, pieces[i])
            pieces[i] = BARE.sub(lambda m, p=p: bare(m, p), p)
        out.append("`".join(pieces))
    return "\n".join(out)


def title_of(e: Entry, number_of) -> str:
    """The heading with the entries it names renumbered, as the body is."""
    return rewrite(plain(e.title), number_of, e.id)


def render(e: Entry, number_of) -> str:
    where = f"{BLOB}/docs/backlog.md"
    head = (
        f"> Backlog entry {e.id}, moved here from [`docs/backlog.md`]({where}) "
        f"(section *{e.section}*). The text below is the entry as it stood; that file is frozen.\n"
    )
    return head + "\n" + rewrite(e.body, number_of, e.id) + "\n"


def gh(*args: str, input: str | None = None) -> str:
    r = subprocess.run(["gh", *args], input=input, capture_output=True, text=True)
    if r.returncode != 0:
        sys.exit(f"gh {' '.join(args[:4])}…: {r.stderr.strip()}")
    return r.stdout


def next_number() -> int:
    out = gh("api", f"repos/{REPO}/issues?state=all&sort=created&direction=desc&per_page=1", "--jq", ".[0].number")
    return int(out.strip() or 0) + 1


def plan_numbers(entries: list[Entry], start: int) -> dict[int, int]:
    """What each entry would be numbered if nothing else takes a number meanwhile."""
    ids = [e.id for e in entries]
    own = [i for i in ids if i >= start]
    tail = [i for i in ids if i < start]
    numbers = {i: i for i in own}
    n = max(own, default=start - 1) + 1
    for i in tail:
        numbers[i] = n
        n += 1
    return numbers


def cmd_parse(_: argparse.Namespace) -> None:
    for e in parse():
        print(f"{e.id}\t{e.size or '-'}\t{len(e.body)}\t{area_of(e.section) or '-'}\t{plain(e.title)[:90]}")


def cmd_dry_run(a: argparse.Namespace) -> None:
    entries = parse()
    load_rows(entries, Path(a.entries))
    start = a.start or next_number()
    numbers = plan_numbers(entries, start)
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    for old in out.glob("*.md"):
        old.unlink()
    rows = []
    for e in entries:
        number_of = lambda n: f"#{numbers[n]}" if n in numbers else f"#{n}"
        body, title = render(e, number_of), title_of(e, number_of)
        if len(body) > 65000:
            sys.exit(f"#{e.id}: {len(body)} characters is over GitHub's limit for an issue body")
        front = f"<!-- issue #{numbers[e.id]} | {e.state} | {','.join(e.labels)} | {e.milestone or '-'} -->\n"
        (out / f"{numbers[e.id]:03d}-backlog-{e.id:03d}.md").write_text(f"{front}# {title}\n\n{body}")
        rows.append((e.id, numbers[e.id], e.state, e.milestone or "-", ",".join(e.labels), title[:70]))
    extras = parse_extras()
    n = max(numbers.values()) + 1
    slugs = {}
    for x in extras:
        slugs[x.slug] = n
        n += 1
    number_of = lambda k: f"#{numbers[k]}" if k in numbers else f"#{k}"
    for x in extras:
        body = render_extra(x, number_of, lambda slug: f"#{slugs[slug]}")
        front = f"<!-- issue #{slugs[x.slug]} | {x.state} | {','.join(x.labels)} | {x.milestone or '-'} -->\n"
        (out / f"{slugs[x.slug]:03d}-extra-{x.slug}.md").write_text(f"{front}# {x.title}\n\n{body}")
        rows.append((x.slug, slugs[x.slug], x.state, x.milestone or "-", ",".join(x.labels), x.title[:70]))
    with (out / "index.tsv").open("w") as f:
        f.write("backlog\tissue\tstate\tmilestone\tlabels\ttitle\n")
        for r in sorted(rows, key=lambda r: r[1]):
            f.write("\t".join(str(x) for x in r) + "\n")
    by_state: dict[str, int] = {}
    for r in rows:
        by_state[r[2]] = by_state.get(r[2], 0) + 1
    same = sum(1 for r in rows if r[0] == r[1])
    print(f"{len(rows)} issues: {len(entries)} backlog entries and {len(extras)} from the roadmap; next free number {start}; "
          f"{same} keep their backlog number, {len(entries) - same} go to the tail")
    print("  " + ", ".join(f"{k}: {v}" for k, v in sorted(by_state.items())))
    print(f"  labels: {sorted({l for e in [*entries, *extras] for l in e.labels})}")
    print(f"  milestones: {sorted({e.milestone for e in [*entries, *extras] if e.milestone})}")
    print(f"  rendered to {out}")


LABEL_COLOURS = {"area": "c5def5", "size": "ededed", "decision": "fbca04", "limitation": "d4c5f9", "idea": "bfdadc"}
LABEL_TEXT = {
    "decision": "Waits for the owner's decision, not for code",
    "limitation": "Deliberate or accepted; written down so it stays findable",
    "idea": "From the roadmap's menu; not scheduled",
}
OPEN_MILESTONES = {"v0.13.0", "v0.14.0"}


def cmd_setup(a: argparse.Namespace) -> None:
    """The labels and milestones the issues name, created before any issue is."""
    if not a.yes:
        sys.exit("this creates labels and milestones on the repository: pass --yes")
    entries = parse()
    load_rows(entries, Path(a.entries))
    have = set(json.loads(gh("label", "list", "--repo", REPO, "--limit", "200", "--json", "name", "--jq", "[.[].name]")))
    extras = parse_extras()
    for label in sorted({l for e in [*entries, *extras] for l in e.labels}):
        if label in have:
            continue
        kind = label.split(":")[0]
        text = LABEL_TEXT.get(label, f"Backlog {kind}: {label.split(':', 1)[-1]}")
        gh("label", "create", label, "--repo", REPO, "--color", LABEL_COLOURS.get(kind, "ededed"), "--description", text)
        print(f"label {label}")
    existing = set(json.loads(gh("api", f"repos/{REPO}/milestones?state=all&per_page=100", "--jq", "[.[].title]")))
    for m in sorted({e.milestone for e in [*entries, *extras] if e.milestone} | OPEN_MILESTONES, key=lambda v: [int(x) for x in v[1:].split(".")]):
        if m in existing:
            continue
        gh("api", f"repos/{REPO}/milestones", "-f", f"title={m}")  # open for now: execute closes the released ones
        print(f"milestone {m}")


def cmd_execute(a: argparse.Namespace) -> None:
    """Create, then make true.

    The next number is counted from what the creates themselves return. GitHub's list of issues
    lags a create by minutes: asked after #77 existed it still said 76, and the first run of this
    put three entries on numbers that were not theirs. An issue is a number and editable content,
    so the second pass compares every issue with its entry and rewrites the ones that differ -
    which is also what repaired those three.

    GitHub allows about 500 writes an hour, and 187 issues of which 135 are closed is already
    320, so an issue is created with the numbers the plan expects and rewritten only if wrong."""
    if not a.yes:
        sys.exit("this creates public issues and uses up their numbers for good: pass --yes after the dry run was read")
    entries = {e.id: e for e in parse()}
    load_rows(list(entries.values()), Path(a.entries))
    extras = parse_extras()
    done: dict[str, int] = json.loads(MAP.read_text()) if MAP.exists() else {}
    last_id = max(entries)

    nxt = max(done.values()) + 1 if done else next_number()
    own = [i for i in sorted(entries) if str(i) not in done and i >= nxt]
    later = [i for i in sorted(entries) if str(i) not in done and i < nxt]
    planned = {i: i for i in own}
    n = max(last_id, max(done.values(), default=0)) + 1
    for i in later:
        planned[i] = n
        n += 1
    planned_slugs = {}
    for x in extras:
        planned_slugs[x.slug] = n
        n += 1

    def expect(k: int) -> str:
        return f"#{done[str(k)]}" if str(k) in done else f"#{planned[k]}" if k in planned else f"backlog entry {k}"

    def expect_slug(slug: str) -> str:
        key = f"extra:{slug}"
        return f"#{done[key]}" if key in done else f"#{planned_slugs[slug]}"

    def record(key: str, number: int) -> None:
        done[key] = number
        MAP.write_text(json.dumps(done, indent=1, sort_keys=True) + "\n")

    def create(title: str, body: str, labels: list[str], milestone: str) -> int:
        args = ["issue", "create", "--repo", REPO, "--title", title, "--body-file", "-"]
        for l in labels:
            args += ["--label", l]
        if milestone:
            args += ["--milestone", milestone]
        url = gh(*args, input=body).strip()
        time.sleep(a.pace)
        return int(url.rsplit("/", 1)[1])

    def create_entry(i: int) -> int:
        e = entries[i]
        return create(title_of(e, expect), render(e, expect), e.labels, e.milestone)

    k = 0
    while k < len(own):
        i = own[k]
        if i != nxt:  # a number between is still free and belongs to nobody left: the order is gone
            print(f"expected to create #{nxt} for backlog {nxt}, which is already placed; the rest goes to the tail", flush=True)
            break
        number = create_entry(i)
        if number == i:
            record(str(i), number)
            print(f"backlog {i} -> #{number}", flush=True)
            k += 1
        elif number <= last_id and str(number) not in done:
            # somebody took i..number-1 meanwhile; this issue is number's by right, and the second
            # pass gives it number's text
            record(str(number), number)
            print(f"#{i}..#{number - 1} were taken in between; #{number} is kept for backlog {number}", flush=True)
            k = own.index(number) + 1
        else:
            record(str(i), number)
            print(f"backlog {i} -> #{number}", flush=True)
            k += 1
        nxt = number + 1
    for i in sorted(entries):
        if str(i) in done:
            continue
        record(str(i), create_entry(i))
        print(f"backlog {i} -> #{done[str(i)]}", flush=True)
    for x in extras:
        key = f"extra:{x.slug}"
        if key in done:
            continue
        record(key, create(x.title, render_extra(x, expect, expect_slug), x.labels, x.milestone))
        print(f"{x.slug} -> #{done[key]}", flush=True)

    milestones = {m["title"]: m["number"] for m in json.loads(gh("api", f"repos/{REPO}/milestones?state=all&per_page=100"))}

    def settle(number: int, title: str, body: str, labels: list[str], milestone: str, state: str) -> None:
        now = json.loads(gh("issue", "view", str(number), "--repo", REPO, "--json", "title,body,state,labels,milestone"))
        same = (
            now["title"] == title
            and now["body"].replace("\r\n", "\n").strip() == body.strip()
            and sorted(l["name"] for l in now["labels"]) == sorted(labels)
            and ((now["milestone"] or {}).get("title") or "") == milestone
        )
        if not same:
            payload = {"title": title, "body": body, "labels": labels, "milestone": milestones[milestone] if milestone else None}
            gh("api", "-X", "PATCH", f"repos/{REPO}/issues/{number}", "--input", "-", input=json.dumps(payload))
            print(f"#{number} rewritten", flush=True)
            time.sleep(a.pace)
        if state != "open" and now["state"] == "OPEN":
            gh("issue", "close", str(number), "--repo", REPO, "--reason", "completed" if state == "completed" else "not planned")
            time.sleep(a.pace)

    for i, e in sorted(entries.items()):
        settle(done[str(i)], title_of(e, expect), render(e, expect), e.labels, e.milestone, e.state)
    for x in extras:
        settle(done[f"extra:{x.slug}"], x.title, render_extra(x, expect, expect_slug), x.labels, x.milestone, x.state)

    # gh only finds an open milestone, so setup opened them all; the released ones close here
    for title, number in milestones.items():
        if title not in OPEN_MILESTONES:
            gh("api", "-X", "PATCH", f"repos/{REPO}/milestones/{number}", "-f", "state=closed")
    print(f"{len(entries) + len(extras)} issues written; the map is {MAP}")


def cmd_table(_: argparse.Namespace) -> None:
    done = json.loads(MAP.read_text())
    entries = parse()
    print("| Backlog | Issue | Title |\n|---|---|---|")
    for e in entries:
        n = done[str(e.id)]
        print(f"| {e.id} | [#{n}](https://github.com/{REPO}/issues/{n}) | {plain(e.title).replace('|', chr(92) + '|')} |")


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("parse").set_defaults(run=cmd_parse)
    d = sub.add_parser("dry-run")
    d.add_argument("--out", default=str(HERE / "out"))
    d.add_argument("--start", type=int, help="assume this next free number instead of asking GitHub")
    d.add_argument("--entries", default=str(ENTRIES))
    d.set_defaults(run=cmd_dry_run)
    u = sub.add_parser("setup")
    u.add_argument("--yes", action="store_true")
    u.add_argument("--entries", default=str(ENTRIES))
    u.set_defaults(run=cmd_setup)
    x = sub.add_parser("execute")
    x.add_argument("--yes", action="store_true")
    x.add_argument("--pace", type=float, default=3.0, help="seconds between writes")
    x.add_argument("--entries", default=str(ENTRIES))
    x.add_argument("--start", type=int, help=argparse.SUPPRESS)
    x.set_defaults(run=cmd_execute)
    sub.add_parser("table").set_defaults(run=cmd_table)
    a = p.parse_args()
    a.run(a)


if __name__ == "__main__":
    main()
