#!/usr/bin/env bash
# Invoked by scripts/e2e/run.sh with HEPHAISTO_URL pointing at its port-forward.
#
# Installs on first use rather than checking a node_modules into the repo. The browser
# download is ~150MB and cached in ~/.cache/ms-playwright, so this is slow once and fast
# afterwards.
set -Eeuo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

command -v npm >/dev/null 2>&1 || { echo "npm is not installed; skipping the UI suite" >&2; exit 127; }

if [ ! -d node_modules ]; then
    echo "installing @playwright/test (first run only)"
    npm install --no-audit --no-fund --silent
fi

# --with-deps is Linux-only and fails on macOS; the browser alone is what is needed here.
npx --no-install playwright install chromium >/dev/null 2>&1 || npx playwright install chromium

# NOT `exec`: the exit status of `playwright test` is not sufficient on its own.
#
# Playwright exits 0 when every spec is SKIPPED, and that is exactly what happened - the
# committed report read {"expected":0,"skipped":5,"ok":true} and the harness recorded the phase
# as a pass on a run that asserted nothing. A suite that silently stops running is worse than
# one that fails, because it keeps reporting the last thing anyone believed about the console.
rm -f results.json

status=0
npx --no-install playwright test "$@" || status=$?

if [ ! -f results.json ]; then
    echo "playwright wrote no results.json; cannot tell a pass from a suite that never ran" >&2
    exit 1
fi

expected=$(jq -r '.stats.expected // 0' results.json)
skipped=$(jq -r '.stats.skipped  // 0' results.json)
unexpected=$(jq -r '.stats.unexpected // 0' results.json)

echo "playwright: expected=$expected skipped=$skipped unexpected=$unexpected"

if [ "$expected" -eq 0 ]; then
    echo "no spec actually ran (expected=0); refusing to report this as a pass" >&2
    exit 1
fi

if [ "$skipped" -ne 0 ]; then
    echo "$skipped spec(s) skipped; the console suite must run in full or say so" >&2
    exit 1
fi

exit "$status"
