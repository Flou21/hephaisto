#!/usr/bin/env bash
# Visual baselines for the design language. No cluster, no agent, no database - the subject is
# design/gallery.html, a static page linking the shipping stylesheet, loaded over file://.
#
#   scripts/visual-test.sh            compare against the committed baselines
#   scripts/visual-test.sh --update   regenerate them (look at the diff before committing it)
#
# ALWAYS the pinned Playwright container, on every machine. Font rasterisation is not portable:
# the same stylesheet in the same browser renders measurably differently on macOS and on Linux,
# so baselines taken on a laptop would fail in CI on antialiasing alone. A visual suite that
# fails for reasons nobody believes is one people re-baseline past, which is worse than not
# having one.
#
# On Linux this runs the suite directly, because CI already runs this script INSIDE that image.
# Anywhere else it re-enters itself through docker. One entry point, so the assertions at the
# bottom cannot be bypassed by taking the other path - which is how the console suite ended up
# reporting a pass on a run that asserted nothing (docs/backlog.md #1).
set -Eeuo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Pinned to the @playwright/test version in design/visual/package.json. Bump them in the same
# commit, or the browser taking the screenshots stops being the browser the baselines came from.
IMAGE="mcr.microsoft.com/playwright:v1.62.1-noble"

if [ "$(uname -s)" != "Linux" ]; then
    command -v docker >/dev/null 2>&1 || {
        echo "docker is required off Linux: the baselines are only valid on the pinned container" >&2
        exit 127
    }
    exec docker run --rm --init \
        -v "$REPO":/work -w /work \
        -e PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
        "$IMAGE" scripts/visual-test.sh "$@"
fi

cd "$REPO/design/visual"

ARGS=()
if [ "${1:-}" = "--update" ]; then ARGS+=(--update-snapshots); shift; fi
ARGS+=("$@")

# npm ci rather than install: the lockfile is the pinned version, and a visual suite that
# silently drifted onto a different Playwright would produce diffs nobody could explain.
npm ci --no-audit --no-fund --silent

rm -f results.json

status=0
npx --no-install playwright test "${ARGS[@]}" || status=$?

# The exit status alone is not sufficient, for the reason the console suite learned the hard
# way: playwright exits 0 when every spec skipped, so a suite that quietly stopped running
# looks exactly like one that passed.
if [ ! -f results.json ]; then
    echo "playwright wrote no results.json; cannot tell a pass from a suite that never ran" >&2
    exit 1
fi

# node rather than jq: node is guaranteed in the Playwright image, jq is not, and a runner
# that fails on a missing tool would report the same non-zero status as a real visual diff.
read -r expected skipped unexpected <<<"$(node -e '
    const s = require("./results.json").stats || {};
    console.log((s.expected||0), (s.skipped||0), (s.unexpected||0));
')"

echo "visual: expected=$expected skipped=$skipped unexpected=$unexpected"

if [ "$expected" -eq 0 ]; then
    echo "no visual comparison actually ran (expected=0); refusing to report this as a pass" >&2
    exit 1
fi

if [ "$skipped" -ne 0 ]; then
    echo "$skipped comparison(s) skipped; the visual suite must run in full or say so" >&2
    exit 1
fi

exit "$status"
