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

exec npx --no-install playwright test "$@"
