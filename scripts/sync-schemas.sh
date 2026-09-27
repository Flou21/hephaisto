#!/usr/bin/env bash
# Vendors the code-fix contract from dev-context into the two consumers in this repo.
#
# dev-context/schemas is the source of truth. The agent (C#) and the coder runner (TS) each
# carry a byte-identical copy plus SCHEMAS.lock, and a parity test on each side fails when the
# copy and the lock disagree - so contract drift shows up as a lock change in a PR, never as a
# runtime ContractViolation in production.
#
#   scripts/sync-schemas.sh [path-to-dev-context]      (default: ../dev/dev-context)
set -euo pipefail

here=$(cd "$(dirname "$0")/.." && pwd)
src=${1:-${DEV_CONTEXT_DIR:-$HOME/dev/dev-context}}
[ -d "$src/schemas" ] || { echo "no schemas/ under $src" >&2; exit 1; }

ctx_sha=$(git -C "$src" rev-parse HEAD 2>/dev/null || echo "uncommitted")

for dest in "$here/src/Hephaisto.Agent/CodeFix/Contract/schemas" "$here/coder/contracts"; do
  rm -rf "$dest"
  mkdir -p "$dest/samples"
  cp "$src"/schemas/*.schema.json "$dest/"
  cp -R "$src"/schemas/samples/valid "$src"/schemas/samples/invalid "$dest/samples/"
  (
    cd "$dest"
    echo "# dev-context $ctx_sha"
    find . -type f -name '*.json' | LC_ALL=C sort | while read -r f; do
      printf '%s  %s\n' "$(shasum -a 256 "$f" | cut -d' ' -f1)" "${f#./}"
    done
  ) > "$dest/SCHEMAS.lock"
  echo "vendored $(find "$dest" -name '*.json' | wc -l | tr -d ' ') files into ${dest#$here/}"
done
