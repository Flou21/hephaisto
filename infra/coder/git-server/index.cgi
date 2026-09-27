#!/bin/sh
# The listing a human reads: every repository and every branch, newest commit first, so a
# pushed hephaisto/codefix-* branch is visible without cloning anything.
#
#   kubectl -n hephaisto-coder port-forward svc/coder-git 8099:80 && curl -s localhost:8099/
printf 'Content-Type: text/plain; charset=utf-8\r\n\r\n'

find /srv/git -type d -name '*.git' -prune | sort | while read -r repo; do
    printf '%s\n' "${repo#/srv/git/}"
    git -C "$repo" for-each-ref --sort=-committerdate \
        --format='  %(refname:short)  %(objectname:short)  %(committerdate:iso8601)  %(subject)' \
        refs/heads
    printf '\n'
done
