#!/usr/bin/env bash
# Mints a sign-in token from the identity-provider stand-in, for P48.
#   signin-token.sh <user> <approver|reader|none>   -> the access token on stdout
# PAGER_STANDIN is the stand-in's address, as the pager suite has it.
set -euo pipefail
case "${2:-none}" in
    approver) role=hephaisto-approver ;;
    *)        role=none ;;
esac
curl -sS --max-time 10 "${PAGER_STANDIN:?}/oidc/token?sub=$1&role=$role" | jq -r .access_token
