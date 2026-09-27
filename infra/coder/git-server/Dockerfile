# A git server for the DEV cluster only: bare repositories over smart HTTP, push enabled.
#
# It stands in for GitHub so the code-fix stage can run end to end on the Mac Studio without a
# network, a token or a sandbox repository: the coder clones the fixture repo and dev-context
# from here, and the implement phase pushes its hephaisto/codefix-* branch back here.
#
# Seeded at BUILD time from git bundles that scripts/coder-git-seed.sh writes into seed/
# (gitignored). Pushes land in an emptyDir, so they last as long as the pod does - a restart
# returns every repository to its seeded state, which for a test fixture is a feature.
#
# Never deployed by the chart, never published. The Tiltfile builds it with disable_push.
FROM alpine:3.22

# git-http-backend lives in git-daemon on Alpine, not in git.
RUN apk add --no-cache git git-daemon lighttpd \
 && addgroup -S -g 64199 git \
 && adduser -S -D -H -u 64199 -G git -s /sbin/nologin git \
 # Push over HTTP needs either an authenticated REMOTE_USER or http.receivepack. There is no
 # authentication here at all - the NetworkPolicy beside the Deployment is the whole boundary,
 # and the only things it admits are coder pods and the egress proxy.
 && git config --system http.receivepack true \
 && git config --system http.uploadpack true \
    # The runner clones blobless and fetches the running image's commit by sha, both of which
    # GitHub allows and a stock git-http-backend refuses.
 && git config --system uploadpack.allowFilter true \
 && git config --system uploadpack.allowAnySHA1InWant true \
 && git config --system init.defaultBranch main \
 # The repositories are copied into an emptyDir at start, owned by the serving uid; this only
 # silences git's ownership check if a volume ever comes up owned by someone else.
 && git config --system --add safe.directory '*'

COPY seed/ /seed/
COPY lighttpd.conf /etc/lighttpd/lighttpd.conf
COPY index.cgi /srv/www/index.cgi
COPY entrypoint.sh /entrypoint.sh

# Bundle -> bare repository. `Owner__repo.bundle` becomes /srv/git-seed/Owner/repo.git, so the
# URL a coder clones has the same shape as the GitHub one it stands in for.
RUN set -eu; \
    for b in /seed/*.bundle; do \
      [ -e "$b" ] || { echo "no bundles in seed/ - run scripts/coder-git-seed.sh first" >&2; exit 1; }; \
      name=$(basename "$b" .bundle | sed 's|__|/|g'); \
      repo="/srv/git-seed/$name.git"; \
      mkdir -p "$(dirname "$repo")"; \
      git init -q --bare "$repo"; \
      git -C "$repo" fetch -q "$b" 'refs/heads/*:refs/heads/*' 'refs/tags/*:refs/tags/*'; \
      if git -C "$repo" show-ref -q --verify refs/heads/main; then git -C "$repo" symbolic-ref HEAD refs/heads/main; fi; \
      echo "seeded $name.git: $(git -C "$repo" for-each-ref --format='%(refname:short)' refs/heads | tr '\n' ' ')"; \
    done; \
    rm -rf /seed; \
    chmod 0755 /entrypoint.sh /srv/www/index.cgi; \
    chown -R git:git /srv/git-seed

USER 64199:64199
EXPOSE 8080
ENTRYPOINT ["/entrypoint.sh"]
