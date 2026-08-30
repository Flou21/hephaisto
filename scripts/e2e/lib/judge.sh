#!/usr/bin/env bash
# The graded, non-gating half of phase 7.
#
# Structural assertions elsewhere in this harness are deterministic: an incident exists, it
# carries the right kind, its cost adds up. Whether the ROOT CAUSE is right is not a question
# a shell script can answer, and it is the question the repo's stated MVP bar is about -
# "measured over at least 10 seeded scenarios, the target is >= 7/10 correct root cause".
#
# infra/chaos/README.md exists partly to make that gradeable: every fixture file carries a
# header stating what it breaks and what the correct diagnosis is. So the grader has an answer
# key, and the job is comparison rather than judgement from scratch.
#
# THIS NEVER FAILS THE RUN. A judge is another language model having an opinion, and a release
# must not be blocked by one. It reports a score; the gating assertions are the deterministic
# ones.

# The expected root cause per fixture, taken from the fixture headers and the README table.
# Kept here rather than parsed out of the YAML so that a reworded comment cannot silently
# change the answer key.
# A case statement rather than `declare -A`, for the bash 3.2 reason in common.sh.
fixture_truth() {
    case "$1" in
        c1)  echo "The container is being OOMKilled: it allocates roughly 200Mi against a 64Mi memory limit, so the kernel kills it and Kubernetes restarts it repeatedly." ;;
        c2)  echo "The application exits deliberately at startup after failing to reach its database dependency at mongo.infra-db:27017, producing CrashLoopBackOff. The decisive evidence is a FATAL log line naming that host." ;;
        c3)  echo "The pod cannot be scheduled because it requests 500Gi of memory, which no node can satisfy. The cause appears only in a FailedScheduling event, not in any metric." ;;
        c4)  echo "The image tag does not exist: the pod references busybox:this-tag-does-not-exist, so the pull fails with ImagePullBackOff/ErrImagePull." ;;
        c5)  echo "The Job fails repeatedly and exceeds its backoffLimit of 2. Its logs name a failing migration step." ;;
        c7)  echo "A referenced Secret does not exist (c7-database-credentials), so the kubelet cannot construct the container environment and reports CreateContainerConfigError. This is NOT an image pull problem." ;;
        c8)  echo "The readiness probe alternates pass/fail on a 60s cycle, so the pod flaps in and out of the Service endpoints. The container is NOT crashing and restarts are zero - a Sev1 here would be a false positive." ;;
        c10) echo "The service returns 500s for about 15% of requests with an elevated p95 latency, while Kubernetes reports it perfectly healthy - the pod stays Ready and no event is emitted." ;;
        c11) echo "The container aborts at startup because it finds a stale generation counter on its persistent volume at /data/generation - the value is 1 and it requires 2 - so it exits 1 and the Deployment enters CrashLoopBackOff. The decisive evidence is a FATAL log line naming that generation." ;;
        *)   echo "" ;;
    esac
}

judge_run() {
    if [ "${RUN_JUDGE:-1}" != "1" ]; then
        skip "diagnosis grading" "--no-judge"
        return 0
    fi
    if [ "${LLM_AVAILABLE:-0}" != "1" ]; then
        skip "diagnosis grading" "no investigations to grade"
        return 0
    fi

    local details="$WORKDIR/details.jsonl"
    local scored=0 correct=0

    : > "$WORKDIR/judgements.jsonl"

    local f truth diagnosis result verdict reason ids id passed_over
    for f in $APPLIED; do
        truth=$(fixture_truth "$f")
        [ -n "$truth" ] || continue

        # The incidents chaos_assert_detection matched for this fixture, in the order it
        # matched them. This function used to resolve the fixture itself, against
        # target.name and the raw fixture id - which is not the string detection matches on
        # (fixture_target), so c10 never matched here at all and was reported ungraded while
        # its diagnosis existed. Grading a row this file resolved independently is #37.
        # `|| true` is load-bearing: run.sh is `set -Eeuo pipefail`, and awk on a file that
        # does not exist (an --only run that skipped detection) exits 2, which would take the
        # whole suite down in the reporter rather than skipping one grade.
        ids=$(awk -F'\t' -v f="$f" '$1 == f {print $2}' \
              "$WORKDIR/fixture-incidents.tsv" 2>/dev/null || true)

        # The primary hypothesis plus its evidence excerpts - what a human would read.
        #
        # First incident that carries one, rather than first incident: a fixture routinely
        # opens two, and only one of them holds the diagnosis. Passing over an empty row is
        # not the same as the fixture having produced nothing, and the old code could not
        # tell the two apart - which is the other half of how the denominator shrank.
        diagnosis=""
        passed_over=0
        for id in $ids; do
            diagnosis=$(jq -r --arg id "$id" '
                select((.id // "") == $id)
                | [.investigations[]?.findings[]? | select(.isPrimary)]
                | .[0] // empty
                | "HYPOTHESIS: \(.hypothesis)\nEVIDENCE: " +
                  ([.evidence[]? | .excerpt] | join(" | "))' "$details" | head -c 4000)

            [ -n "$diagnosis" ] && break
            passed_over=$(( passed_over + 1 ))
        done

        if [ -z "$diagnosis" ]; then
            # Now an honest statement about the fixture rather than about the row that was
            # picked. The two cases are different and were previously the same sentence:
            # nothing matched at all, versus every incident it opened was checked and none
            # carried a primary finding.
            if [ "$passed_over" -eq 0 ]; then
                printf '  %sskip%s  grade %s -- detection matched no incident for it\n' \
                    "$C_YELLOW" "$C_RESET" "$f"
            else
                printf '  %sskip%s  grade %s -- no primary finding in any of its %s incident(s)\n' \
                    "$C_YELLOW" "$C_RESET" "$f" "$passed_over"
            fi
            continue
        fi

        if [ "$passed_over" -gt 0 ]; then
            say "graded $f on incident $id (passed over $passed_over with no primary finding)"
        fi

        result=$(judge_ask "$truth" "$diagnosis") || {
            warn "judge call failed for $f"
            continue
        }

        verdict=$(jq -r '.correct // false' <<<"$result" 2>/dev/null || echo false)
        reason=$(jq -r '.reason // ""'    <<<"$result" 2>/dev/null || echo "")

        scored=$(( scored + 1 ))
        if [ "$verdict" = "true" ]; then
            correct=$(( correct + 1 ))
            printf '  %s++%s    %s: correct -- %s\n' "$C_GREEN" "$C_RESET" "$f" "${reason:0:100}"
        else
            printf '  %s--%s    %s: incorrect -- %s\n' "$C_RED" "$C_RESET" "$f" "${reason:0:100}"
        fi

        jq -cn --arg f "$f" --arg v "$verdict" --arg r "$reason" --arg d "$diagnosis" \
            '{fixture:$f, correct:($v == "true"), reason:$r, diagnosis:$d}' \
            >> "$WORKDIR/judgements.jsonl"
    done

    JUDGE_SCORE="$correct/$scored"

    if [ "$scored" -eq 0 ]; then
        skip "diagnosis grading" "nothing could be graded"
        return 0
    fi

    # Reported, never gating. Recorded as a pass at any score so that a run is not failed by
    # a second model's opinion; the number is in the summary for a human to read.
    record pass "$CURRENT_PHASE" "root cause graded $correct/$scored" \
        "reported only - the MVP bar is >= 7/10 over >= 10 scenarios"

    say "root cause: $correct of $scored correct (repo target is >= 7/10 across >= 10 scenarios)"
}

# One Gemini call, structured output, no tools. Deliberately a different and cheaper model
# than the agent's: a judge that is the same model reasoning about its own output is closer to
# self-assessment than to review.
judge_ask() {
    local truth="$1" diagnosis="$2"
    local model="${JUDGE_MODEL:-gemini-3.7-flash}"

    local payload
    payload=$(jq -n --arg t "$truth" --arg d "$diagnosis" '{
        contents: [{
            role: "user",
            parts: [{ text: (
                "You are grading an SRE agent'"'"'s incident diagnosis against a known-correct answer.\n\n" +
                "KNOWN CORRECT ANSWER:\n" + $t + "\n\n" +
                "THE AGENT SAID:\n" + $d + "\n\n" +
                "Did the agent identify the same underlying root cause? Judge the CAUSE, not the wording, " +
                "and not whether it restated the Kubernetes symptom. Restating the symptom " +
                "(\"the pod is in CrashLoopBackOff\") without identifying why is NOT correct. " +
                "Answer strictly as JSON: {\"correct\": true|false, \"reason\": \"<one sentence>\"}"
            )}]
        }],
        generationConfig: { temperature: 0, responseMimeType: "application/json" }
    }')

    local response
    response=$(curl -sS --max-time 60 \
        "https://generativelanguage.googleapis.com/v1beta/models/${model}:generateContent" \
        -H "x-goog-api-key: ${HEPHAISTO_GEMINI_API_KEY}" \
        -H 'Content-Type: application/json' \
        -d "$payload" 2>/dev/null) || return 1

    jq -r '.candidates[0].content.parts[0].text // empty' <<<"$response" 2>/dev/null
}
