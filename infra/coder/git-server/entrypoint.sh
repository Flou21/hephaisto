#!/bin/sh
# Copy the seeded repositories into the writable emptyDir, then serve. The image's own copy is
# never written to, so a pod restart is a reset to the seeded state.
set -eu

if [ ! -d /srv/git ] || [ ! -w /srv/git ]; then
    echo "/srv/git must be a writable volume (an emptyDir in git-server.yaml)" >&2
    exit 1
fi

cp -a /srv/git-seed/. /srv/git/

for repo in $(find /srv/git -type d -name '*.git' -prune); do
    echo "serving ${repo#/srv/git/}: $(git -C "$repo" for-each-ref --format='%(refname:short)' refs/heads | tr '\n' ' ')"
done

# 3>&1: the access log writes to fd 3 - see lighttpd.conf.
exec lighttpd -D -f /etc/lighttpd/lighttpd.conf 3>&1
