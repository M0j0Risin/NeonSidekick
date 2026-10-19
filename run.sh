#!/bin/sh
# Runs the app from source (JIT, no publish); any arguments are passed on (./run.sh --headless).
cd "$(dirname "$0")" || exit 1
exec dotnet run --project src/NeonSidekick -- "$@"
