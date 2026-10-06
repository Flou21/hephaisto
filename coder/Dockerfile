# hephaisto-coder: the image a Kubernetes Job runs to let the Claude Agent SDK plan, and after
# human approval implement, a fix for one incident in one repository.
#
#   docker build -t hephaisto/coder:dev coder/
#   coder/scripts/test.sh --in-image
#
# It must run with a read-only root filesystem: everything writable lives under /work and /tmp,
# which the Job mounts as emptyDirs. Nothing secret is baked in - no token, no nuget.config;
# credentials arrive as env from a Secret.
#
# One image, three roles (CODEFIX_ROLE, see src/config.ts): a Job starts it as `prepare` (the
# GitHub and NuGet tokens, before the model exists), as `coder` (the model's credential, the
# agent, the build and tests) and, to implement, as `publish` (the GitHub token, after coder
# has ended). The container the model runs in is never handed a git or NuGet token. Without
# CODEFIX_ROLE all three run in this one process - for `docker run` and the tests, not for a Job.
#
# Base images are pinned by digest (multi-arch index digests). Bumping one is a deliberate edit.

ARG NODE_IMAGE=node:24-bookworm-slim@sha256:0e0ff40c39bc087845bfb27465a0df4ea419520094bc35842ff83dd8cbe6f9b6
ARG DOTNET_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29
# bun 1.3.14 is what the TR Nuxt repositories pin in packageManager
ARG BUN_IMAGE=oven/bun:1.3.14-debian@sha256:9dba1a1b43ce28c9d7931bfc4eb00feb63b0114720a0277a8f939ae4dfc9db6f
ARG TARGETARCH

FROM ${DOTNET_IMAGE} AS dotnet
FROM ${BUN_IMAGE} AS bun

# ---- gh, checksum-verified per architecture (ADD --checksum fails the build on a mismatch)
FROM ${NODE_IMAGE} AS gh-amd64
ADD --checksum=sha256:9bca2d1c16825f109907a23307628a2f0698fbf99662b73a5cf0b020293072b8 \
    https://github.com/cli/cli/releases/download/v2.101.0/gh_2.101.0_linux_amd64.tar.gz /tmp/gh.tgz
FROM ${NODE_IMAGE} AS gh-arm64
ADD --checksum=sha256:b57e8063f18862647c9d22727c32e9da1b963f8bf9db648fe123a6975695640f \
    https://github.com/cli/cli/releases/download/v2.101.0/gh_2.101.0_linux_arm64.tar.gz /tmp/gh.tgz
FROM gh-${TARGETARCH} AS gh
RUN mkdir /gh && tar -xzf /tmp/gh.tgz -C /gh --strip-components=1

# ---- build: dependencies from the lockfile, TypeScript compiled
FROM ${NODE_IMAGE} AS build
WORKDIR /opt/coder
COPY package.json package-lock.json ./
RUN set -eu; \
    npm ci --no-audit --no-fund --loglevel=warn; \
    # the claude-code package's postinstall puts the native binary in place; run it if npm skipped it
    node_modules/.bin/claude --version >/dev/null 2>&1 || node node_modules/@anthropic-ai/claude-code/install.cjs; \
    # lockstep: CLI 2.1.N and SDK 0.3.N ship together; refuse an image that mixes them
    cli=$(node -p "require('./node_modules/@anthropic-ai/claude-code/package.json').version"); \
    sdk=$(node -p "require('./node_modules/@anthropic-ai/claude-agent-sdk/package.json').version"); \
    if [ "${cli##*.}" != "${sdk##*.}" ]; then echo "claude-code $cli and claude-agent-sdk $sdk are not a lockstep pair" >&2; exit 1; fi; \
    HOME=/tmp node_modules/.bin/claude --version | grep -q "^$cli "; \
    # the SDK is always pointed at the claude-code binary above; its bundled copy of the same
    # version is 215 MB of dead weight
    rm -rf node_modules/@anthropic-ai/claude-agent-sdk-linux-* node_modules/@anthropic-ai/claude-agent-sdk-darwin-*
COPY tsconfig.json vitest.config.ts ./
COPY src ./src
RUN npx tsc -p tsconfig.json
COPY contracts ./contracts
COPY prompts ./prompts
COPY fake-scripts ./fake-scripts
COPY bin ./bin
COPY scripts ./scripts
COPY test ./test
RUN mkdir -p gh-shim && cp test/gh-shim/gh gh-shim/gh \
 && chmod 0755 bin/guard bin/askpass gh-shim/gh scripts/test.sh test/gh-shim/gh

# ---- runtime
FROM ${NODE_IMAGE}
# Stamped by CI (the MinVer version of the release/nightly); shown in every PR body's version line.
ARG VERSION=0.0.0-dev
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      git ca-certificates tini jq ripgrep python3 python3-venv \
      libicu72 libssl3 zlib1g libgssapi-krb5-2 tzdata \
 && rm -rf /var/lib/apt/lists/* \
 && groupadd --gid 64198 coder \
 && useradd --uid 64198 --gid 64198 --no-create-home --home-dir /work/home --shell /usr/sbin/nologin coder
COPY --from=dotnet /usr/share/dotnet /usr/share/dotnet
COPY --from=bun /usr/local/bin/bun /usr/local/bin/bun
COPY --from=gh /gh/bin/gh /usr/local/bin/gh
COPY --from=build /opt/coder /opt/coder
RUN ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet \
 && ln -s /usr/local/bin/bun /usr/local/bin/bunx \
 && ln -s /opt/coder/node_modules/@anthropic-ai/claude-code/bin/claude.exe /usr/local/bin/claude \
 && dotnet --version && bun --version && gh --version | head -1 && git --version && rg --version | head -1 \
 && python3 -m venv --help >/dev/null

ENV CODER_VERSION=${VERSION} \
    CLAUDE_CONFIG_DIR=/work/.claude \
    HOME=/work/home \
    DISABLE_AUTOUPDATER=1 \
    CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1 \
    CODEFIX_CLAUDE_EXECUTABLE=/opt/coder/node_modules/@anthropic-ai/claude-code/bin/claude.exe \
    DOTNET_ROOT=/usr/share/dotnet \
    DOTNET_CLI_USE_MSBUILD_SERVER=0 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    DOTNET_GENERATE_ASPNET_CERTIFICATE=false \
    NUGET_PACKAGES=/work/nuget/packages \
    GH_NO_UPDATE_NOTIFIER=1 \
    PYTHONDONTWRITEBYTECODE=1

USER 64198:64198
WORKDIR /work
ENTRYPOINT ["/usr/bin/tini", "--", "node", "/opt/coder/dist/main.js"]
