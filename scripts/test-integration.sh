#!/usr/bin/env bash
# Run the Postgres-backed integration suite.
#
# Separate from scripts/test.sh on purpose. That one is pure, offline and needs nothing
# installed; this one needs a real Postgres with pgvector, because the bugs it exists to
# catch are bugs in what EF Core and Postgres actually do together. Keeping them apart is
# what stops "the tests need Docker" leaking into the fast suite everyone runs.
#
#   ./scripts/dev-db.sh up          # once, to bring the database up
#   ./scripts/test-integration.sh
#
# ConnectionStrings__hephaisto is honoured if already exported, so CI can point this at a
# service container instead.
set -euo pipefail
cd "$(dirname "$0")/.."

: "${ConnectionStrings__hephaisto:=Host=localhost;Port=5433;Database=hephaisto;Username=hephaisto;Password=dev;Include Error Detail=true}"
export ConnectionStrings__hephaisto

exec dotnet run --project tests/Hephaisto.IntegrationTests/Hephaisto.IntegrationTests.csproj \
     -c "${1:-Debug}" -- "${@:2}"
