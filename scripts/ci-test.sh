#!/usr/bin/env bash
#
# Run a test project and REFUSE to pass if suspiciously few tests ran.
#
#     scripts/ci-test.sh <project.csproj> <minimum> [configuration]
#
# The floor is not paranoia. Microsoft.Testing.Platform can exit 0 having run nothing, and
# this repo has already been burned by exactly that: `dotnet test` starts the executable in
# server mode, it reports "Zero tests ran", and the exit code is 0. A green build that tested
# nothing is worse than a red one, because nobody looks at it again.
#
# Note also what this guards against SECOND: `--minimum-expected-tests` is NOT a flag this
# runner supports. Passing it produces `error: unknown option` - and still exits 0. So the
# count is parsed out of the summary line instead, which is the thing that is actually true.
set -uo pipefail
cd "$(dirname "$0")/.."

PROJECT="${1:?usage: ci-test.sh <project.csproj> <minimum> [configuration]}"
MINIMUM="${2:?usage: ci-test.sh <project.csproj> <minimum> [configuration]}"
CONFIG="${3:-Release}"

OUT=$(mktemp)
trap 'rm -f "$OUT"' EXIT

dotnet run --project "$PROJECT" -c "$CONFIG" 2>&1 | tee "$OUT"
STATUS=${PIPESTATUS[0]}

if [ "$STATUS" -ne 0 ]; then
    echo "tests failed (exit $STATUS)" >&2
    exit "$STATUS"
fi

# "  Hephaisto.Tests  Total: 647, Errors: 0, Failed: 0, ..."
TOTAL=$(grep -oE 'Total: *[0-9]+' "$OUT" | grep -oE '[0-9]+' | tail -1)

if [ -z "${TOTAL:-}" ]; then
    echo "could not find a test count in the output: the runner changed its summary format," >&2
    echo "so this guard is no longer guarding anything. Fix it rather than removing it." >&2
    exit 1
fi

if [ "$TOTAL" -lt "$MINIMUM" ]; then
    echo "only $TOTAL tests ran, expected at least $MINIMUM." >&2
    echo "Either a whole project stopped being discovered, or the floor needs lowering on purpose." >&2
    exit 1
fi

echo "$TOTAL tests ran (floor $MINIMUM)."
