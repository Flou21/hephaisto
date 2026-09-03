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
        node -e "
          const { chromium } = require(\"@playwright/test\");
          const base = process.env.BASE_URL, out = process.env.OUT;

          (async () => {
            const b = await chromium.launch();
            const p = await b.newPage({
                viewport: { width: 1440, height: 900 },
                deviceScaleFactor: 2,
                colorScheme: process.env.SCHEME,
            });

            // NOT networkidle: the console is Blazor Server and holds a SignalR websocket
            // open, so \"the network went quiet\" either never happens or happens before the
            // circuit has rendered anything.
            const go = async (path) => {
                const r = await p.goto(base + path, { waitUntil: \"domcontentloaded\", timeout: 30000 });
                if (!r || !r.ok()) throw new Error(\`\${path} returned \${r && r.status()}\`);
            };

            await go(\"/\");
            await p.evaluate(() => document.fonts.ready);

            // Same refusal as brand-assets.sh. A screenshot in a fallback face is a picture of
            // a different product, and it is not obvious in a thumbnail.
            const ok = await p.evaluate(() => document.fonts.check(\"16px Archivo\"));
            if (!ok) { console.error(\"Archivo did not load; refusing to photograph the console\"); process.exit(1); }

            // The console is Blazor Server: until the circuit takes over, the page is static
            // markup that photographs identically and behaves like nothing - the trap backlog
            // #48 records, and #53 is what happens when nobody checks. So WAIT for a row to
            // exist rather than counting whatever is there when the HTML arrives. Counting
            // immediately is how this script first reported an empty seeded database.
            // data-testid, not an href pattern. The console links are RELATIVE -
            // href=\"incidents/<guid>\" with no leading slash - so an href^=/incidents selector
            // matches nothing and reports a seeded database as empty. Incidents.razor puts a
            // testid on the link for exactly this reason; use the thing that was put there.
            await p.waitForSelector(\"[data-testid=incident-link]\", { timeout: 60000 })
                .catch(() => { throw new Error(\"no incident rows rendered within 60s - the circuit never took over, or the database is not seeded\"); });

            const rows = await p.locator(\"[data-testid=incident-link]\").count();

            await p.screenshot({ path: out + \"/console-incidents.png\", fullPage: false });
            console.log(\"wrote console-incidents.png (\" + rows + \" incidents)\");

            const href = await p.locator(\"[data-testid=incident-link]\").first().getAttribute(\"href\");
            await go(href.startsWith(\"/\") ? href : \"/\" + href);
            await p.evaluate(() => document.fonts.ready);
            await p.screenshot({ path: out + \"/console-incident.png\", fullPage: true });
            console.log(\"wrote console-incident.png (\" + href + \")\");

            await go(\"/status\");
            await p.evaluate(() => document.fonts.ready);
            await p.screenshot({ path: out + \"/console-status.png\", fullPage: false });
            console.log(\"wrote console-status.png\");

            await b.close();
          })().catch((e) => { console.error(String(e)); process.exit(1); });
        "
    '

echo "wrote $OUT/ - LOOK at the diff before committing"
