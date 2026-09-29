# pager: P48 | mcp signin | shared | with sign-in on, a login-provider token and a Secret token both get in
#
# Backlog #157, F1. Production runs with sign-in on AND a gateway's static token, and no other
# install covers that pair. The `signin` capability is a second, small install with the
# identity-provider stand-in (stage 7 of the v0.11.0 plan): PAGER_SIGNIN_MCP is its endpoint,
# PAGER_SIGNIN_TOKEN mints a signed token for a user and a role, PAGER_SIGNIN_STATIC is one of
# its Secret tokens.
#
#   PAGER_SIGNIN_TOKEN <user> <role> -> prints an access token

scenario() {
    local t who
    for v in PAGER_SIGNIN_MCP PAGER_SIGNIN_TOKEN PAGER_SIGNIN_STATIC; do
        [ -n "${!v:-}" ] || { fail "the sign-in install is described" "$v is not set"; return; }
    done

    t=$("$PAGER_SIGNIN_TOKEN" pager-person approver)
    who=$(mcp_json "$(mcp_call "$t" get_caller_identity '{}' "$PAGER_SIGNIN_MCP")")
    want "a login-provider token is its user" "$(jq -r '.name' <<<"$who")" = pager-person
    want "with the role its token carries" "$(jq -r '.role' <<<"$who")" = approver

    t=$("$PAGER_SIGNIN_TOKEN" pager-guest none)
    who=$(mcp_json "$(mcp_call "$t" get_caller_identity '{}' "$PAGER_SIGNIN_MCP")")
    want "a user without the approver role is a reader" "$(jq -r '.role' <<<"$who")" = reader

    who=$(mcp_json "$(mcp_call "$PAGER_SIGNIN_STATIC" get_caller_identity '{}' "$PAGER_SIGNIN_MCP")")
    want "a Secret token still gets in with sign-in on" "$(jq -r '.name' <<<"$who")" = mcp/pager-shared

    want "no token is still 401" "$(mcp_raw "$PAGER_SIGNIN_MCP")" = 401
}
