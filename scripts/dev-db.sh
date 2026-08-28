#!/usr/bin/env bash
# Start a throwaway Postgres+pgvector for local development and apply the migrations.
#
# This is NOT the cluster's database - it is a plain container on port 5433, so it cannot
# disturb anything in k3s. `down` removes it and its data entirely.
#
# The image must be pullable. In a non-interactive shell docker's credential helper cannot
# reach the macOS keychain and the pull fails with "keychain cannot be accessed"; run
#   security -v unlock-keychain ~/Library/Keychains/login.keychain-db
# once in a normal terminal, or simply run this script interactively.
set -euo pipefail
cd "$(dirname "$0")/.."

NAME=watchtower-dev-pg
CONN="Host=localhost;Port=5433;Database=watchtower;Username=watchtower;Password=dev"

case "${1:-up}" in
  up)
    if ! docker inspect "$NAME" >/dev/null 2>&1; then
      docker run -d --name "$NAME" \
        -e POSTGRES_USER=watchtower -e POSTGRES_PASSWORD=dev -e POSTGRES_DB=watchtower \
        -p 5433:5432 pgvector/pgvector:pg17 >/dev/null
    fi
    docker start "$NAME" >/dev/null 2>&1 || true

    printf 'waiting for postgres'
    for _ in $(seq 1 60); do
      if docker exec "$NAME" pg_isready -U watchtower >/dev/null 2>&1; then break; fi
      printf '.'; sleep 1
    done
    echo

    # CREATE EXTENSION lives in the migration, but the role needs to exist first or the
    # audit-trail GRANT/REVOKE block quietly no-ops (it is wrapped in an IF EXISTS check).
    docker exec "$NAME" psql -U watchtower -d watchtower -v ON_ERROR_STOP=1 -c \
      "DO \$\$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='watchtower_app')
         THEN CREATE ROLE watchtower_app LOGIN PASSWORD 'dev'; END IF; END \$\$;" >/dev/null

    ConnectionStrings__watchtower="$CONN" \
      dotnet ef database update --project src/Watchtower.Agent --context WatchtowerDbContext

    echo
    echo "Ready. Run the agent against it with:"
    echo "  ConnectionStrings__watchtower='$CONN' \\"
    echo "  Kubernetes__RbacMode=WarnOnly ASPNETCORE_ENVIRONMENT=Development \\"
    echo "  dotnet run --project src/Watchtower.Agent"
    ;;
  down)
    docker rm -f "$NAME" >/dev/null 2>&1 || true
    echo "removed $NAME"
    ;;
  psql)
    exec docker exec -it "$NAME" psql -U watchtower -d watchtower
    ;;
  *)
    echo "usage: $0 [up|down|psql]" >&2; exit 2 ;;
esac
