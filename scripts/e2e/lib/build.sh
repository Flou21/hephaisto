#!/usr/bin/env bash
# Phases 1 and 2: get a published build, and wait until it is really pullable.

# ---------------------------------------------------------------------------------------
# Phase 1a - dispatch a nightly
# ---------------------------------------------------------------------------------------
build_nightly() {
    say "dispatching nightly.yml on $(git -C "$REPO" rev-parse --abbrev-ref HEAD)"

    local before
    before=$(gh -R "$GH_REPO" run list --workflow=nightly.yml --limit 1 \
                --json databaseId -q '.[0].databaseId // 0')

    gh -R "$GH_REPO" workflow run nightly.yml --ref "$(git -C "$REPO" rev-parse --abbrev-ref HEAD)" \
        || die "could not dispatch nightly.yml"

    # A dispatch returns before the run exists, so the id has to be polled for. Matching on
    # "newer than the one that was newest a moment ago" rather than "the newest" avoids
    # latching onto a previous run and declaring victory on it.
    local run_id="" deadline=$(( SECONDS + 120 ))
    printf '  %swaiting%s for the run to appear ' "$C_DIM" "$C_RESET"
    while [ "$SECONDS" -lt "$deadline" ]; do
        run_id=$(gh -R "$GH_REPO" run list --workflow=nightly.yml --limit 1 \
                    --json databaseId -q '.[0].databaseId // 0')
        if [ "$run_id" != "0" ] && [ "$run_id" != "$before" ]; then
            printf ' %s\n' "$run_id"
            break
        fi
        printf '.'
        sleep 3
        run_id=""
    done
    [ -n "$run_id" ] || die "nightly.yml was dispatched but no new run appeared within 120s"

    say "watching run $run_id (this takes a few minutes)"
    gh -R "$GH_REPO" run watch "$run_id" --exit-status \
        || die "nightly.yml run $run_id failed: gh -R $GH_REPO run view $run_id --log-failed"

    pass "nightly build published"

    # The version job uploads this precisely because a job output is unreadable from outside
    # the workflow. Recomputing MinVer here would only agree while this checkout sits on the
    # exact commit that was built.
    rm -rf "$WORKDIR/version-artifact"
    gh -R "$GH_REPO" run download "$run_id" -n nightly-version -D "$WORKDIR/version-artifact" \
        || die "could not download the nightly-version artifact from run $run_id"

    VERSION=$(cat "$WORKDIR/version-artifact/nightly-version.txt")
    [ -n "$VERSION" ] || die "the nightly-version artifact was empty"
    say "version under test: $VERSION"
}

# ---------------------------------------------------------------------------------------
# Phase 1b - cut a release candidate
# ---------------------------------------------------------------------------------------
# The pre-ship path. Unlike a nightly this is permanent and public: a tag, a GHCR image, an
# OCI chart and a GitHub prerelease, none of which should be deleted afterwards. So it
# refuses to guess and it asks before pushing.
build_rc() {
    local branch dirty behind
    branch=$(git -C "$REPO" rev-parse --abbrev-ref HEAD)
    [ "$branch" = "main" ] || die "--rc must be cut from main, not $branch"

    dirty=$(git -C "$REPO" status --porcelain)
    [ -z "$dirty" ] || die "working tree is dirty; an rc must correspond to a commit that exists"

    git -C "$REPO" fetch --quiet origin main --tags
    behind=$(git -C "$REPO" rev-list --count HEAD..origin/main)
    [ "$behind" -eq 0 ] || die "HEAD is $behind commit(s) behind origin/main; push or rebase first"

    # The next free -rcN for the version MinVer says this commit is heading towards.
    #
    # BOTH flags are load-bearing, and for one reason: minver-cli does not read
    # Directory.Build.props. Without -p it defaults to alpha.0; without -m it cannot see
    # MinVerMinimumMajorMinor, so after v0.2.0 it says 0.2.1 no matter what milestone the repo
    # is actually working towards. That failure is expensive rather than cosmetic - the tag
    # gets PUSHED, and then release.yml's own guard refuses to publish because MinVer under
    # MSBuild disagrees with it, leaving a permanent public tag with nothing behind it.
    #
    # The floor is read out of the props file rather than hardcoded here, so the two cannot
    # drift the way the -p default already did once.
    local base next=1 minimum
    minimum=$(sed -n 's:.*<MinVerMinimumMajorMinor>\(.*\)</MinVerMinimumMajorMinor>.*:\1:p' \
                "$REPO/Directory.Build.props" | head -1)
    base=$(cd "$REPO" && dotnet minver -t v -p main.0 ${minimum:+-m "$minimum"} 2>/dev/null | sed 's/-.*//')
    while git -C "$REPO" rev-parse -q --verify "refs/tags/v${base}-rc${next}" >/dev/null; do
        next=$(( next + 1 ))
    done
    local tag="v${base}-rc${next}"

    printf '\n'
    printf '  %sAbout to publish a release candidate.%s\n' "$C_BOLD" "$C_RESET"
    printf '    tag     %s  (at %s)\n' "$tag" "$(git -C "$REPO" rev-parse --short HEAD)"
    printf '    image   %s:%s\n' "$IMAGE_REPO" "${tag#v}"
    printf '    chart   %s/hephaisto --version %s\n' "$CHART_REPO" "${tag#v}"
    printf '    release a GitHub prerelease, kept permanently\n'
    printf '\n'
    printf '  %sThis is public and effectively permanent.%s Use --nightly to test a build instead.\n' \
        "$C_YELLOW" "$C_RESET"
    printf '\n'

    if [ "${ASSUME_YES:-0}" != "1" ]; then
        local reply
        read -r -p "  type the tag to confirm: " reply
        [ "$reply" = "$tag" ] || die "not confirmed (got '${reply}')"
    fi

    git -C "$REPO" tag -a "$tag" -m "Release candidate $tag"
    git -C "$REPO" push origin "$tag" || { git -C "$REPO" tag -d "$tag"; die "could not push $tag"; }
    say "pushed $tag"

    local run_id="" deadline=$(( SECONDS + 120 ))
    printf '  %swaiting%s for release.yml ' "$C_DIM" "$C_RESET"
    while [ "$SECONDS" -lt "$deadline" ]; do
        run_id=$(gh -R "$GH_REPO" run list --workflow=release.yml --limit 5 \
                    --json databaseId,headBranch -q \
                    "[.[] | select(.headBranch == \"$tag\")] | .[0].databaseId // 0")
        [ "$run_id" != "0" ] && { printf ' %s\n' "$run_id"; break; }
        printf '.'; sleep 3; run_id=""
    done
    [ -n "$run_id" ] || die "$tag was pushed but release.yml never started"

    gh -R "$GH_REPO" run watch "$run_id" --exit-status \
        || die "release.yml failed for $tag: gh -R $GH_REPO run view $run_id --log-failed"

    VERSION="${tag#v}"
    pass "release candidate $tag published"
}

# ---------------------------------------------------------------------------------------
# Phase 2 - the artifacts are really there
# ---------------------------------------------------------------------------------------
# "The workflow went green" and "a consumer can pull it" are different events, and registry
# propagation sits between them. The harness installs from the registry, so this is the check
# that turns a confusing helm failure four minutes from now into a clear one here.
build_await_artifacts() {
    say "confirming $VERSION is pullable"

    wait_for "the chart to appear in the registry" 180 \
        helm show chart "$CHART_REPO/hephaisto" --version "$VERSION" \
        || { fail "chart is pullable" "$CHART_REPO/hephaisto:$VERSION not resolvable"; return 1; }
    pass "chart is pullable anonymously"

    wait_for "the image manifest to appear" 180 \
        docker manifest inspect "$IMAGE_REPO:$VERSION" \
        || { fail "image is pullable" "$IMAGE_REPO:$VERSION not resolvable"; return 1; }

    # Multi-arch matters here specifically: the kind node is arm64, and an amd64-only image
    # would run under emulation or not at all.
    local arches
    arches=$(docker manifest inspect "$IMAGE_REPO:$VERSION" \
             | jq -r '[.manifests[] | select(.platform.os != "unknown")
                       | "\(.platform.os)/\(.platform.architecture)"] | sort | join(",")')
    say "image platforms: $arches"
    [[ "$arches" == *"linux/arm64"* ]] \
        && pass "image publishes a linux/arm64 manifest" \
        || fail "image has no linux/arm64 manifest" "got: $arches"

    # The chart must name the image tag it was published alongside. release.yml and
    # nightly.yml both assert this, but they assert it about what they just pushed; this
    # asserts it about what is actually being installed.
    local named
    named=$(helm template hephaisto "$CHART_REPO/hephaisto" --version "$VERSION" \
                --namespace "$APP_NS" 2>/dev/null \
            | sed 's/"//g' | grep -oE "image: $IMAGE_REPO:[^ ]+" | head -1 | awk '{print $2}')
    [ "$named" = "$IMAGE_REPO:$VERSION" ] \
        && pass "the published chart points at the published image" \
        || fail "chart/image mismatch" "chart names '${named:-<none>}', expected $IMAGE_REPO:$VERSION"
}
