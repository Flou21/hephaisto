#!/usr/bin/env bash
# Run the test suite.
#
# Use this rather than `dotnet test`. On this toolchain (.NET 10.0.400, xunit.v3 4.0.0,
# Microsoft.Testing.Platform 2.3.3) `dotnet test` cannot run these tests:
#
#   - without a global.json "test.runner" entry it hard-errors with
#     "Testing with VSTest target is no longer supported by Microsoft.Testing.Platform";
#   - with one, it launches the test executable in `--server dotnettestcli` mode, which
#     exits after ~200ms reporting "Zero tests ran" (exit code 5).
#
# Verified against xunit.v3 3.2.2 and 4.0.0, with and without the VSTest adapter, and with
# and without central transitive pinning - the behaviour is identical, so it is xunit.v3's
# server-mode support and not this repo's configuration.
#
# xunit v3 test projects are executables, and running one directly works perfectly: all
# tests discover and run, and the exit code is honest (0 pass, non-zero otherwise), so this
# is safe for CI. Revisit `dotnet test` after a xunit.v3 bump.
set -euo pipefail
cd "$(dirname "$0")/.."
exec dotnet run --project tests/Hephaisto.Tests/Hephaisto.Tests.csproj -c "${1:-Debug}" -- "${@:2}"
