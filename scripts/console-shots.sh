#!/usr/bin/env bash
# Photographs the SHIPPING console, in the pinned Playwright container so the output is
# identical on every machine - the same reason the visual baselines and brand-assets.sh use it.
#
# WHAT IT POINTS AT, AND WHY IT IS NOT THE E2E HARNESS
#
# The roadmap's plan for this was a `shots` phase at the end of `scripts/e2e/run.sh`, wanting
# the one `--full --mode Auto` run. That puts brand-new code at the end of the most expensive,
# least repeatable, single-shared-resource run in the project, and it buys a reader nothing a
# cheaper capture does not. So it points at `demo/compose.yaml` instead: two containers, no
# cluster, no API key, seeded from the committed transcripts. Deterministic, free, and
# reproducible by any stranger who read the README - which is the same argument the demo
# itself rests on.
#
#   HEPHAISTO_VERSION=<published tag> docker compose -f demo/compose.yaml up -d
#   scripts/console-shots.sh
#   docker compose -f demo/compose.yaml down -v
#
# RUN IT AGAINST THE RELEASE IMAGE. The transcripts ship INSIDE the image, so the console
# shows whatever corpus that build carried - photograph a pre-release tag and you publish a
# picture of a smaller demo than the site describes. That is the staleness this script exists
# to avoid, arriving by a different door.
#
# NEVER A CI GATE. The subject is model-written prose, and a pixel comparison over it would be
# a build gate on a language model. `scripts/visual-test.sh` is the safety net; this is
# photography. Commit what it writes, and LOOK at the diff.
set -Eeuo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
IMAGE="mcr.microsoft.com/playwright:v1.62.1-noble"
OUT="design/shots"

# From inside a container, localhost is the container. The compose stack publishes 8080 on the
# host, so the capture reaches it the same way the e2e harness reaches the model - by an
# address that is not loopback. Override for a console running anywhere else.
BASE_URL="${CONSOLE_URL:-http://host.docker.internal:8080}"
SCHEME="${CONSOLE_SCHEME:-dark}"

command -v docker >/dev/null 2>&1 || { echo "docker is required" >&2; exit 127; }

mkdir -p "$REPO/$OUT"

docker run --rm --init \
    --add-host=host.docker.internal:host-gateway \
    -v "$REPO":/work -w /work/design/visual \
    -e PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    -e BASE_URL="$BASE_URL" -e SCHEME="$SCHEME" -e OUT="/work/$OUT" "$IMAGE" bash -lc '
        set -Eeuo pipefail
        npm ci --no-audit --no-fund --silent
        node console-shots.mjs
    '

echo "wrote $OUT/ - LOOK at the diff before committing"
