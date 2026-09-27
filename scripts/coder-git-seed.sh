#!/usr/bin/env bash
#
# Writes the git bundles the dev git server (infra/coder/git-server) is built from.
#
#     scripts/coder-git-seed.sh
#
# Two repositories, each as ONE bundle holding every branch and tag:
#
#   ${FIXTURE_REPO:-$HOME/hephaisto-fixture-dotnet}  -> Flou21__hephaisto-fixture-dotnet.bundle
#   ${DEV_CONTEXT_REPO:-$HOME/dev/dev-context}         -> dev-context.bundle
#
# `__` becomes `/` in the image, so the fixture is served at /Flou21/hephaisto-fixture-dotnet.git
# - the same path as the GitHub repository it stands in for.
#
# "Every branch" includes remote-tracking branches that were never checked out locally. A fresh
# clone of the fixture repo has only `main` as a local branch and the fixture/* branches as
# origin/*, and bundling --branches alone would silently ship a git server with no fixtures on
# it - the coder would then fail to clone its defaultBranch with a message about a ref, not
# about this script.
#
# Idempotent; the Tiltfile runs it before every coder-git build. Output goes to a gitignored
# directory, because a bundle of dev-context is a copy of a private repository.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${CODER_GIT_SEED_DIR:-$ROOT/infra/coder/git-server/seed}"
FIXTURE_REPO="${FIXTURE_REPO:-$HOME/hephaisto-fixture-dotnet}"
DEV_CONTEXT_REPO="${DEV_CONTEXT_REPO:-$HOME/dev/dev-context}"

bundle() {
    local src="$1" name="$2" var="$3"

    if ! git -C "$src" rev-parse --git-dir >/dev/null 2>&1; then
        echo "coder-git-seed: $src is not a git repository. Clone it there, or point $var at it." >&2
        exit 1
    fi

    local tmp
    tmp=$(mktemp -d)
    # shellcheck disable=SC2064
    trap "rm -rf '$tmp'" RETURN

    # A bare clone of a local repository copies its local branches and tags.
    git clone -q --bare --no-local "$src" "$tmp/r.git"

    # Then the remote-tracking branches that have no local branch of the same name.
    local b
    while read -r b; do
        [ -n "$b" ] && [ "$b" != HEAD ] || continue
        if ! git -C "$tmp/r.git" show-ref -q --verify "refs/heads/$b"; then
            git -C "$tmp/r.git" fetch -q "$src" "refs/remotes/origin/$b:refs/heads/$b"
        fi
    done < <(git -C "$src" for-each-ref --format='%(refname:strip=3)' refs/remotes/origin)

    mkdir -p "$OUT"
    rm -f "$OUT/$name.bundle"
    if git -C "$tmp/r.git" show-ref -q --tags; then
        git -C "$tmp/r.git" bundle create -q "$OUT/$name.bundle" --branches --tags
    else
        git -C "$tmp/r.git" bundle create -q "$OUT/$name.bundle" --branches
    fi

    echo "coder-git-seed: $name <- $src ($(git -C "$tmp/r.git" for-each-ref --format='%(refname:short)' refs/heads | tr '\n' ' '))"
}

bundle "$FIXTURE_REPO"     Flou21__hephaisto-fixture-dotnet FIXTURE_REPO
bundle "$DEV_CONTEXT_REPO" dev-context                      DEV_CONTEXT_REPO
